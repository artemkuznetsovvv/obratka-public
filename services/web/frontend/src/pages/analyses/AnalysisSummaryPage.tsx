import { useMemo, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import {
  AlertTriangle,
  Building2,
  CalendarRange,
  Layers,
  MapPin,
  Pencil,
  Rocket,
  Tags,
} from 'lucide-react'
import { AppLayout } from '@/layouts/AppLayout'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { companiesApi, type LogicalBranchDto } from '@/api/companies'
import { cn } from '@/lib/utils'
import { AnalysisStepper } from './AnalysisStepper'
import {
  clearWizardState,
  defaultWizardState,
  formatPeriodSummary,
  loadWizardState,
  type WizardState,
} from './wizardState'

const SOURCE_META: Record<string, { label: string; color: string }> = {
  '2gis': { label: '2ГИС', color: 'bg-emerald-100 text-emerald-700' },
  yandex: { label: 'Яндекс.Карты', color: 'bg-amber-100 text-amber-700' },
  google: { label: 'Google Maps', color: 'bg-blue-100 text-blue-700' },
}

export default function AnalysisSummaryPage() {
  const { companyId } = useParams<{ companyId: string }>()
  const navigate = useNavigate()
  const [launched, setLaunched] = useState(false)

  const wizard: WizardState = useMemo(
    () => (companyId ? loadWizardState(companyId) : null) ?? defaultWizardState(),
    [companyId],
  )

  const companyQuery = useQuery({
    queryKey: ['company', companyId],
    queryFn: () => companiesApi.get(companyId!),
    enabled: !!companyId,
  })

  const groupsQuery = useQuery({
    queryKey: ['company', companyId, 'groups'],
    queryFn: () => companiesApi.listGroups(companyId!),
    enabled: !!companyId,
  })

  const activeGroups = useMemo(
    () =>
      (groupsQuery.data ?? []).filter(
        (lb) => lb.isSelected && lb.providers.some((p) => p.isEnabled),
      ),
    [groupsQuery.data],
  )

  const activeProvidersCount = useMemo(
    () =>
      activeGroups.reduce((acc, lb) => acc + lb.providers.filter((p) => p.isEnabled).length, 0),
    [activeGroups],
  )

  const groupedByCity = useMemo(() => groupByCity(activeGroups), [activeGroups])

  const reachedFromStep2 = (groupsQuery.data ?? []).length > 0

  const onLaunch = () => {
    if (!companyId) return
    // Real PG launch is wired up in a follow-up. For now: clear wizard state and
    // park the user on history where running/completed jobs will eventually live.
    setLaunched(true)
    clearWizardState(companyId)
    navigate('/history')
  }

  return (
    <AppLayout
      breadcrumbs={[
        { label: 'Главная', to: '/' },
        { label: 'Новый анализ', to: '/analyses/new' },
        { label: 'Запуск' },
      ]}
    >
      <div className="max-w-4xl mx-auto">
        <AnalysisStepper current={3} />

        <div className="mb-6">
          <h1 className="text-h1 text-text-primary mb-2">Проверьте параметры запуска</h1>
          <p className="text-body text-text-secondary">
            Это сводка того, что система соберёт и отправит в LLM. После запуска параметры менять
            нельзя — придётся создавать новый анализ.
          </p>
        </div>

        {companyQuery.isLoading || groupsQuery.isLoading ? (
          <Card className="p-8 text-text-secondary">Загружаем данные анализа…</Card>
        ) : companyQuery.isError ? (
          <Card className="p-8 text-destructive">
            Не удалось загрузить компанию: {(companyQuery.error as Error).message}
          </Card>
        ) : groupsQuery.isError ? (
          <Card className="p-8 text-destructive">
            Не удалось загрузить группировку филиалов: {(groupsQuery.error as Error).message}
          </Card>
        ) : (
          <>
            {!reachedFromStep2 && (
              <div className="mb-6 rounded-2xl border border-amber-200 bg-amber-50 px-5 py-4 text-sm text-amber-900 flex items-start gap-3">
                <AlertTriangle size={18} className="mt-0.5 shrink-0" />
                <div>
                  <div className="font-medium mb-0.5">Сначала сгруппируйте филиалы.</div>
                  <div className="text-amber-800">
                    Группировка карточек по физическим точкам — обязательный шаг.{' '}
                    <button
                      type="button"
                      onClick={() => navigate(`/analyses/new/${companyId}/branches`)}
                      className="underline font-medium"
                    >
                      Вернитесь на шаг 2
                    </button>
                    .
                  </div>
                </div>
              </div>
            )}

            <div className="space-y-4">
              {/* Block 1: company */}
              <SummaryBlock
                icon={<Building2 size={18} />}
                title="Компания"
                editTo={`/analyses/new?from=${companyId}`}
              >
                <div className="text-sm">
                  <div className="font-medium text-text-primary">{companyQuery.data?.name}</div>
                  {(companyQuery.data?.category || companyQuery.data?.subcategory) && (
                    <div className="text-text-tertiary mt-0.5">
                      {[companyQuery.data?.category, companyQuery.data?.subcategory]
                        .filter(Boolean)
                        .join(' · ')}
                    </div>
                  )}
                </div>
                {companyQuery.data?.cities && companyQuery.data.cities.length > 0 && (
                  <div className="mt-3 flex flex-wrap gap-2">
                    {companyQuery.data.cities.map((c) => (
                      <span
                        key={c}
                        className="inline-flex items-center gap-1 px-2.5 py-1 rounded-full bg-page-bg text-xs text-text-secondary"
                      >
                        <MapPin size={12} />
                        {c}
                      </span>
                    ))}
                  </div>
                )}
              </SummaryBlock>

              {/* Block 2: period */}
              <SummaryBlock
                icon={<CalendarRange size={18} />}
                title="Период анализа"
                editTo={`/analyses/new?from=${companyId}`}
              >
                <div className="text-sm font-medium text-text-primary">
                  {formatPeriodSummary(wizard.period)}
                </div>
                {wizard.period.kind === 'since-beginning' && (
                  <div className="text-xs text-text-tertiary mt-1">
                    Соберём все доступные отзывы — это может занять больше времени.
                  </div>
                )}
              </SummaryBlock>

              {/* Block 3: sources */}
              <SummaryBlock
                icon={<Layers size={18} />}
                title="Источники"
                editTo={`/analyses/new?from=${companyId}`}
              >
                <div className="flex flex-wrap gap-2">
                  {wizard.sources.map((s) => {
                    const meta = SOURCE_META[s] ?? { label: s, color: 'bg-page-bg text-text-secondary' }
                    return (
                      <span
                        key={s}
                        className={cn(
                          'inline-flex items-center gap-1.5 px-3 py-1 rounded-full text-xs font-medium',
                          meta.color,
                        )}
                      >
                        {meta.label}
                      </span>
                    )
                  })}
                </div>
              </SummaryBlock>

              {/* Block 4: physical branches with providers */}
              <SummaryBlock
                icon={<Tags size={18} />}
                title={`Филиалы · ${activeGroups.length} (${activeProvidersCount} карточек)`}
                editTo={`/analyses/new/${companyId}/branches`}
              >
                {activeGroups.length === 0 ? (
                  <div className="text-sm text-text-tertiary">
                    Нет активных филиалов. Вернитесь на шаг 2 и выберите хотя бы один блок.
                  </div>
                ) : (
                  <div className="space-y-4">
                    {groupedByCity.map((cg) => (
                      <div key={cg.city}>
                        <div className="text-xs text-text-tertiary uppercase tracking-wide mb-2 flex items-center gap-1">
                          <MapPin size={12} />
                          {cg.city}
                        </div>
                        <div className="space-y-2">
                          {cg.branches.map((lb) => (
                            <div
                              key={lb.id}
                              className="rounded-xl border border-border-subtle bg-card/60 px-4 py-3"
                            >
                              <div className="text-sm font-medium text-text-primary">{lb.name}</div>
                              {lb.address && (
                                <div className="text-xs text-text-tertiary mt-0.5">{lb.address}</div>
                              )}
                              <div className="mt-2 flex flex-wrap gap-1.5">
                                {lb.providers
                                  .filter((p) => p.isEnabled)
                                  .map((p) => {
                                    const meta =
                                      SOURCE_META[p.source] ?? {
                                        label: p.source,
                                        color: 'bg-page-bg text-text-secondary',
                                      }
                                    return (
                                      <span
                                        key={p.branchId}
                                        className={cn(
                                          'inline-flex items-center px-2 py-0.5 rounded text-[11px] font-semibold',
                                          meta.color,
                                        )}
                                      >
                                        {meta.label}
                                      </span>
                                    )
                                  })}
                              </div>
                            </div>
                          ))}
                        </div>
                      </div>
                    ))}
                  </div>
                )}
              </SummaryBlock>
            </div>

            <div className="mt-8 flex items-center justify-end gap-3">
              <Button
                variant="outline"
                onClick={() => navigate(`/analyses/new/${companyId}/branches`)}
              >
                Назад
              </Button>
              <Button
                onClick={onLaunch}
                disabled={launched || activeGroups.length === 0}
                className="gap-2"
              >
                <Rocket size={18} />
                {launched ? 'Запускаем…' : 'Запустить анализ'}
              </Button>
            </div>
          </>
        )}
      </div>
    </AppLayout>
  )
}

function SummaryBlock({
  icon,
  title,
  editTo,
  children,
}: {
  icon: React.ReactNode
  title: string
  editTo: string
  children: React.ReactNode
}) {
  const navigate = useNavigate()
  return (
    <Card className="p-5">
      <header className="flex items-center justify-between mb-3">
        <div className="flex items-center gap-2 text-h3 text-text-primary">
          <span className="text-brand">{icon}</span>
          {title}
        </div>
        <button
          type="button"
          onClick={() => navigate(editTo)}
          className="inline-flex items-center gap-1.5 text-sm text-brand hover:text-brand-hover transition-colors"
        >
          <Pencil size={14} />
          Изменить
        </button>
      </header>
      {children}
    </Card>
  )
}

interface CityGroup {
  city: string
  branches: LogicalBranchDto[]
}

function groupByCity(branches: LogicalBranchDto[]): CityGroup[] {
  const map = new Map<string, LogicalBranchDto[]>()
  for (const b of branches) {
    if (!map.has(b.city)) map.set(b.city, [])
    map.get(b.city)!.push(b)
  }
  return Array.from(map.entries())
    .sort(([a], [b]) => a.localeCompare(b, 'ru'))
    .map(([city, list]) => ({
      city,
      branches: list.sort((a, b) => a.name.localeCompare(b.name, 'ru')),
    }))
}

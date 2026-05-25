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
import { companiesApi, type CompanyBranchDto } from '@/api/companies'
import { cn } from '@/lib/utils'
import { AnalysisStepper } from './AnalysisStepper'
import {
  clearWizardState,
  defaultWizardState,
  formatPeriodSummary,
  loadWizardState,
  type WizardState,
} from './wizardState'

// Минимум отзывов для разумного LLM-анализа (ТЗ 2.7). Финальная проверка
// по факту сбора сейчас на стороне Processing Gateway; этот порог — только
// раннее предупреждение, чтобы юзер успел расширить период/источники.
const MIN_REVIEWS_FOR_ANALYSIS = 10

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

  const branchesQuery = useQuery({
    queryKey: ['company', companyId, 'branches'],
    queryFn: () => companiesApi.listBranches(companyId!),
    enabled: !!companyId,
  })

  // listBranches returns the whole company catalog (including past unselected). The set
  // confirmed on step 2 lives in wizardState.selectedBranchIds. If the user deep-linked
  // here without going through step 2, fall back to «every branch» so they can at least see
  // the shape, and we render a warning above the actions.
  const selectedBranches = useMemo<CompanyBranchDto[]>(() => {
    const all = branchesQuery.data ?? []
    const ids = wizard.selectedBranchIds
    if (!ids || ids.length === 0) return all
    const set = new Set(ids)
    return all.filter((b) => set.has(b.id))
  }, [branchesQuery.data, wizard.selectedBranchIds])

  const grouped = useMemo(() => groupByCityAndSource(selectedBranches), [selectedBranches])

  // Heuristic quota pre-check: sum reviewCount across selected branches. This is a rough
  // estimate (it's the source's «rating count», not «reviews within period»). Period filter
  // happens during real collection — this is just to warn early.
  const estimatedTotalReviews = selectedBranches.reduce(
    (acc, b) => acc + (b.reviewCount ?? 0),
    0,
  )
  const quotaLow = estimatedTotalReviews < MIN_REVIEWS_FOR_ANALYSIS

  const reachedFromStep2 = !!wizard.selectedBranchIds && wizard.selectedBranchIds.length > 0

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

        {companyQuery.isLoading || branchesQuery.isLoading ? (
          <Card className="p-8 text-text-secondary">Загружаем данные анализа…</Card>
        ) : companyQuery.isError ? (
          <Card className="p-8 text-destructive">
            Не удалось загрузить компанию: {(companyQuery.error as Error).message}
          </Card>
        ) : branchesQuery.isError ? (
          <Card className="p-8 text-destructive">
            Не удалось загрузить выбранные филиалы: {(branchesQuery.error as Error).message}
          </Card>
        ) : (
          <>
            {!reachedFromStep2 && (
              <div className="mb-6 rounded-2xl border border-amber-200 bg-amber-50 px-5 py-4 text-sm text-amber-900 flex items-start gap-3">
                <AlertTriangle size={18} className="mt-0.5 shrink-0" />
                <div>
                  <div className="font-medium mb-0.5">Вы пропустили шаг выбора филиалов.</div>
                  <div className="text-amber-800">
                    Покажем все сохранённые карточки компании. Чтобы уточнить выбор —{' '}
                    <button
                      type="button"
                      onClick={() => navigate(`/analyses/new/${companyId}/branches`)}
                      className="underline font-medium"
                    >
                      вернитесь на шаг 2
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

              {/* Block 4: branches */}
              <SummaryBlock
                icon={<Tags size={18} />}
                title={`Филиалы · ${selectedBranches.length}`}
                editTo={`/analyses/new/${companyId}/branches`}
              >
                {selectedBranches.length === 0 ? (
                  <div className="text-sm text-text-tertiary">
                    Не выбрано ни одной карточки. Вернитесь на шаг 2 и отметьте филиалы.
                  </div>
                ) : (
                  <div className="space-y-3">
                    {grouped.map((cityGroup) => (
                      <div key={cityGroup.city}>
                        <div className="text-xs text-text-tertiary uppercase tracking-wide mb-1.5 flex items-center gap-1">
                          <MapPin size={12} />
                          {cityGroup.city}
                        </div>
                        <div className="space-y-1.5">
                          {cityGroup.sources.map((sg) => {
                            const meta =
                              SOURCE_META[sg.source] ?? {
                                label: sg.source,
                                color: 'bg-page-bg text-text-secondary',
                              }
                            return (
                              <div
                                key={sg.source}
                                className="flex items-start gap-2 text-sm"
                              >
                                <span
                                  className={cn(
                                    'inline-flex items-center justify-center px-2 py-0.5 rounded text-[11px] font-semibold shrink-0 mt-0.5',
                                    meta.color,
                                  )}
                                >
                                  {meta.label}
                                </span>
                                <div className="flex-1 text-text-secondary">
                                  {sg.items.map((it) => it.name).join(', ')}
                                </div>
                              </div>
                            )
                          })}
                        </div>
                      </div>
                    ))}
                  </div>
                )}
              </SummaryBlock>

              {/* Block 5: estimate / quota warning */}
              {selectedBranches.length > 0 && (
                <Card
                  className={cn(
                    'p-5',
                    quotaLow ? 'border-amber-200 bg-amber-50' : 'border-emerald-200 bg-emerald-50/60',
                  )}
                >
                  <div className="flex items-start gap-3">
                    {quotaLow ? (
                      <AlertTriangle size={18} className="text-amber-700 shrink-0 mt-0.5" />
                    ) : (
                      <Rocket size={18} className="text-emerald-700 shrink-0 mt-0.5" />
                    )}
                    <div className="text-sm">
                      <div
                        className={cn(
                          'font-medium',
                          quotaLow ? 'text-amber-900' : 'text-emerald-900',
                        )}
                      >
                        Ожидаемый объём данных: ~{estimatedTotalReviews.toLocaleString('ru-RU')}{' '}
                        отзывов
                      </div>
                      <div
                        className={cn(
                          'mt-1',
                          quotaLow ? 'text-amber-800' : 'text-emerald-800',
                        )}
                      >
                        {quotaLow ? (
                          <>
                            Минимум для качественного анализа — {MIN_REVIEWS_FOR_ANALYSIS} отзывов.
                            По выбранным филиалам ожидаемого объёма недостаточно. Расширьте период,
                            добавьте источники или филиалы — либо запустите как есть, если хотите
                            проверить «есть ли вообще данные».
                          </>
                        ) : (
                          <>
                            Это оценка по общему счётчику отзывов на карточках; реальное число за
                            выбранный период станет известно после сбора.
                          </>
                        )}
                      </div>
                    </div>
                  </div>
                </Card>
              )}
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
                disabled={launched || selectedBranches.length === 0}
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
  sources: Array<{ source: string; items: CompanyBranchDto[] }>
}

function groupByCityAndSource(branches: CompanyBranchDto[]): CityGroup[] {
  const map = new Map<string, Map<string, CompanyBranchDto[]>>()
  for (const b of branches) {
    if (!map.has(b.city)) map.set(b.city, new Map())
    const inner = map.get(b.city)!
    if (!inner.has(b.source)) inner.set(b.source, [])
    inner.get(b.source)!.push(b)
  }
  return Array.from(map.entries())
    .sort(([a], [b]) => a.localeCompare(b, 'ru'))
    .map(([city, inner]) => ({
      city,
      sources: Array.from(inner.entries())
        .sort(([a], [b]) => a.localeCompare(b))
        .map(([source, items]) => ({
          source,
          items: items.sort((a, b) => a.name.localeCompare(b.name, 'ru')),
        })),
    }))
}

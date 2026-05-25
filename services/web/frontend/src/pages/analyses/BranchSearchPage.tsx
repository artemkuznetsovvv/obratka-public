import { useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import {
  ArrowRight,
  CalendarRange,
  Check,
  ChevronDown,
  ExternalLink,
  Loader2,
  MapPin,
  Pencil,
  Plus,
  Sparkles,
  Star,
  Ungroup,
  Unlink,
  X,
} from 'lucide-react'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { AppLayout } from '@/layouts/AppLayout'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { companiesApi, type BranchSearchResultItem } from '@/api/companies'
import { describeApiError } from '@/api/errors'
import { cn } from '@/lib/utils'
import { AnalysisStepper } from './AnalysisStepper'
import {
  defaultWizardState,
  formatPeriodSummary,
  formatSourcesSummary,
  loadWizardState,
  saveWizardState,
  type WizardState,
} from './wizardState'
import {
  attachToGroup,
  buildSavePayload,
  type CitiesState,
  type CityLayout,
  type CityState,
  type ClientGroup,
  countActiveProviders,
  createGroupFromUnmatched,
  detachProvider,
  ignoreUnmatched,
  layoutFromSearchResponse,
  setGroupName,
  setGroupSelected,
  setProviderEnabled,
  ungroup,
  unignore,
} from './branchGroupingState'

const SOURCE_META: Record<string, { label: string; color: string }> = {
  '2gis': { label: '2ГИС', color: 'bg-emerald-100 text-emerald-700' },
  yandex: { label: 'Яндекс.Карты', color: 'bg-amber-100 text-amber-700' },
  google: { label: 'Google Maps', color: 'bg-blue-100 text-blue-700' },
}

export default function BranchSearchPage() {
  const { companyId } = useParams<{ companyId: string }>()
  const navigate = useNavigate()

  const companyQuery = useQuery({
    queryKey: ['company', companyId],
    queryFn: () => companiesApi.get(companyId!),
    enabled: !!companyId,
  })

  const [cityStates, setCityStates] = useState<CitiesState>({})
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)

  const wizard: WizardState = useMemo(
    () => (companyId ? loadWizardState(companyId) : null) ?? defaultWizardState(),
    [companyId],
  )

  const cities = companyQuery.data?.cities ?? []
  const allFinished = cities.length > 0 && cities.every((c) => {
    const s = cityStates[c]?.status
    return s === 'done' || s === 'error'
  })
  const hasMultipleCities = cities.length > 1
  const currentSearchingCity = cities.find((c) => cityStates[c]?.status === 'searching')
  const activeProvidersCount = countActiveProviders(cityStates)

  useEffect(() => {
    if (!companyId || cities.length === 0) return
    let cancelled = false
    const initial: CitiesState = {}
    for (const c of cities) initial[c] = { status: 'pending' }
    setCityStates(initial)

    ;(async () => {
      for (const city of cities) {
        if (cancelled) return
        setCityStates((prev) => ({ ...prev, [city]: { status: 'searching' } }))
        try {
          const response = await companiesApi.search(companyId, city, wizard.sources)
          if (cancelled) return
          setCityStates((prev) => ({
            ...prev,
            [city]: { status: 'done', layout: layoutFromSearchResponse(response) },
          }))
        } catch (err) {
          if (cancelled) return
          const message = describeApiError(err, 'Не удалось выполнить поиск')
          setCityStates((prev) => ({ ...prev, [city]: { status: 'error', message } }))
        }
      }
    })()

    return () => {
      cancelled = true
    }
  }, [companyId, cities.join('|'), wizard.sources.join('|')])

  const updateLayout = (city: string, updater: (l: CityLayout) => CityLayout) => {
    setCityStates((prev) => {
      const s = prev[city]
      if (s?.status !== 'done') return prev
      return { ...prev, [city]: { status: 'done', layout: updater(s.layout) } }
    })
  }

  const onNext = async () => {
    if (!companyId) return
    setSaveError(null)
    if (activeProvidersCount === 0) {
      setSaveError('Выберите хотя бы одну карточку, чтобы продолжить')
      return
    }
    setSaving(true)
    try {
      const payload = buildSavePayload(cityStates, cities)
      await companiesApi.saveBranchGroups(companyId, payload)
      // selectedBranchIds в wizard-state нужен step-3 чтобы отрисовать сводку.
      const selectedIds: string[] = []
      for (const g of payload.groups) {
        if (!g.isSelected) continue
        for (const p of g.providers) if (p.isEnabled) selectedIds.push(p.branchId)
      }
      saveWizardState(companyId, { ...wizard, selectedBranchIds: selectedIds })
      navigate(`/analyses/new/${companyId}/summary`)
    } catch (err) {
      setSaveError(describeApiError(err, 'Не удалось сохранить выбор'))
    } finally {
      setSaving(false)
    }
  }

  return (
    <AppLayout
      breadcrumbs={[
        { label: 'Главная', to: '/' },
        { label: 'Новый анализ', to: '/analyses/new' },
        { label: 'Выбор источников' },
      ]}
    >
      <div className="max-w-4xl mx-auto">
        <AnalysisStepper current={2} />

        <div className="mb-6">
          <h1 className="text-h1 text-text-primary mb-2">Найденные филиалы</h1>
          <p className="text-body text-text-secondary">
            Карточки с разных источников сгруппированы в физические филиалы. Внутри каждого
            блока — отдельные чекбоксы по провайдерам, можно отключить лишний источник или
            весь блок. Если автогруппировка ошиблась — используйте «Разгруппировать» или
            раздел «Не удалось сгруппировать».
          </p>
        </div>

        <Card className="mb-6 px-5 py-4 flex flex-col sm:flex-row sm:items-center gap-3 sm:gap-6">
          <div className="flex items-start gap-3">
            <CalendarRange size={18} className="text-brand mt-0.5 shrink-0" />
            <div className="min-w-0">
              <div className="text-xs text-text-tertiary uppercase tracking-wide">Период анализа</div>
              <div className="text-sm font-medium text-text-primary truncate">
                {formatPeriodSummary(wizard.period)}
              </div>
            </div>
          </div>
          <div className="hidden sm:block h-8 w-px bg-border-subtle" />
          <div className="min-w-0 flex-1">
            <div className="text-xs text-text-tertiary uppercase tracking-wide">Источники</div>
            <div className="text-sm font-medium text-text-primary truncate">
              {formatSourcesSummary(wizard.sources)}
            </div>
          </div>
          <button
            type="button"
            onClick={() => navigate(`/analyses/new?from=${companyId}`)}
            className="inline-flex items-center gap-1.5 text-sm text-brand hover:text-brand-hover transition-colors self-start sm:self-auto"
          >
            <Pencil size={14} />
            Изменить
          </button>
        </Card>

        {companyQuery.isLoading && (
          <Card className="p-8 text-text-secondary">Загрузка анкеты компании…</Card>
        )}
        {companyQuery.isError && (
          <Card className="p-8 text-destructive">
            Не удалось загрузить компанию: {(companyQuery.error as Error).message}
          </Card>
        )}

        {companyQuery.data && (
          <>
            <div className="space-y-8">
              {cities.map((city) => (
                <CitySection
                  key={city}
                  city={city}
                  state={cityStates[city] ?? { status: 'pending' }}
                  onUpdate={(updater) => updateLayout(city, updater)}
                />
              ))}
            </div>

            {hasMultipleCities && !allFinished && currentSearchingCity && (
              <div className="mt-8 flex items-center gap-3 rounded-2xl border border-border-subtle bg-card px-5 py-4 shadow-sm">
                <Loader2 className="text-brand animate-spin" size={20} />
                <div className="text-sm text-text-primary">
                  Ищем организации в городе <span className="font-semibold">{currentSearchingCity}</span>…
                </div>
              </div>
            )}

            {saveError && (
              <div className="mt-6 rounded-lg border border-destructive/30 bg-destructive/5 px-4 py-3 text-sm text-destructive">
                {saveError}
              </div>
            )}

            <div className="mt-8 flex flex-col-reverse items-stretch gap-3 sm:flex-row sm:items-center sm:justify-between">
              <div className="rounded-full bg-state-active-bg border border-brand/20 text-brand px-4 py-1.5 text-sm font-medium self-start sm:self-auto">
                Активных карточек: {activeProvidersCount}
              </div>
              <div className="flex items-center justify-end gap-3">
                <Button
                  variant="outline"
                  onClick={() => navigate(`/analyses/new?from=${companyId}`)}
                >
                  Назад
                </Button>
                <Button
                  onClick={onNext}
                  disabled={!allFinished || saving || activeProvidersCount === 0}
                  className="gap-2"
                >
                  {saving ? 'Сохраняем…' : 'Далее'}
                  {!saving && <ArrowRight size={18} />}
                </Button>
              </div>
            </div>
          </>
        )}
      </div>
    </AppLayout>
  )
}

// ----- City section -----

function CitySection({
  city,
  state,
  onUpdate,
}: {
  city: string
  state: CityState
  onUpdate: (updater: (l: CityLayout) => CityLayout) => void
}) {
  return (
    <section>
      <header className="flex items-baseline justify-between mb-3">
        <h2 className="text-h2 text-text-primary flex items-center gap-2">
          <MapPin size={18} className="text-brand" />
          {city}
        </h2>
        {state.status === 'done' && (
          <span className="text-sm text-text-tertiary">
            Группы: {state.layout.groups.length}{' '}
            {state.layout.unmatchedBranchIds.length > 0 &&
              `· Несгруппировано: ${state.layout.unmatchedBranchIds.length}`}
          </span>
        )}
      </header>

      {state.status === 'pending' && (
        <Card className="p-5 text-sm text-text-tertiary">Ожидает поиска…</Card>
      )}
      {state.status === 'searching' && (
        <Card className="p-5 flex items-center gap-3 text-sm text-text-secondary">
          <Loader2 className="text-brand animate-spin" size={16} />
          Ищем организации в городе {city}…
        </Card>
      )}
      {state.status === 'error' && (
        <Card className="p-5 space-y-2 text-sm text-destructive">
          <div>{state.message}</div>
          <div className="text-text-tertiary">
            Проверьте правильность ввода названия города/компании. Если другие города нашлись —
            запустить анализ можно по ним, этот будет проигнорирован.
          </div>
        </Card>
      )}
      {state.status === 'done' && (
        <CityLayoutView layout={state.layout} city={city} onUpdate={onUpdate} />
      )}
    </section>
  )
}

function CityLayoutView({
  layout,
  city,
  onUpdate,
}: {
  layout: CityLayout
  city: string
  onUpdate: (updater: (l: CityLayout) => CityLayout) => void
}) {
  const isEmpty =
    layout.groups.length === 0 &&
    layout.unmatchedBranchIds.length === 0 &&
    layout.ignoredBranchIds.length === 0
  if (isEmpty) {
    return (
      <Card className="p-5 space-y-2 text-sm text-text-tertiary">
        <div>Ничего не найдено в этом городе.</div>
        <div>
          Возможно, парсер не сматчил название. Проверьте правильность ввода или удалите этот
          город из анкеты компании.
        </div>
      </Card>
    )
  }
  return (
    <div className="space-y-4">
      {layout.groups.length > 0 && (
        <div className="space-y-3">
          {layout.groups.map((g) => (
            <LogicalBranchBlock
              key={g.key}
              group={g}
              layout={layout}
              onToggleMain={(isSelected) =>
                onUpdate((l) => setGroupSelected(l, g.key, isSelected))
              }
              onToggleProvider={(branchId, isEnabled) =>
                onUpdate((l) => setProviderEnabled(l, g.key, branchId, isEnabled))
              }
              onDetachProvider={(branchId) =>
                onUpdate((l) => detachProvider(l, g.key, branchId))
              }
              onRename={(name) => onUpdate((l) => setGroupName(l, g.key, name))}
              onUngroup={() => onUpdate((l) => ungroup(l, g.key))}
            />
          ))}
        </div>
      )}

      {layout.unmatchedBranchIds.length > 0 && (
        <UnmatchedSection
          layout={layout}
          city={city}
          onAttach={(branchId, key) => onUpdate((l) => attachToGroup(l, branchId, key))}
          onCreate={(branchId) => onUpdate((l) => createGroupFromUnmatched(l, branchId))}
          onIgnore={(branchId) => onUpdate((l) => ignoreUnmatched(l, branchId))}
        />
      )}

      {layout.ignoredBranchIds.length > 0 && (
        <IgnoredSection
          layout={layout}
          onRestore={(branchId) => onUpdate((l) => unignore(l, branchId))}
        />
      )}
    </div>
  )
}

// ----- Logical branch block -----

function LogicalBranchBlock({
  group,
  layout,
  onToggleMain,
  onToggleProvider,
  onDetachProvider,
  onRename,
  onUngroup,
}: {
  group: ClientGroup
  layout: CityLayout
  onToggleMain: (v: boolean) => void
  onToggleProvider: (branchId: string, v: boolean) => void
  onDetachProvider: (branchId: string) => void
  onRename: (name: string) => void
  onUngroup: () => void
}) {
  const isCustom = group.key.startsWith('custom-')
  // Чтобы у юзера была возможность ОТВЯЗАТЬ карточку: action есть всегда, но если в
  // группе остался ровно один провайдер — он не может «отвязаться» (группа просто
  // удалится, см. detachProvider). В этом случае UX лучше прятать кнопку вообще,
  // т.к. для разваливания группы есть «Разгруппировать».
  const canDetach = group.providers.length > 1

  return (
    <Card className={cn('overflow-hidden', !group.isSelected && 'opacity-60')}>
      <div className="flex items-start justify-between gap-3 px-5 py-3 bg-page-bg border-b border-border-subtle">
        <label className="flex items-start gap-3 cursor-pointer flex-1 min-w-0">
          <input
            type="checkbox"
            checked={group.isSelected}
            onChange={(e) => onToggleMain(e.target.checked)}
            className="mt-0.5 h-4 w-4 rounded border-border-subtle text-brand focus:ring-ring"
          />
          <div
            className="flex-1 min-w-0"
            // Клик по самому заголовку не должен переключать чекбокс — у нас тут
            // inline-edit. Свайпаем bubbling до label-а.
            onClick={(e) => e.preventDefault()}
          >
            <div className="flex items-center gap-2 flex-wrap">
              <EditableHeading value={group.name} onChange={onRename} />
              {!isCustom && (
                <span className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] font-medium bg-emerald-50 text-emerald-700 border border-emerald-200">
                  <Sparkles size={10} /> Автогруппировка
                </span>
              )}
              {isCustom && (
                <span className="inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-medium bg-blue-50 text-blue-700 border border-blue-200">
                  Создан вручную
                </span>
              )}
            </div>
            {group.address && (
              <div className="text-xs text-text-tertiary mt-0.5 truncate">{group.address}</div>
            )}
          </div>
        </label>
        <button
          type="button"
          onClick={onUngroup}
          className="shrink-0 inline-flex items-center gap-1 text-xs text-text-tertiary hover:text-destructive transition-colors"
          title="Разбить на отдельные карточки"
        >
          <Ungroup size={14} />
          Разгруппировать
        </button>
      </div>
      <ul className="divide-y divide-border-subtle">
        {group.providers.map((p) => {
          const card = layout.cardsById[p.branchId]
          if (!card) return null
          const meta =
            SOURCE_META[card.source] ?? { label: card.source, color: 'bg-page-bg text-text-secondary' }
          return (
            <li
              key={p.branchId}
              className={cn(
                'flex items-center gap-4 px-5 py-3 transition-colors',
                !group.isSelected && 'pointer-events-none',
                p.isEnabled && group.isSelected ? 'bg-card' : 'bg-card/60',
              )}
            >
              <input
                type="checkbox"
                checked={p.isEnabled}
                disabled={!group.isSelected}
                onChange={(e) => onToggleProvider(p.branchId, e.target.checked)}
                className="h-4 w-4 rounded border-border-subtle text-brand focus:ring-ring"
              />
              <span
                className={cn(
                  'inline-flex items-center justify-center px-2 py-0.5 rounded text-[11px] font-semibold shrink-0',
                  meta.color,
                )}
              >
                {meta.label}
              </span>
              <div className="flex-1 min-w-0">
                <div className="text-sm text-text-primary truncate">{card.name}</div>
                {card.address && (
                  <div className="text-xs text-text-tertiary truncate">{card.address}</div>
                )}
              </div>
              {card.rating !== null && (
                <div className="hidden sm:flex items-center gap-1 text-sm text-text-secondary shrink-0">
                  <Star size={14} className="text-amber-500 fill-amber-500" />
                  <span className="font-medium text-text-primary">{card.rating.toFixed(1)}</span>
                  {card.reviewCount !== null && (
                    <span className="text-xs text-text-tertiary">
                      {card.reviewCount.toLocaleString('ru-RU')}
                    </span>
                  )}
                </div>
              )}
              {card.externalUrl && (
                <a
                  href={card.externalUrl}
                  target="_blank"
                  rel="noreferrer noopener"
                  className="shrink-0 inline-flex items-center gap-1 text-xs text-brand hover:text-brand-hover"
                  title="Открыть оригинальную карточку"
                >
                  <ExternalLink size={12} />
                  Открыть
                </a>
              )}
              {canDetach && (
                <button
                  type="button"
                  onClick={() => onDetachProvider(p.branchId)}
                  className="shrink-0 inline-flex items-center gap-1 text-xs text-text-tertiary hover:text-destructive transition-colors pointer-events-auto"
                  title="Отвязать карточку из группы (переедет в «Не сгруппировано»)"
                >
                  <Unlink size={12} />
                  Отвязать
                </button>
              )}
            </li>
          )
        })}
      </ul>
    </Card>
  )
}

// Inline-редактируемый заголовок группы. Дефолт — отображение как текст с иконкой
// карандаша на hover'е; клик по карандашу/тексту → input на месте; Enter / blur
// сохраняет, Escape отменяет. Юзер может задать собственное название для физ. филиала
// (особенно важно когда автоматика дала одинаковые имена из бренда).
function EditableHeading({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  const [editing, setEditing] = useState(false)
  const [draft, setDraft] = useState(value)
  const inputRef = useRef<HTMLInputElement>(null)

  const start = () => {
    setDraft(value)
    setEditing(true)
    requestAnimationFrame(() => inputRef.current?.select())
  }
  const commit = () => {
    const trimmed = draft.trim()
    setEditing(false)
    if (trimmed && trimmed !== value) onChange(trimmed)
  }
  const cancel = () => {
    setEditing(false)
    setDraft(value)
  }

  if (editing) {
    return (
      <span className="inline-flex items-center gap-1 max-w-full">
        <input
          ref={inputRef}
          autoFocus
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onBlur={commit}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault()
              commit()
            } else if (e.key === 'Escape') {
              e.preventDefault()
              cancel()
            }
          }}
          maxLength={500}
          className="px-2 py-0.5 rounded border border-border-subtle bg-card text-sm font-semibold text-text-primary focus:outline-none focus:ring-2 focus:ring-ring max-w-[320px]"
        />
        <button
          type="button"
          onClick={commit}
          // onMouseDown сохраняем чтобы blur input'а не сработал раньше чем onClick
          onMouseDown={(e) => e.preventDefault()}
          className="p-1 text-emerald-600 hover:text-emerald-700"
          title="Сохранить"
        >
          <Check size={14} />
        </button>
      </span>
    )
  }

  return (
    <button
      type="button"
      onClick={start}
      className="group inline-flex items-center gap-1.5 text-left max-w-full hover:text-brand transition-colors"
      title="Переименовать филиал"
    >
      <span className="text-sm font-semibold text-text-primary truncate group-hover:text-brand">
        {value || <span className="italic text-text-tertiary">Без названия</span>}
      </span>
      <Pencil size={12} className="text-text-tertiary opacity-0 group-hover:opacity-100 transition-opacity shrink-0" />
    </button>
  )
}

// ----- Unmatched section -----

function UnmatchedSection({
  layout,
  city,
  onAttach,
  onCreate,
  onIgnore,
}: {
  layout: CityLayout
  city: string
  onAttach: (branchId: string, key: string) => void
  onCreate: (branchId: string) => void
  onIgnore: (branchId: string) => void
}) {
  return (
    <div>
      <div className="mb-2 px-1 text-xs uppercase tracking-wide text-text-tertiary">
        Не удалось сгруппировать автоматически · {city}
      </div>
      <Card className="overflow-hidden border-amber-100">
        <div className="px-5 py-3 bg-amber-50/60 border-b border-amber-100 text-xs text-amber-900">
          Эти карточки автоматика не смогла привязать к существующим группам. Для каждой
          выберите: «Привязать к филиалу», «Создать новый филиал» или «Игнорировать».
        </div>
        <ul className="divide-y divide-border-subtle">
          {layout.unmatchedBranchIds.map((branchId) => {
            const card = layout.cardsById[branchId]
            if (!card) return null
            return (
              <UnmatchedCardRow
                key={branchId}
                card={card}
                groups={layout.groups}
                onAttach={(key) => onAttach(branchId, key)}
                onCreate={() => onCreate(branchId)}
                onIgnore={() => onIgnore(branchId)}
              />
            )
          })}
        </ul>
      </Card>
    </div>
  )
}

function UnmatchedCardRow({
  card,
  groups,
  onAttach,
  onCreate,
  onIgnore,
}: {
  card: BranchSearchResultItem
  groups: ClientGroup[]
  onAttach: (key: string) => void
  onCreate: () => void
  onIgnore: () => void
}) {
  const meta =
    SOURCE_META[card.source] ?? { label: card.source, color: 'bg-page-bg text-text-secondary' }

  return (
    <li className="flex items-center gap-3 px-5 py-3">
      <span
        className={cn(
          'inline-flex items-center justify-center px-2 py-0.5 rounded text-[11px] font-semibold shrink-0',
          meta.color,
        )}
      >
        {meta.label}
      </span>
      <div className="flex-1 min-w-0">
        <div className="text-sm text-text-primary truncate">{card.name}</div>
        {card.address && (
          <div className="text-xs text-text-tertiary truncate">{card.address}</div>
        )}
      </div>
      {card.rating !== null && (
        <div className="hidden sm:flex items-center gap-1 text-sm text-text-secondary shrink-0">
          <Star size={14} className="text-amber-500 fill-amber-500" />
          <span className="font-medium text-text-primary">{card.rating.toFixed(1)}</span>
          {card.reviewCount !== null && (
            <span className="text-xs text-text-tertiary">
              {card.reviewCount.toLocaleString('ru-RU')}
            </span>
          )}
        </div>
      )}
      {card.externalUrl && (
        <a
          href={card.externalUrl}
          target="_blank"
          rel="noreferrer noopener"
          className="hidden sm:inline-flex shrink-0 items-center gap-1 text-xs text-brand hover:text-brand-hover"
        >
          <ExternalLink size={12} />
          Открыть
        </a>
      )}
      <UnmatchedActionsMenu
        groups={groups}
        onAttach={onAttach}
        onCreate={onCreate}
        onIgnore={onIgnore}
      />
    </li>
  )
}

function UnmatchedActionsMenu({
  groups,
  onAttach,
  onCreate,
  onIgnore,
}: {
  groups: ClientGroup[]
  onAttach: (key: string) => void
  onCreate: () => void
  onIgnore: () => void
}) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger
        className={cn(
          'inline-flex items-center gap-1.5 h-9 px-3 rounded-lg text-xs font-medium',
          'bg-card border border-border-subtle text-text-primary',
          'hover:border-brand/40 hover:bg-state-active-bg/40 hover:text-brand',
          'focus:outline-none focus:ring-2 focus:ring-ring focus:ring-offset-2',
          'data-[state=open]:border-brand/40 data-[state=open]:bg-state-active-bg/40 data-[state=open]:text-brand',
          'transition-colors',
        )}
      >
        Действие
        <ChevronDown size={14} className="opacity-70" />
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-72">
        {groups.length > 0 && (
          <>
            <DropdownMenuLabel>Привязать к филиалу</DropdownMenuLabel>
            {groups.map((g) => (
              <DropdownMenuItem
                key={g.key}
                onSelect={() => onAttach(g.key)}
                className="flex flex-col items-start gap-0.5"
              >
                <span className="text-sm font-medium truncate w-full">{g.name || 'Без названия'}</span>
                {g.address && (
                  <span className="text-xs text-text-tertiary truncate w-full">{g.address}</span>
                )}
              </DropdownMenuItem>
            ))}
            <DropdownMenuSeparator />
          </>
        )}
        <DropdownMenuItem onSelect={onCreate} className="gap-2">
          <Plus size={14} className="text-text-tertiary" />
          <span>Создать новый филиал</span>
        </DropdownMenuItem>
        <DropdownMenuItem onSelect={onIgnore} destructive className="gap-2">
          <X size={14} />
          <span>Игнорировать</span>
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

// ----- Ignored section -----

function IgnoredSection({
  layout,
  onRestore,
}: {
  layout: CityLayout
  onRestore: (branchId: string) => void
}) {
  return (
    <div>
      <div className="mb-2 px-1 text-xs uppercase tracking-wide text-text-tertiary">
        Игнорируется · {layout.ignoredBranchIds.length}
      </div>
      <Card className="overflow-hidden border-border-subtle">
        <ul className="divide-y divide-border-subtle">
          {layout.ignoredBranchIds.map((branchId) => {
            const card = layout.cardsById[branchId]
            if (!card) return null
            const meta =
              SOURCE_META[card.source] ?? { label: card.source, color: 'bg-page-bg text-text-secondary' }
            return (
              <li key={branchId} className="flex items-center gap-3 px-5 py-2.5 text-text-tertiary">
                <span
                  className={cn(
                    'inline-flex items-center justify-center px-2 py-0.5 rounded text-[11px] font-semibold shrink-0 opacity-60',
                    meta.color,
                  )}
                >
                  {meta.label}
                </span>
                <div className="flex-1 min-w-0">
                  <div className="text-sm truncate">{card.name}</div>
                  {card.address && <div className="text-xs truncate">{card.address}</div>}
                </div>
                <button
                  type="button"
                  onClick={() => onRestore(branchId)}
                  className="text-xs text-brand hover:text-brand-hover inline-flex items-center gap-1"
                >
                  <X size={12} />
                  Вернуть
                </button>
              </li>
            )
          })}
        </ul>
      </Card>
    </div>
  )
}

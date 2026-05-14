import { useEffect, useMemo, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { ArrowRight, Loader2, MapPin, Star } from 'lucide-react'
import { AppLayout } from '@/layouts/AppLayout'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import {
  companiesApi,
  type BranchSearchResultItem,
  type BranchSearchSourceGroup,
} from '@/api/companies'
import { cn } from '@/lib/utils'
import { AnalysisStepper } from './AnalysisStepper'

type CityState =
  | { status: 'pending' }
  | { status: 'searching' }
  | { status: 'done'; sources: BranchSearchSourceGroup[] }
  | { status: 'error'; message: string }

const SOURCE_META: Record<string, { label: string; color: string }> = {
  '2gis': { label: '2ГИС', color: 'bg-emerald-100 text-emerald-700' },
  yandex: { label: 'Яндекс.Карты', color: 'bg-amber-100 text-amber-700' },
  google: { label: 'Google Maps', color: 'bg-blue-100 text-blue-700' },
}

// Selection key is the internal CompanyBranch.Id — stable and unique even when parser
// plugins don't return externalId.

export default function BranchSearchPage() {
  const { companyId } = useParams<{ companyId: string }>()
  const navigate = useNavigate()

  const companyQuery = useQuery({
    queryKey: ['company', companyId],
    queryFn: () => companiesApi.get(companyId!),
    enabled: !!companyId,
  })

  const [cityStates, setCityStates] = useState<Record<string, CityState>>({})
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)

  const cities = companyQuery.data?.cities ?? []
  const allDone = cities.length > 0 && cities.every((c) => cityStates[c]?.status === 'done')
  const hasMultipleCities = cities.length > 1

  useEffect(() => {
    if (!companyId || cities.length === 0) return

    let cancelled = false
    const initial: Record<string, CityState> = {}
    for (const c of cities) initial[c] = { status: 'pending' }
    setCityStates(initial)

    ;(async () => {
      for (const city of cities) {
        if (cancelled) return
        setCityStates((prev) => ({ ...prev, [city]: { status: 'searching' } }))
        try {
          const response = await companiesApi.search(companyId, city)
          if (cancelled) return
          setCityStates((prev) => ({
            ...prev,
            [city]: { status: 'done', sources: response.sources },
          }))
        } catch (err) {
          if (cancelled) return
          const message = err instanceof Error ? err.message : 'Ошибка поиска'
          setCityStates((prev) => ({ ...prev, [city]: { status: 'error', message } }))
        }
      }
    })()

    return () => {
      cancelled = true
    }
  }, [companyId, cities.join('|')])

  const currentSearchingCity = cities.find((c) => cityStates[c]?.status === 'searching')

  const allItemsFlat = useMemo(() => {
    const items: Array<{ city: string; item: BranchSearchResultItem }> = []
    for (const city of cities) {
      const state = cityStates[city]
      if (state?.status !== 'done') continue
      for (const group of state.sources) {
        for (const it of group.items) items.push({ city, item: it })
      }
    }
    return items
  }, [cities, cityStates])

  const toggle = (key: string) => {
    setSelected((prev) => {
      const next = new Set(prev)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })
  }

  const onLaunch = async () => {
    if (!companyId) return
    const branchIds = allItemsFlat
      .filter(({ item }) => selected.has(item.id))
      .map(({ item }) => item.id)
    if (branchIds.length === 0) {
      setSaveError('Выберите хотя бы одну точку, чтобы продолжить')
      return
    }
    setSaving(true)
    setSaveError(null)
    try {
      await companiesApi.saveBranches(companyId, branchIds)
      navigate('/history')
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Не удалось сохранить выбор'
      setSaveError(message)
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
          <h1 className="text-h1 text-text-primary mb-2">Выберите ваши точки</h1>
          <p className="text-body text-text-secondary">
            Отметьте все карточки, относящиеся к вашему бизнесу. Это позволит системе собрать наиболее
            точные данные.
          </p>
        </div>

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
            <div className="space-y-6">
              {cities.map((city) => (
                <CitySection
                  key={city}
                  city={city}
                  state={cityStates[city] ?? { status: 'pending' }}
                  selected={selected}
                  onToggle={toggle}
                />
              ))}
            </div>

            {hasMultipleCities && !allDone && currentSearchingCity && (
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
                Выбрано: {selected.size} {selected.size === 1 ? 'точка' : 'точек'}
              </div>
              <div className="flex items-center justify-end gap-3">
                <Button
                  variant="outline"
                  onClick={() => navigate(`/analyses/new?from=${companyId}`)}
                >
                  Назад
                </Button>
                <Button
                  onClick={onLaunch}
                  disabled={!allDone || saving || selected.size === 0}
                  className="gap-2"
                >
                  {saving ? 'Сохраняем…' : 'Запустить анализ'}
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

function CitySection({
  city,
  state,
  selected,
  onToggle,
}: {
  city: string
  state: CityState
  selected: Set<string>
  onToggle: (key: string) => void
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
            Найдено: {state.sources.reduce((acc, g) => acc + g.items.length, 0)}
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
        <Card className="p-5 text-sm text-destructive">Ошибка поиска: {state.message}</Card>
      )}
      {state.status === 'done' && state.sources.length === 0 && (
        <Card className="p-5 text-sm text-text-tertiary">Ничего не найдено в этом городе.</Card>
      )}
      {state.status === 'done' && state.sources.length > 0 && (
        <div className="space-y-3">
          {state.sources.map((group) => (
            <SourceGroup
              key={group.source}
              city={city}
              group={group}
              selected={selected}
              onToggle={onToggle}
            />
          ))}
        </div>
      )}
    </section>
  )
}

function SourceGroup({
  city,
  group,
  selected,
  onToggle,
}: {
  city: string
  group: BranchSearchSourceGroup
  selected: Set<string>
  onToggle: (key: string) => void
}) {
  const meta = SOURCE_META[group.source] ?? { label: group.source, color: 'bg-page-bg text-text-secondary' }
  return (
    <Card className="overflow-hidden">
      <div className="flex items-center justify-between px-5 py-3 bg-page-bg border-b border-border-subtle">
        <div className="flex items-center gap-2">
          <span className={cn('inline-flex items-center justify-center w-6 h-6 rounded text-xs font-bold', meta.color)}>
            {meta.label.charAt(0)}
          </span>
          <span className="text-sm font-medium text-text-primary">{meta.label}</span>
        </div>
        <span className="text-xs text-text-tertiary">
          {group.items.length === 0
            ? 'Ничего не найдено'
            : `Найдено ${group.items.length} ${pluralizePoints(group.items.length)}`}
        </span>
      </div>
      {group.items.length > 0 && (
        <ul className="divide-y divide-border-subtle">
          {group.items.map((item) => {
            const key = item.id
            const checked = selected.has(key)
            return (
              <li key={key}>
                <label
                  className={cn(
                    'flex items-center gap-4 px-5 py-3 cursor-pointer transition-colors',
                    checked ? 'bg-state-active-bg/40' : 'hover:bg-page-bg/60',
                  )}
                >
                  <input
                    type="checkbox"
                    checked={checked}
                    onChange={() => onToggle(key)}
                    className="h-4 w-4 rounded border-border-subtle text-brand focus:ring-ring"
                  />
                  <div className="flex-1 min-w-0">
                    <div className="text-sm font-medium text-text-primary truncate">{item.name}</div>
                    {item.address && (
                      <div className="text-xs text-text-tertiary truncate">{item.address}</div>
                    )}
                  </div>
                  {item.rating !== null && (
                    <div className="flex items-center gap-1 text-sm text-text-secondary shrink-0">
                      <Star size={14} className="text-amber-500 fill-amber-500" />
                      <span className="font-medium text-text-primary">{item.rating.toFixed(1)}</span>
                      {item.reviewCount !== null && (
                        <span className="text-xs text-text-tertiary">
                          {item.reviewCount.toLocaleString('ru-RU')} {pluralizeReviews(item.reviewCount)}
                        </span>
                      )}
                    </div>
                  )}
                </label>
              </li>
            )
          })}
        </ul>
      )}
    </Card>
  )
}

function pluralizePoints(n: number) {
  const mod10 = n % 10
  const mod100 = n % 100
  if (mod10 === 1 && mod100 !== 11) return 'точка'
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return 'точки'
  return 'точек'
}

function pluralizeReviews(n: number) {
  const mod10 = n % 10
  const mod100 = n % 100
  if (mod10 === 1 && mod100 !== 11) return 'отзыв'
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return 'отзыва'
  return 'отзывов'
}

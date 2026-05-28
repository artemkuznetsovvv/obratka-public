import { createContext, useContext, useMemo, useState, type ReactNode } from 'react'
import type { DashboardHeaderDto } from '@/api/dashboards'

// Глобальный Context фильтров дашборда. Карточки метрик (1-7, О1-О3) будут
// подписываться на него через useDashboardFilters. В итерации 2 значения
// никуда не передаются (карточек ещё нет) — но Provider уже стоит, чтобы
// при появлении метрик не пришлось пересобирать дерево.
//
// Особые правила:
// - Период действует не на все метрики: метрика 4 «Свежий пульс» (окно 30д)
//   и метрика 7 «Новые отзывы за период» (свой переключатель) период игнорируют.
//   Ответственность за это — на самих карточках.
// - Topics в итерации 2 всегда []: источника списка тем нет до метрики 5.

export type Sentiment = 'позитивный' | 'нейтральный' | 'негативный'
export type Stars = 1 | 2 | 3 | 4 | 5

export const ALL_SENTIMENTS: readonly Sentiment[] = ['позитивный', 'нейтральный', 'негативный']
export const ALL_STARS: readonly Stars[] = [1, 2, 3, 4, 5]

export interface DashboardFiltersValue {
  periodFrom: string | null
  periodTo: string | null
  sources: string[]
  // cities/branches связаны каскадом: при изменении cities автоматически
  // пересчитываются branches (intersection). См. setCities в Provider.
  cities: string[]
  branches: string[]
  topics: string[]
  sentiments: Sentiment[]
  stars: Stars[]
}

interface DashboardFiltersContextShape extends DashboardFiltersValue {
  setPeriod: (from: string | null, to: string | null) => void
  setSources: (next: string[]) => void
  setCities: (next: string[]) => void
  setBranches: (next: string[]) => void
  setSentiments: (next: Sentiment[]) => void
  setStars: (next: Stars[]) => void
  reset: () => void
  hasActiveFilters: boolean
}

const Ctx = createContext<DashboardFiltersContextShape | null>(null)

export function DashboardFiltersProvider({
  header,
  children,
}: {
  header: DashboardHeaderDto
  children: ReactNode
}) {
  // Defaults: всё включено. Период по умолчанию = null/null («с самого начала»),
  // карточки показывают baseline за весь период джоба.
  // НЕ берём header.periodFrom/To — там лежит Company.DraftPeriodFrom/To
  // (caveat §9: реального периода джоба нигде нет), это «настройки следующего
  // анализа», обычно узкое окно, которое сбивало бы дефолт фильтра.
  // Когда в Processing-Gateway появится period_from/to на analysis_jobs
  // (processing-gateway-todo.md #1) — заменим на честный период джоба.
  // Уникальные города джоба (для фильтра «Город»). Берём только не-null;
  // если у филиала city=null, он не попадает в city-фильтр, но всё равно
  // присутствует в branches-фильтре (отображается без группировки).
  const uniqueCities = useMemo(
    () =>
      Array.from(
        new Set(
          header.branches
            .map((b) => b.city)
            .filter((c): c is string => !!c && c.length > 0),
        ),
      ),
    [header.branches],
  )

  const initial = useMemo<DashboardFiltersValue>(
    () => ({
      periodFrom: null,
      periodTo: null,
      sources: header.sources.slice(),
      cities: uniqueCities.slice(),
      branches: header.branches.map((b) => b.branchId),
      topics: [],
      sentiments: [...ALL_SENTIMENTS],
      stars: [...ALL_STARS],
    }),
    [header, uniqueCities],
  )

  const [state, setState] = useState<DashboardFiltersValue>(initial)

  const hasActiveFilters = useMemo(
    () => !sameFilters(state, initial),
    [state, initial],
  )

  const value = useMemo<DashboardFiltersContextShape>(
    () => ({
      ...state,
      setPeriod: (from, to) => setState((s) => ({ ...s, periodFrom: from, periodTo: to })),
      setSources: (next) => setState((s) => ({ ...s, sources: next })),
      // Каскад: при изменении cities обновляем branches как intersection
      // с разрешёнными (филиалы в выбранных городах). Юзер «снял Москву» →
      // все московские филиалы автоматически выпадают из branches-фильтра.
      // Когда юзер «вернул Москву» — branches НЕ восстанавливаются автоматом
      // (юзер сам выберет, какие из московских ему нужны).
      setCities: (next) =>
        setState((s) => {
          const nextCitiesSet = new Set(next)
          const allowedBranchIds = new Set(
            header.branches
              .filter((b) => !b.city || nextCitiesSet.has(b.city))
              .map((b) => b.branchId),
          )
          return {
            ...s,
            cities: next,
            branches: s.branches.filter((id) => allowedBranchIds.has(id)),
          }
        }),
      setBranches: (next) => setState((s) => ({ ...s, branches: next })),
      setSentiments: (next) => setState((s) => ({ ...s, sentiments: next })),
      setStars: (next) => setState((s) => ({ ...s, stars: next })),
      reset: () => setState(initial),
      hasActiveFilters,
    }),
    [state, initial, hasActiveFilters, header.branches],
  )

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>
}

export function useDashboardFilters(): DashboardFiltersContextShape {
  const v = useContext(Ctx)
  if (!v) throw new Error('useDashboardFilters must be used inside DashboardFiltersProvider')
  return v
}

function sameFilters(a: DashboardFiltersValue, b: DashboardFiltersValue): boolean {
  return (
    a.periodFrom === b.periodFrom &&
    a.periodTo === b.periodTo &&
    sameArr(a.sources, b.sources) &&
    sameArr(a.cities, b.cities) &&
    sameArr(a.branches, b.branches) &&
    sameArr(a.sentiments, b.sentiments) &&
    sameArr(a.stars, b.stars)
  )
}

function sameArr<T>(a: readonly T[], b: readonly T[]): boolean {
  if (a.length !== b.length) return false
  const setB = new Set(b)
  for (const x of a) if (!setB.has(x)) return false
  return true
}

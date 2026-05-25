import { useMemo } from 'react'
import { Filter, RotateCcw } from 'lucide-react'
import { Card } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import type { DashboardHeaderDto } from '@/api/dashboards'
import { SOURCE_LABEL } from '@/pages/history/analysisStatus'
import {
  ALL_SENTIMENTS,
  ALL_STARS,
  useDashboardFilters,
  type Sentiment,
  type Stars,
} from '../DashboardFiltersContext'
import { MultiSelectFilter, type MultiSelectOption } from './MultiSelectFilter'

const SOURCE_BADGE: Record<string, string> = {
  '2gis': 'bg-emerald-100 text-emerald-700',
  yandex: 'bg-amber-100 text-amber-700',
  google: 'bg-blue-100 text-blue-700',
}

const SENTIMENT_LABEL: Record<Sentiment, string> = {
  'позитивный': 'Хорошо',
  'нейтральный': 'Нейтрально',
  'негативный': 'Плохо',
}

const SENTIMENT_BADGE: Record<Sentiment, string> = {
  'позитивный': 'bg-emerald-100 text-emerald-700',
  'нейтральный': 'bg-slate-100 text-slate-700',
  'негативный': 'bg-rose-100 text-rose-700',
}

// Блок фильтров над основным содержимым дашборда (ТЗ 4.4).
// 6 фильтров: период, источник, филиал, тема, тональность, рейтинг.
// «Тема» в итерации 2 — disabled (источника списка тем нет до метрики 5).
export function DashboardFilters({ header }: { header: DashboardHeaderDto }) {
  const filters = useDashboardFilters()

  const sourceOptions = useMemo<MultiSelectOption<string>[]>(
    () =>
      header.sources.map((s) => ({
        value: s,
        label: SOURCE_LABEL[s] ?? s,
        badgeClass: SOURCE_BADGE[s] ?? 'bg-page-bg text-text-secondary',
      })),
    [header.sources],
  )

  const branchOptions = useMemo<MultiSelectOption<string>[]>(
    () =>
      header.branches.map((b) => ({
        value: b.branchId,
        label: b.name ?? 'Филиал удалён',
      })),
    [header.branches],
  )

  const sentimentOptions = useMemo<MultiSelectOption<Sentiment>[]>(
    () =>
      ALL_SENTIMENTS.map((s) => ({
        value: s,
        label: SENTIMENT_LABEL[s],
        badgeClass: SENTIMENT_BADGE[s],
      })),
    [],
  )

  const starsOptions = useMemo<MultiSelectOption<Stars>[]>(
    () =>
      ALL_STARS.map((n) => ({
        value: n,
        label: `${n} ${n === 1 ? 'звезда' : n < 5 ? 'звезды' : 'звёзд'}`,
      })),
    [],
  )

  return (
    <Card className="mb-6 p-4">
      <div className="flex items-center justify-between gap-3 flex-wrap mb-3">
        <div className="flex items-center gap-2 text-h3 text-text-primary">
          <Filter size={16} className="text-text-tertiary" />
          Фильтры
        </div>
        {filters.hasActiveFilters && (
          <Button
            variant="ghost"
            size="sm"
            onClick={filters.reset}
            className="gap-1.5 text-xs text-text-secondary"
          >
            <RotateCcw size={12} />
            Сбросить
          </Button>
        )}
      </div>
      <div className="flex flex-wrap items-end gap-2">
        <DateRange />
        <MultiSelectFilter
          label="Источник"
          options={sourceOptions}
          selected={filters.sources}
          onChange={filters.setSources}
        />
        <MultiSelectFilter
          label="Филиал"
          options={branchOptions}
          selected={filters.branches}
          onChange={filters.setBranches}
        />
        <MultiSelectFilter
          label="Тема"
          options={[]}
          selected={[]}
          onChange={() => {}}
          disabled
          disabledHint="Появится после реализации метрики «О чём говорят чаще всего»"
        />
        <MultiSelectFilter
          label="Тональность"
          options={sentimentOptions}
          selected={filters.sentiments}
          onChange={filters.setSentiments}
        />
        <MultiSelectFilter
          label="Рейтинг"
          options={starsOptions}
          selected={filters.stars}
          onChange={filters.setStars}
        />
      </div>
    </Card>
  )
}

// Период — нативный <input type="date"> на минималках. В будущем заменим
// на react-day-picker, когда понадобится более богатый UX (пресеты, локаль и т.д.).
function DateRange() {
  const f = useDashboardFilters()
  const fromValue = f.periodFrom ? f.periodFrom.slice(0, 10) : ''
  const toValue = f.periodTo ? f.periodTo.slice(0, 10) : ''
  return (
    <div className="flex flex-col gap-1">
      <span className="text-text-tertiary text-xs uppercase tracking-wide px-1">Период</span>
      <div className="flex items-center gap-1.5">
        <input
          type="date"
          value={fromValue}
          max={toValue || undefined}
          onChange={(e) => f.setPeriod(e.target.value || null, f.periodTo)}
          className="h-9 px-2 rounded-lg border border-border-subtle bg-card text-sm text-text-primary focus:outline-none focus:ring-2 focus:ring-brand/30"
        />
        <span className="text-text-tertiary text-xs">→</span>
        <input
          type="date"
          value={toValue}
          min={fromValue || undefined}
          onChange={(e) => f.setPeriod(f.periodFrom, e.target.value || null)}
          className="h-9 px-2 rounded-lg border border-border-subtle bg-card text-sm text-text-primary focus:outline-none focus:ring-2 focus:ring-brand/30"
        />
      </div>
    </div>
  )
}

import { useMemo, useState } from 'react'
import { CalendarRange, ChevronDown, Filter, RotateCcw } from 'lucide-react'
import { Card } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Calendar } from '@/components/ui/calendar'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
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
import { cn } from '@/lib/utils'
import { extractBranchLabel } from '../branchLabel'

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
        // У сетевых брендов name одинаковое у всех — показываем «локацию»
        // (улица+дом или ТЦ) из адреса. Та же утилита что в табах дашборда.
        label: extractBranchLabel(b.address, b.name),
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
        {/* Фильтр «Филиал» бесполезен когда филиал один — скрываем. Карточки
            всё равно получают branchId напрямую из секции; filters.branches в
            Context остаётся валидным single-item массивом. */}
        {header.branches.length > 1 && (
          <MultiSelectFilter
            label="Филиал"
            options={branchOptions}
            selected={filters.branches}
            onChange={filters.setBranches}
          />
        )}
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

// Период — единый trigger-popover с DayPicker в range-режиме и пресетами
// слева. Пресеты — sliding windows от клиентского «сегодня» (юзер мыслит
// «месяц от моего сегодня», бэк сравнивает с review_date по date-only).
function DateRange() {
  const f = useDashboardFilters()
  const [open, setOpen] = useState(false)

  const fromDate = f.periodFrom ? new Date(f.periodFrom) : undefined
  const toDate = f.periodTo ? new Date(f.periodTo) : undefined

  const triggerLabel = useMemo(() => {
    if (!f.periodFrom || !f.periodTo) return 'Выбрать период'
    return `${formatDayMonth(f.periodFrom)} — ${formatDayMonth(f.periodTo)}`
  }, [f.periodFrom, f.periodTo])

  const applyPreset = (daysBack: number) => {
    const today = new Date()
    const start = new Date(today)
    start.setDate(start.getDate() - daysBack + 1)
    f.setPeriod(toIsoDate(start), toIsoDate(today))
    setOpen(false)
  }

  return (
    <div className="flex flex-col gap-1">
      <span className="text-text-tertiary text-xs uppercase tracking-wide px-1">Период</span>
      <Popover open={open} onOpenChange={setOpen}>
        <PopoverTrigger asChild>
          <Button
            type="button"
            variant="outline"
            size="sm"
            className={cn(
              'h-9 justify-between gap-2 font-normal min-w-[14rem]',
              !f.periodFrom && 'text-text-tertiary',
            )}
          >
            <span className="inline-flex items-center gap-1.5">
              <CalendarRange size={14} className="text-text-tertiary" />
              {triggerLabel}
            </span>
            <ChevronDown size={14} className="text-text-tertiary shrink-0" />
          </Button>
        </PopoverTrigger>
        <PopoverContent className="p-0 w-auto" align="start">
          <div className="flex">
            {/* Левый столбец — пресеты */}
            <div className="flex flex-col gap-0.5 p-3 border-r border-border-subtle min-w-[10rem]">
              <div className="text-[11px] uppercase tracking-wide text-text-tertiary mb-1 px-2">
                Быстрый выбор
              </div>
              <PresetButton label="Неделя" onClick={() => applyPreset(7)} />
              <PresetButton label="Месяц" onClick={() => applyPreset(30)} />
              <PresetButton label="3 месяца" onClick={() => applyPreset(90)} />
              <div className="my-1 h-px bg-border-subtle" />
              <PresetButton
                label="Очистить"
                onClick={() => {
                  f.setPeriod(null, null)
                  setOpen(false)
                }}
                muted
              />
            </div>
            {/* Календарь справа */}
            <div className="p-2">
              <Calendar
                mode="range"
                selected={{ from: fromDate, to: toDate }}
                onSelect={(range) => {
                  f.setPeriod(
                    range?.from ? toIsoDate(range.from) : null,
                    range?.to ? toIsoDate(range.to) : null,
                  )
                }}
                numberOfMonths={2}
                defaultMonth={fromDate}
              />
            </div>
          </div>
        </PopoverContent>
      </Popover>
    </div>
  )
}

function PresetButton({
  label,
  onClick,
  muted,
}: {
  label: string
  onClick: () => void
  muted?: boolean
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        'px-2 py-1.5 rounded-md text-sm text-left transition-colors',
        muted
          ? 'text-text-tertiary hover:bg-page-bg hover:text-text-secondary'
          : 'text-text-primary hover:bg-state-active-bg hover:text-brand',
      )}
    >
      {label}
    </button>
  )
}

function toIsoDate(d: Date): string {
  const y = d.getFullYear()
  const m = String(d.getMonth() + 1).padStart(2, '0')
  const day = String(d.getDate()).padStart(2, '0')
  return `${y}-${m}-${day}`
}

// Компактный формат для trigger-кнопки: «01 апр» (без года, чтобы влезало).
// Год в trigger не нужен — диапазон обычно в текущем году; если фильтр
// растянули на несколько лет — раскроет popover и юзер увидит точные даты.
function formatDayMonth(iso: string): string {
  try {
    const d = new Date(iso)
    return d.toLocaleDateString('ru-RU', { day: '2-digit', month: 'short' })
  } catch {
    return iso.slice(0, 10)
  }
}

import { useMemo, useState } from 'react'
import type { Matcher } from 'react-day-picker'
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

  // Уникальные города джоба. Если >1 → показываем фильтр «Город» и добавляем
  // city к лейблу филиалов (чтобы отличать одинаковые улицы в разных городах).
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
  const isMultiCity = uniqueCities.length > 1

  const cityOptions = useMemo<MultiSelectOption<string>[]>(
    () => uniqueCities.map((c) => ({ value: c, label: c })),
    [uniqueCities],
  )

  // Фильтр «Филиал»: показываем только те филиалы, которые сейчас в выбранных
  // городах (каскад вниз). При multi-city к лейблу добавляем «· Москва».
  const branchOptions = useMemo<MultiSelectOption<string>[]>(() => {
    const allowedCities = new Set(filters.cities)
    return header.branches
      .filter((b) => !b.city || allowedCities.size === 0 || allowedCities.has(b.city))
      .map((b) => ({
        value: b.branchId,
        label: extractBranchLabel(b.address, b.name, {
          cityHint: isMultiCity ? b.city : null,
        }),
      }))
  }, [header.branches, filters.cities, isMultiCity])

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
        <DateRange periodFrom={header.periodFrom} periodTo={header.periodTo} />
        <MultiSelectFilter
          label="Источник"
          options={sourceOptions}
          selected={filters.sources}
          onChange={filters.setSources}
        />
        {/* Фильтр «Город» — виден только при ≥2 городах в джобе. Каскад вниз:
            снятый город автоматически выпадает из «Филиал» (см. setCities в
            DashboardFiltersContext). */}
        {isMultiCity && (
          <MultiSelectFilter
            label="Город"
            options={cityOptions}
            selected={filters.cities}
            onChange={filters.setCities}
          />
        )}
        {/* Фильтр «Филиал» бесполезен когда филиал один — скрываем. */}
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
// слева. Пресеты — sliding windows от конца периода анализа (юзер мыслит
// «последний месяц анализа», бэк сравнивает с review_date по date-only).
// Выбор ограничен периодом анализа [periodFrom, periodTo] — нельзя уйти за
// границы джоба (см. caveat в DashboardFiltersContext: period — best-effort
// из Company.DraftPeriodFrom/To, но это ровно то, что показано в шапке).
function DateRange({
  periodFrom,
  periodTo,
}: {
  periodFrom: string | null
  periodTo: string | null
}) {
  const f = useDashboardFilters()
  const [open, setOpen] = useState(false)

  // parseIsoDateLocal (а не new Date) — значения фильтра приходят date-only из toIsoDate;
  // new Date('YYYY-MM-DD') парсит как UTC-полночь и в минус-зонах съезжает на день назад,
  // рассинхронизируясь с minDate/maxDate (тоже локальными). Держим один часовой контекст.
  const fromDate = f.periodFrom ? parseIsoDateLocal(f.periodFrom) : undefined
  const toDate = f.periodTo ? parseIsoDateLocal(f.periodTo) : undefined

  // Границы анализа как локальные даты (полночь) — для дизейбла и пресетов.
  const minDate = useMemo(() => (periodFrom ? parseIsoDateLocal(periodFrom) : undefined), [periodFrom])
  const maxDate = useMemo(() => (periodTo ? parseIsoDateLocal(periodTo) : undefined), [periodTo])

  // Дни вне [minDate, maxDate] нельзя выбрать (RDP matchers; границы включительно).
  const disabledMatcher = useMemo<Matcher[] | undefined>(() => {
    const m: Matcher[] = []
    if (minDate) m.push({ before: minDate })
    if (maxDate) m.push({ after: maxDate })
    return m.length > 0 ? m : undefined
  }, [minDate, maxDate])

  const triggerLabel = useMemo(() => {
    if (!f.periodFrom || !f.periodTo) return 'Выбрать период'
    return `${formatDayMonth(f.periodFrom)} — ${formatDayMonth(f.periodTo)}`
  }, [f.periodFrom, f.periodTo])

  const applyPreset = (daysBack: number) => {
    // Верх пресета — конец периода анализа (или сегодня, если период не задан).
    const upper = maxDate ?? new Date()
    const start = new Date(upper)
    start.setDate(start.getDate() - daysBack + 1)
    // Низ не вылезает за начало периода анализа.
    const clampedStart = minDate && start < minDate ? minDate : start
    // Защита от инвертированных границ (periodFrom > periodTo): не отдаём from > to.
    const safeStart = clampedStart > upper ? upper : clampedStart
    f.setPeriod(toIsoDate(safeStart), toIsoDate(upper))
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
                defaultMonth={fromDate ?? maxDate ?? minDate}
                disabled={disabledMatcher}
                startMonth={minDate}
                endMonth={maxDate}
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

// ISO-datetime (DateTimeOffset из API) → локальная дата на полночь, без сдвига
// по таймзоне. Берём только Y-M-D, чтобы границы совпадали с date-only выбором
// календаря (иначе «2024-12-01T00:00:00Z» в минус-зонах съезжал бы на день назад).
function parseIsoDateLocal(iso: string): Date {
  const [y, m, d] = iso.slice(0, 10).split('-').map(Number)
  return new Date(y, (m ?? 1) - 1, d ?? 1)
}

// Компактный формат для trigger-кнопки: «01 апр» (без года, чтобы влезало).
// Год в trigger не нужен — диапазон обычно в текущем году; если фильтр
// растянули на несколько лет — раскроет popover и юзер увидит точные даты.
function formatDayMonth(iso: string): string {
  try {
    // parseIsoDateLocal — date-only значения фильтра без UTC-сдвига (см. fromDate выше).
    const d = parseIsoDateLocal(iso)
    return d.toLocaleDateString('ru-RU', { day: '2-digit', month: 'short' })
  } catch {
    return iso.slice(0, 10)
  }
}

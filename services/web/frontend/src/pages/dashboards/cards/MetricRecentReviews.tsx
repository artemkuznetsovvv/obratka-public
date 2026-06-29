import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useParams } from 'react-router-dom'
import { Card } from '@/components/ui/card'
import {
  metricsApi,
  type RecentReviewsMetricDto,
  type RecentReviewsWindow,
} from '@/api/metrics'
import { cn } from '@/lib/utils'
import { useDashboardFilters } from '../DashboardFiltersContext'
import { MetricSkeletonCard } from './shared/CardParts'

const WINDOW_OPTIONS: { value: RecentReviewsWindow; short: string; full: string }[] = [
  { value: '7d',  short: '7д',  full: 'неделю' },
  { value: '30d', short: '30д', full: 'месяц' },
  { value: '3m',  short: '3м',  full: '3 месяца' },
  { value: '6m',  short: '6м',  full: 'полгода' },
  { value: '12m', short: '12м', full: 'год' },
]

// Метрика М7 «Новые отзывы за период».
// Переключатель окна — локальный state карточки (по спеке: «влияет только на
// эту карточку, на остальной дашборд не влияет»).
// Period дашборда сюда НЕ передаётся, sentiments тоже не передаётся.
export function MetricRecentReviews({ branchId }: { branchId: string }) {
  const { jobId } = useParams<{ jobId: string }>()
  const filters = useDashboardFilters()
  const [windowValue, setWindowValue] = useState<RecentReviewsWindow>('7d')

  const sourcesKey = [...filters.sources].sort().join(',')
  const starsKey = [...filters.stars].sort((a, b) => a - b).join(',')

  const q = useQuery({
    queryKey: [
      'metrics',
      jobId,
      'recent-reviews',
      branchId,
      windowValue,
      sourcesKey,
      starsKey,
    ],
    queryFn: () =>
      metricsApi.recentReviews(jobId!, {
        branchIds: [branchId],
        window: windowValue,
        sources: filters.sources,
        stars: filters.stars,
      }),
    enabled: !!jobId && !!branchId,
    staleTime: 60_000,
  })

  return (
    <Card
      className={cn(
        'p-5 flex flex-col gap-3 transition-opacity min-h-[14rem]',
        q.isFetching && !q.isLoading && 'opacity-70',
      )}
    >
      <Header windowValue={windowValue} onChange={setWindowValue} />

      {q.isLoading ? (
        <div className="py-4 text-xs text-text-tertiary">Загружаем…</div>
      ) : q.isError ? (
        <div className="py-2 text-xs text-rose-700">
          Не удалось посчитать: {(q.error as Error).message}
        </div>
      ) : !q.data ? (
        <MetricSkeletonCard />
      ) : (
        <RecentReviewsView dto={q.data} windowValue={windowValue} />
      )}
    </Card>
  )
}

function Header({
  windowValue,
  onChange,
}: {
  windowValue: RecentReviewsWindow
  onChange: (next: RecentReviewsWindow) => void
}) {
  return (
    <div className="flex items-start justify-between gap-2">
      <div className="text-sm font-semibold text-text-primary">Новые отзывы</div>
      <div className="flex items-center gap-1">
        <div className="inline-flex rounded-lg bg-page-bg p-0.5 text-[10px] font-medium">
          {WINDOW_OPTIONS.map((opt) => (
            <button
              key={opt.value}
              type="button"
              onClick={() => onChange(opt.value)}
              className={cn(
                'px-1.5 py-0.5 rounded-md transition-colors',
                windowValue === opt.value
                  ? 'bg-card text-text-primary shadow-sm'
                  : 'text-text-tertiary hover:text-text-secondary',
              )}
              aria-pressed={windowValue === opt.value}
            >
              {opt.short}
            </button>
          ))}
        </div>
      </div>
    </div>
  )
}

function RecentReviewsView({
  dto,
  windowValue,
}: {
  dto: RecentReviewsMetricDto
  windowValue: RecentReviewsWindow
}) {
  const opt = WINDOW_OPTIONS.find((o) => o.value === windowValue)!

  // Облегчённый режим: < 2 полных prev-окон.
  if (dto.fullPreviousWindows < 2) {
    return (
      <>
        <div>
          <div className="text-[40px] leading-none font-bold text-text-primary tabular-nums">
            {dto.currentCount}
          </div>
          <div className="text-[11px] text-text-tertiary mt-1">за {opt.full}</div>
        </div>
        <div className="text-xs text-text-tertiary">
          Пока недостаточно данных для сравнения
        </div>
      </>
    )
  }

  // Полный режим: «обычное» = average по 2 или 3 prev-окнам.
  const prevs: number[] = dto.fullPreviousWindows >= 3
    ? [dto.prev3Count, dto.prev2Count, dto.prev1Count]
    : [dto.prev2Count, dto.prev1Count]
  const usual = prevs.reduce((a, b) => a + b, 0) / prevs.length
  const usualRounded = Math.round(usual)
  // Отклонение в долях (для правил), %integer для отображения.
  const deviation = usual === 0
    ? (dto.currentCount === 0 ? 0 : Number.POSITIVE_INFINITY)
    : (dto.currentCount - usual) / usual

  const verdict = pickVerdict(deviation)

  // Spark: prev'ы от старого к новому + current. 3 или 4 точки.
  const sparkPoints = [...prevs, dto.currentCount]

  return (
    <>
      <div className={cn('text-xl font-bold leading-tight', verdict.colorClass)}>
        {verdict.text}
      </div>

      <div>
        <div className="text-[40px] leading-none font-bold text-text-primary tabular-nums">
          {dto.currentCount}
        </div>
        <div className="text-[11px] text-text-tertiary mt-1">
          за {opt.full}, обычно {usualRounded}
        </div>
      </div>

      <Sparkline values={sparkPoints} accentColor={verdict.colorClass} />

      <div className="text-[10px] text-text-tertiary mt-auto">
        Сравнение с {prevs.length === 3 ? 'тремя' : 'двумя'} предыдущими окнами
      </div>
    </>
  )
}

// Правила оценки по спеке М7. Отклонение в долях.
//   [-0.30 .. +0.30]      → «Поток отзывов в норме»     (серый)
//   (+0.30 .. +1.00]      → «Отзывов больше обычного»   (зелёный)
//   > +1.00               → «Резкий всплеск отзывов»    (оранжевый)
//   (-0.50 .. -0.30)      → «Поток отзывов в норме»     (серый)  ← поглощён первым правилом
//   ≤ -0.50               → «Резкая тишина»             (красный)
//
// Спека дублирует «норма» для (-0.50, -0.30) — но это эквивалентно первому
// правилу (±30%) только сверху; снизу до -50% это всё ещё «норма». Объединяю
// в одну ветку «|dev| ≤ 0.3 ИЛИ -0.5 < dev < -0.3 → норма» = «dev > -0.5 И dev ≤ +0.3».
function pickVerdict(deviation: number): { text: string; colorClass: string } {
  if (deviation > 1.0) return { text: 'Резкий всплеск отзывов', colorClass: 'text-orange-600' }
  if (deviation > 0.3) return { text: 'Отзывов больше обычного', colorClass: 'text-emerald-600' }
  if (deviation <= -0.5) return { text: 'Резкая тишина', colorClass: 'text-rose-600' }
  // -0.5 < dev ≤ +0.3  →  норма (включая ±30% и (-50%, -30%))
  return { text: 'Поток отзывов в норме', colorClass: 'text-slate-600' }
}

// SVG спарклайн без подписей значений. ViewBox 100×24, polyline тонкая.
// Точки рассчитываются: x — равномерное распределение, y — нормированное от
// min до max в наборе.
function Sparkline({ values, accentColor }: { values: number[]; accentColor: string }) {
  if (values.length < 2) return null
  const min = Math.min(...values)
  const max = Math.max(...values)
  const range = max - min
  const w = 100
  const h = 24
  const padX = 2
  const padY = 3
  const innerW = w - padX * 2
  const innerH = h - padY * 2

  const points = values.map((v, i) => {
    const x = padX + (innerW * i) / (values.length - 1)
    const y = range === 0
      ? padY + innerH / 2
      : padY + innerH - (innerH * (v - min)) / range
    return [x, y] as const
  })
  const polyline = points.map(([x, y]) => `${x.toFixed(2)},${y.toFixed(2)}`).join(' ')
  const lastPoint = points[points.length - 1]

  return (
    <svg
      viewBox={`0 0 ${w} ${h}`}
      preserveAspectRatio="none"
      className="w-full h-6 text-text-tertiary"
      aria-hidden
    >
      <polyline
        points={polyline}
        fill="none"
        stroke="currentColor"
        strokeWidth="1.25"
        strokeLinejoin="round"
        strokeLinecap="round"
      />
      {/* Точка на current — выделяем accent-цветом */}
      <circle
        cx={lastPoint[0]}
        cy={lastPoint[1]}
        r="1.8"
        className={accentColor}
        fill="currentColor"
      />
    </svg>
  )
}

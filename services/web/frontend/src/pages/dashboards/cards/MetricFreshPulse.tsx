import { useQuery } from '@tanstack/react-query'
import { useParams } from 'react-router-dom'
import { ArrowDown, ArrowRight, ArrowUp } from 'lucide-react'
import { Card } from '@/components/ui/card'
import { metricsApi, type FreshPulseMetricDto } from '@/api/metrics'
import { cn } from '@/lib/utils'
import { useDashboardFilters } from '../DashboardFiltersContext'
import { MetricErrorCard, MetricSkeletonCard } from './shared/CardParts'

// Метрика М4 «Свежий пульс» — взвешенный индекс настроения за окно 30 дней
// от server now. Period дашборда сюда НЕ передаётся (исключение, как у М3
// с sentiments), sentiments — тоже. Sources / Stars / Branch — применяются.
//
// FRESHNESS-WEIGHTS TODO (OBR-35): пока веса = 1, formula = (pos-neg)/total
// (см. FreshPulseMetricService.cs).
export function MetricFreshPulse({ branchId }: { branchId: string }) {
  const { jobId } = useParams<{ jobId: string }>()
  const filters = useDashboardFilters()

  const sourcesKey = [...filters.sources].sort().join(',')
  const starsKey = [...filters.stars].sort((a, b) => a - b).join(',')

  const q = useQuery({
    // queryKey НЕ включает period — окно жёстко 30 дней, изменения фильтра
    // периода не должны триггерить рефетч этой карточки.
    queryKey: [
      'metrics',
      jobId,
      'fresh-pulse',
      branchId,
      sourcesKey,
      starsKey,
    ],
    queryFn: () =>
      metricsApi.freshPulse(jobId!, {
        branchIds: [branchId],
        sources: filters.sources,
        stars: filters.stars,
      }),
    enabled: !!jobId && !!branchId,
    staleTime: 60_000,
  })

  if (q.isLoading) return <MetricSkeletonCard minHeight="14rem" />
  if (q.isError) return <MetricErrorCard message={(q.error as Error).message} />
  if (!q.data) return <MetricSkeletonCard minHeight="14rem" />

  return <FreshPulseView dto={q.data} isFetching={q.isFetching && !q.isLoading} />
}

function FreshPulseView({ dto, isFetching }: { dto: FreshPulseMetricDto; isFetching: boolean }) {
  const cur = dto.current

  // Empty state по спеке: «Нет данных за последний месяц», шкала и динамика
  // скрываются, без падения.
  if (cur.totalNonEmpty === 0 || cur.index === null) {
    return (
      <Card className={cn('p-5 flex flex-col gap-2 min-h-[14rem]', isFetching && 'opacity-70')}>
        <Header />
        <div className="text-sm text-text-tertiary">Нет данных за последний месяц</div>
      </Card>
    )
  }

  const indexRounded = Math.round(cur.index)
  const previousRounded = dto.previous.index !== null ? Math.round(dto.previous.index) : null
  const verdict = pickVerdict(indexRounded)
  const dynamic = computeDynamic(cur.index, dto.previous.index)

  // Позиция точки на шкале -100..+100 → 0..100% слева.
  const dotLeftPct = ((indexRounded + 100) / 2)

  return (
    <Card
      className={cn(
        'p-5 flex flex-col gap-3 transition-opacity min-h-[14rem]',
        isFetching && 'opacity-70',
      )}
    >
      <Header />

      {/* Словесная оценка — главный элемент */}
      <div className={cn('text-xl font-bold', verdict.colorClass)}>{verdict.text}</div>

      {/* Шкала -100..+100 с точкой-меткой */}
      <div className="pt-2 pb-1">
        <div
          className="relative h-2 rounded-full"
          style={{
            background:
              'linear-gradient(to right, rgb(244 63 94) 0%, rgb(148 163 184) 50%, rgb(16 185 129) 100%)',
          }}
        >
          {/* Точка-метка + лейбл со значением */}
          <div
            className="absolute -top-1.5 -translate-x-1/2 flex flex-col items-center"
            style={{ left: `${dotLeftPct}%` }}
            title={`Индекс: ${indexRounded}`}
          >
            <div className="w-5 h-5 rounded-full bg-card border-2 border-text-primary shadow" />
            <span className="mt-1 text-[11px] font-semibold text-text-primary tabular-nums">
              {indexRounded > 0 ? '+' : ''}
              {indexRounded}
            </span>
          </div>
        </div>
        <div className="mt-7 flex justify-between text-[10px] text-text-tertiary tabular-nums">
          <span>−100</span>
          <span>0</span>
          <span>+100</span>
        </div>
      </div>

      {/* Динамика к прошлому месяцу */}
      <DynamicLine
        dynamic={dynamic}
        currentRounded={indexRounded}
        previousRounded={previousRounded}
      />

      <div className="text-[11px] text-text-tertiary mt-auto">
        по {cur.totalNonEmpty} {pluralize(cur.totalNonEmpty, ['отзыву', 'отзывам', 'отзывам'])} с оценкой LLM
      </div>
    </Card>
  )
}

function Header() {
  return (
    <div className="flex items-baseline justify-between gap-2">
      <div className="min-w-0">
        <div className="text-sm font-semibold text-text-primary">Свежий пульс</div>
        <div className="text-[11px] text-text-tertiary">за последний месяц</div>
      </div>
      <span className="text-[10px] uppercase tracking-wide text-text-tertiary font-mono">М4</span>
    </div>
  )
}

// Таблица фраз из спеки М4. Используем точное (не округлённое) значение,
// чтобы на границах правил поведение совпадало с описанием.
function pickVerdict(index: number): { text: string; colorClass: string } {
  if (index >= 50) return { text: 'Клиенты в восторге', colorClass: 'text-emerald-700' }
  if (index >= 20) return { text: 'Клиенты довольны', colorClass: 'text-emerald-600' }
  if (index > -20) return { text: 'Смешанные настроения', colorClass: 'text-slate-600' }
  if (index > -50) return { text: 'Клиенты недовольны', colorClass: 'text-orange-600' }
  return { text: 'Клиенты раздражены', colorClass: 'text-rose-600' }
}

interface DynamicState {
  tone: 'good' | 'bad' | 'flat' | 'unavailable'
  diff: number
}

function computeDynamic(currentIndex: number, previousIndex: number | null): DynamicState {
  if (previousIndex === null) return { tone: 'unavailable', diff: 0 }
  const diff = Math.round(currentIndex) - Math.round(previousIndex)
  if (diff >= 5) return { tone: 'good', diff }
  if (diff <= -5) return { tone: 'bad', diff }
  return { tone: 'flat', diff }
}

function DynamicLine({
  dynamic,
  currentRounded,
  previousRounded,
}: {
  dynamic: DynamicState
  currentRounded: number
  previousRounded: number | null
}) {
  // Полная подсказка с явными «было / стало» — снимает двусмысленность
  // прочтения «−N к прошлому месяцу» как абсолютного значения прошлого месяца.
  const tooltip =
    previousRounded === null
      ? undefined
      : `Прошлый месяц: ${previousRounded > 0 ? '+' : ''}${previousRounded}. Сейчас: ${
          currentRounded > 0 ? '+' : ''
        }${currentRounded}`

  if (dynamic.tone === 'unavailable') {
    return (
      <div className="text-xs text-text-tertiary">— нет данных за предыдущий месяц</div>
    )
  }
  if (dynamic.tone === 'flat') {
    return (
      <div
        className="flex items-center gap-1 text-xs text-text-tertiary"
        title={tooltip}
      >
        <ArrowRight size={12} strokeWidth={2.5} />
        Без изменений к прошлому месяцу
      </div>
    )
  }
  const Icon = dynamic.tone === 'good' ? ArrowUp : ArrowDown
  const color = dynamic.tone === 'good' ? 'text-emerald-600' : 'text-rose-600'
  const sign = dynamic.diff > 0 ? '+' : '−'
  const abs = Math.abs(dynamic.diff)
  return (
    <div
      className={cn('flex items-center gap-1 text-xs font-medium tabular-nums', color)}
      title={tooltip}
    >
      <Icon size={12} strokeWidth={2.5} />
      {sign}
      {abs} {pluralize(abs, ['пункт', 'пункта', 'пунктов'])} к прошлому месяцу
    </div>
  )
}

function pluralize(n: number, forms: [string, string, string]): string {
  const mod10 = n % 10
  const mod100 = n % 100
  if (mod10 === 1 && mod100 !== 11) return forms[0]
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return forms[1]
  return forms[2]
}

import { AlertCircle, ArrowDown, ArrowRight, ArrowUp } from 'lucide-react'
import { Card } from '@/components/ui/card'
import { cn } from '@/lib/utils'

// Стрелка + абсолютная разница к предыдущему периоду такой же длительности.
// Цвета: рост = зелёный, падение = красный, без изменений = серый.
// «—» когда: (1) нет периода, (2) обе нули (нет данных в обоих периодах).
//
// «Обе нули = —» — прагматичная эвристика: без доп. SQL не различить
// «не было данных за prev» vs «реальный 0=0». Спека просит прочерк в первом
// случае, мы покрываем оба и реальный 0=0 (где тренд всё равно неинформативен).
export function TrendLine({
  current,
  previous,
  hasPrev,
  size,
}: {
  current: number
  previous: number
  hasPrev: boolean
  size: 'sm' | 'lg'
}) {
  if (!hasPrev || (current === 0 && previous === 0)) {
    return (
      <span
        className={cn(
          'inline-flex items-center text-text-tertiary',
          size === 'lg' ? 'text-sm' : 'text-xs',
        )}
        title={
          hasPrev
            ? 'Нет отзывов ни за выбранный, ни за предыдущий период'
            : 'Период не выбран — тренд недоступен'
        }
      >
        —
      </span>
    )
  }

  const diff = current - previous
  const Icon = diff > 0 ? ArrowUp : diff < 0 ? ArrowDown : ArrowRight
  const color =
    diff > 0
      ? 'text-emerald-600'
      : diff < 0
      ? 'text-rose-600'
      : 'text-text-tertiary'

  const absDiff = Math.abs(diff).toLocaleString('ru-RU')
  const sign = diff > 0 ? '+' : diff < 0 ? '−' : ''
  return (
    <span
      className={cn(
        'inline-flex items-center gap-0.5 tabular-nums',
        color,
        size === 'lg' ? 'text-sm font-medium' : 'text-xs',
      )}
      title={`предыдущий период: ${previous.toLocaleString('ru-RU')}`}
    >
      <Icon size={size === 'lg' ? 14 : 12} strokeWidth={2.5} />
      {sign}
      {absDiff}
    </span>
  )
}

export function MetricSkeletonCard({ minHeight = '14rem' }: { minHeight?: string }) {
  return (
    <Card
      className="p-5 flex items-center justify-center text-xs text-text-tertiary"
      style={{ minHeight }}
    >
      Загружаем…
    </Card>
  )
}

export function MetricErrorCard({ message }: { message: string }) {
  return (
    <Card className="p-5 border-rose-200 bg-rose-50">
      <div className="flex items-start gap-2 text-sm">
        <AlertCircle size={16} className="text-rose-700 shrink-0 mt-0.5" />
        <div className="min-w-0">
          <div className="font-semibold text-rose-900 mb-0.5">
            Не удалось посчитать метрику
          </div>
          <div className="text-rose-800 text-xs break-words">{message}</div>
        </div>
      </div>
    </Card>
  )
}

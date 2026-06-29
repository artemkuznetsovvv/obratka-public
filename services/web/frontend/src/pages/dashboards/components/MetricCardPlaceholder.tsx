import { Sparkles } from 'lucide-react'
import { Card } from '@/components/ui/card'
import { cn } from '@/lib/utils'

export interface MetricCardPlaceholderProps {
  // Короткое название карточки («Количество отзывов»).
  title: string
  // Опц. слаг метрики (для отладки/будущего mapping'а на компонент).
  slug?: string
  // Опц. подсказка справа от иконки (например «окно 30 дней»).
  hint?: string
  // grid-area для нестандартных сеток. По умолчанию занимает 1 ячейку.
  className?: string
  // Минимальная высота — чтобы все placeholder'ы в ряду были одинакового размера.
  minHeight?: string
}

// Пустышка под будущую карточку метрики. При реализации конкретной метрики
// файл MetricXxx.tsx заменяет этот placeholder в соответствующем месте секции.
export function MetricCardPlaceholder({
  title,
  slug,
  hint,
  className,
  minHeight = '7rem',
}: MetricCardPlaceholderProps) {
  return (
    <Card
      className={cn(
        'p-4 flex flex-col justify-between border-dashed border-border-subtle bg-page-bg/30',
        className,
      )}
      style={{ minHeight }}
    >
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <div className="text-sm font-semibold text-text-primary truncate">{title}</div>
          {hint && (
            <div className="text-[11px] text-text-tertiary mt-0.5">{hint}</div>
          )}
        </div>
        {slug && (
          <span className="text-[10px] uppercase tracking-wide text-text-tertiary font-mono shrink-0">
            {slug}
          </span>
        )}
      </div>
      <div className="flex items-center gap-1.5 text-xs text-text-tertiary">
        <Sparkles size={12} />
        Скоро
      </div>
    </Card>
  )
}

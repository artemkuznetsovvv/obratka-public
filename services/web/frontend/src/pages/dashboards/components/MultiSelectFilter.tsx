import { Check, ChevronDown } from 'lucide-react'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'

export interface MultiSelectOption<T extends string | number> {
  value: T
  label: string
  // Опц. цветная плашка (для источников: 2gis = зелёный и т.д.).
  badgeClass?: string
}

export interface MultiSelectFilterProps<T extends string | number> {
  label: string
  options: readonly MultiSelectOption<T>[]
  selected: readonly T[]
  onChange: (next: T[]) => void
  // Если true — кнопка disabled (для filterа Тема в итерации 2).
  disabled?: boolean
  disabledHint?: string
  // Минимальная ширина триггера (иначе подстраивается под контент).
  triggerClassName?: string
}

// Универсальный multi-select на DropdownMenu. Чекбоксы — простые квадратики
// (нет Radix Checkbox в проекте), управляются через onSelect с preventDefault,
// чтобы клик не закрывал dropdown — селект мульти-выбора.
export function MultiSelectFilter<T extends string | number>({
  label,
  options,
  selected,
  onChange,
  disabled,
  disabledHint,
  triggerClassName,
}: MultiSelectFilterProps<T>) {
  const selectedSet = new Set(selected)
  const selectedCount = selectedSet.size
  const total = options.length

  const summary =
    selectedCount === 0
      ? 'не выбрано'
      : selectedCount === total
      ? 'все'
      : `${selectedCount} из ${total}`

  const toggle = (v: T) => {
    if (selectedSet.has(v)) {
      onChange(selected.filter((x) => x !== v))
    } else {
      onChange([...selected, v])
    }
  }

  const allOn = () => onChange(options.map((o) => o.value))
  const allOff = () => onChange([])

  const trigger = (
    <Button
      type="button"
      variant="outline"
      size="sm"
      disabled={disabled}
      title={disabled ? disabledHint : undefined}
      className={cn(
        'h-9 justify-between gap-2 font-normal',
        triggerClassName,
      )}
    >
      <span className="flex items-baseline gap-1.5 min-w-0">
        <span className="text-text-tertiary text-xs uppercase tracking-wide">{label}</span>
        <span
          className={cn(
            'text-sm font-medium truncate',
            selectedCount === 0 ? 'text-text-tertiary' : 'text-text-primary',
          )}
        >
          {summary}
        </span>
      </span>
      <ChevronDown size={14} className="text-text-tertiary shrink-0" />
    </Button>
  )

  if (disabled) {
    return trigger
  }

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>{trigger}</DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="min-w-[16rem] max-w-[20rem]">
        <div className="flex items-center justify-between px-2 pt-1 pb-1.5">
          <span className="text-[11px] uppercase tracking-wide text-text-tertiary">{label}</span>
          <div className="flex items-center gap-1.5">
            <button
              type="button"
              onClick={allOn}
              className="text-[11px] text-brand hover:underline"
            >
              Все
            </button>
            <span className="text-text-tertiary text-[11px]">·</span>
            <button
              type="button"
              onClick={allOff}
              className="text-[11px] text-text-tertiary hover:text-text-primary hover:underline"
            >
              Снять
            </button>
          </div>
        </div>
        <ul className="max-h-[20rem] overflow-y-auto py-0.5">
          {options.map((o) => {
            const checked = selectedSet.has(o.value)
            return (
              <li key={String(o.value)}>
                <button
                  type="button"
                  onClick={() => toggle(o.value)}
                  className={cn(
                    'w-full flex items-center gap-2 rounded-lg px-2.5 py-1.5 text-sm text-left transition-colors',
                    'hover:bg-state-active-bg',
                    checked && 'text-text-primary',
                    !checked && 'text-text-secondary',
                  )}
                >
                  <span
                    className={cn(
                      'w-4 h-4 rounded border flex items-center justify-center shrink-0 transition-colors',
                      checked
                        ? 'bg-brand border-brand text-white'
                        : 'border-border-subtle bg-card',
                    )}
                  >
                    {checked && <Check size={12} strokeWidth={3} />}
                  </span>
                  {o.badgeClass ? (
                    <span
                      className={cn(
                        'inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-semibold',
                        o.badgeClass,
                      )}
                    >
                      {o.label}
                    </span>
                  ) : (
                    <span className="truncate">{o.label}</span>
                  )}
                </button>
              </li>
            )
          })}
        </ul>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

import { ChevronLeft, ChevronRight } from 'lucide-react'
import { DayPicker, type DayPickerProps } from 'react-day-picker'
import { ru } from 'date-fns/locale'
import { cn } from '@/lib/utils'
import 'react-day-picker/dist/style.css'

// Кастомизация DayPicker (v10) под нашу палитру (brand, state-active-bg).
// Локаль ru, navLayout="around" — кнопки prev/next с двух сторон caption'а
// (а не обе справа, как по дефолту RDP).
//
// СОЗНАТЕЛЬНО НЕ переопределяем classNames для root/nav/buttons/month —
// используем встроенный CSS RDP (react-day-picker/dist/style.css). Любые
// мои overrides на nav/button_* ломали hit area стрелок. Кастомизируем
// только то, что реально нужно для бренда — цвет выделения дней.
export function Calendar({
  className,
  classNames,
  showOutsideDays = true,
  ...props
}: DayPickerProps) {
  return (
    <DayPicker
      locale={ru}
      navLayout="around"
      showOutsideDays={showOutsideDays}
      className={cn(className)}
      classNames={{
        // Только цветовая кастомизация выделения дней под brand-палитру.
        // Остальное (nav, layout) — встроенный CSS RDP.
        selected: cn('[&>button]:bg-brand [&>button]:text-white [&>button]:hover:bg-brand-hover'),
        range_start: cn('[&>button]:bg-brand [&>button]:text-white [&>button]:rounded-r-none'),
        range_end: cn('[&>button]:bg-brand [&>button]:text-white [&>button]:rounded-l-none'),
        range_middle: cn(
          '[&>button]:bg-state-active-bg [&>button]:text-brand [&>button]:rounded-none',
        ),
        today: '[&>button]:font-bold [&>button]:underline underline-offset-4',
        ...classNames,
      }}
      components={{
        Chevron: ({ orientation }) =>
          orientation === 'left' ? (
            <ChevronLeft size={16} />
          ) : (
            <ChevronRight size={16} />
          ),
      }}
      {...props}
    />
  )
}

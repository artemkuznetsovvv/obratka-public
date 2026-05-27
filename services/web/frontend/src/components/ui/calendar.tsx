import { ChevronLeft, ChevronRight } from 'lucide-react'
import { DayPicker, type DayPickerProps } from 'react-day-picker'
import { ru } from 'date-fns/locale'
import { cn } from '@/lib/utils'
import 'react-day-picker/dist/style.css'

// Кастомизация DayPicker (v10) под нашу палитру (brand, page-bg,
// text-text-primary). Локаль ru. Используется в фильтре периода дашборда.
// Структура classNames следует shadcn-паттерну для RDP 9/10: кнопки
// previous/next позиционированы absolute внутри month_caption, без
// конкуренции с caption_label.
export function Calendar({
  className,
  classNames,
  showOutsideDays = true,
  ...props
}: DayPickerProps) {
  return (
    <DayPicker
      locale={ru}
      showOutsideDays={showOutsideDays}
      className={cn('p-1', className)}
      classNames={{
        months: 'flex flex-col sm:flex-row gap-4 relative',
        month: 'space-y-3',
        month_caption:
          'flex justify-center pt-1 relative items-center h-8 text-sm font-medium',
        caption_label: 'text-sm font-medium capitalize',
        nav: 'absolute inset-x-0 top-1 flex items-center justify-between px-1 pointer-events-none',
        button_previous: cn(
          'h-7 w-7 inline-flex items-center justify-center rounded-md',
          'text-text-secondary hover:bg-page-bg hover:text-text-primary transition-colors',
          'pointer-events-auto',
        ),
        button_next: cn(
          'h-7 w-7 inline-flex items-center justify-center rounded-md',
          'text-text-secondary hover:bg-page-bg hover:text-text-primary transition-colors',
          'pointer-events-auto',
        ),
        month_grid: 'w-full border-collapse',
        weekdays: 'flex',
        weekday: 'text-text-tertiary w-9 font-normal text-[11px] uppercase',
        week: 'flex w-full mt-1',
        day: 'h-9 w-9 text-center text-sm relative p-0',
        day_button: cn(
          'h-9 w-9 inline-flex items-center justify-center rounded-md',
          'text-text-primary hover:bg-page-bg hover:text-text-primary',
          'focus:outline-none focus:ring-2 focus:ring-brand/40',
          'aria-selected:opacity-100',
        ),
        selected: cn(
          '[&>button]:bg-brand [&>button]:text-white [&>button]:hover:bg-brand-hover',
        ),
        range_start: cn('[&>button]:bg-brand [&>button]:text-white [&>button]:rounded-r-none'),
        range_end: cn('[&>button]:bg-brand [&>button]:text-white [&>button]:rounded-l-none'),
        range_middle: cn(
          '[&>button]:bg-state-active-bg [&>button]:text-brand [&>button]:rounded-none',
        ),
        today: '[&>button]:font-bold [&>button]:underline underline-offset-4',
        outside: '[&>button]:text-text-tertiary [&>button]:opacity-50',
        disabled: '[&>button]:text-text-tertiary [&>button]:opacity-40 [&>button]:cursor-not-allowed',
        hidden: 'invisible',
        ...classNames,
      }}
      components={{
        Chevron: ({ orientation }) =>
          orientation === 'left' ? (
            <ChevronLeft size={14} />
          ) : (
            <ChevronRight size={14} />
          ),
      }}
      {...props}
    />
  )
}

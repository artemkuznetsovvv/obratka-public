import { ChevronLeft, ChevronRight } from 'lucide-react'
import { DayPicker, type DayPickerProps } from 'react-day-picker'
import { ru } from 'date-fns/locale'
import { cn } from '@/lib/utils'
import 'react-day-picker/dist/style.css'

// Кастомизация DayPicker под нашу палитру (brand, page-bg, text-text-primary).
// Локаль ru. Используется в фильтре периода дашборда, при необходимости —
// в других местах через ту же обёртку.
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
        months: 'flex flex-col sm:flex-row gap-4',
        month: 'space-y-3',
        month_caption: 'flex items-center justify-center pt-1 relative text-sm font-medium',
        caption_label: 'text-sm font-medium capitalize',
        nav: 'flex items-center justify-between absolute inset-x-1 top-1',
        button_previous: cn(
          'h-7 w-7 inline-flex items-center justify-center rounded-md',
          'text-text-secondary hover:bg-page-bg hover:text-text-primary transition-colors',
        ),
        button_next: cn(
          'h-7 w-7 inline-flex items-center justify-center rounded-md',
          'text-text-secondary hover:bg-page-bg hover:text-text-primary transition-colors',
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

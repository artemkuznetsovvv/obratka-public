import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'
import type { AnalysisPeriod } from './wizardState'

interface Preset {
  label: string
  days: number
}

const PRESETS: Preset[] = [
  { label: 'Последние 7 дней', days: 7 },
  { label: 'Последние 30 дней', days: 30 },
  { label: 'Последние 3 месяца', days: 90 },
  { label: 'Последний год', days: 365 },
]

function isoToday(): string {
  const now = new Date()
  return formatYmd(now)
}

function isoDaysAgo(days: number): string {
  const d = new Date()
  d.setDate(d.getDate() - days)
  return formatYmd(d)
}

function formatYmd(d: Date): string {
  const y = d.getFullYear()
  const m = String(d.getMonth() + 1).padStart(2, '0')
  const day = String(d.getDate()).padStart(2, '0')
  return `${y}-${m}-${day}`
}

interface PeriodPickerProps {
  value: AnalysisPeriod
  onChange: (period: AnalysisPeriod) => void
}

export function PeriodPicker({ value, onChange }: PeriodPickerProps) {
  const isRange = value.kind === 'range'
  const today = isoToday()

  const setRange = (from: string, to: string) => onChange({ kind: 'range', from, to })

  const applyPreset = (days: number) => setRange(isoDaysAgo(days), today)

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap gap-2">
        <ModeButton
          active={isRange}
          onClick={() => setRange(isoDaysAgo(30), today)}
        >
          Диапазон дат
        </ModeButton>
        <ModeButton
          active={!isRange}
          onClick={() => onChange({ kind: 'since-beginning' })}
        >
          С самого начала
        </ModeButton>
      </div>

      {isRange && (
        <>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <div>
              <label className="block text-xs text-text-secondary mb-1">От</label>
              <Input
                type="date"
                value={value.from}
                max={value.to || today}
                onChange={(e) => setRange(e.target.value, value.to)}
              />
            </div>
            <div>
              <label className="block text-xs text-text-secondary mb-1">До</label>
              <Input
                type="date"
                value={value.to}
                min={value.from}
                max={today}
                onChange={(e) => setRange(value.from, e.target.value)}
              />
            </div>
          </div>

          <div className="flex flex-wrap gap-2">
            {PRESETS.map((p) => (
              <button
                key={p.days}
                type="button"
                onClick={() => applyPreset(p.days)}
                className="px-3 py-1.5 rounded-full border border-border-subtle bg-card text-xs text-text-secondary hover:border-brand/30 hover:text-brand transition-colors"
              >
                {p.label}
              </button>
            ))}
          </div>
        </>
      )}

      {!isRange && (
        <p className="text-xs text-text-tertiary">
          Анализируем все отзывы, которые удастся собрать. Хорошо для первой оценки тональности
          или если непонятен горизонт — но сбор займёт больше времени.
        </p>
      )}
    </div>
  )
}

function ModeButton({
  active,
  onClick,
  children,
}: {
  active: boolean
  onClick: () => void
  children: React.ReactNode
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        'px-4 py-2 rounded-full text-sm font-medium transition-colors',
        active
          ? 'bg-state-active-bg text-brand border border-brand/30'
          : 'border border-border-subtle text-text-secondary hover:border-brand/30',
      )}
    >
      {children}
    </button>
  )
}

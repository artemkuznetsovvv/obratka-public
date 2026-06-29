import { useEffect, useState } from 'react'
import { Bell, Loader2, MapPin } from 'lucide-react'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { SOURCE_LABEL } from '@/pages/history/analysisStatus'
import {
  DEFAULT_WINDOW_DAYS,
  FREQUENCY_LABEL,
  WINDOW_DAYS_LABEL,
  WINDOW_DAYS_OPTIONS,
  frequenciesForRole,
  type MonitoringFrequency,
} from '@/api/monitorings'

export interface BranchOption {
  branchId: string
  name: string | null
  address: string | null
  city: string | null
}

export interface MonitoringConfigValues {
  sources: string[]
  branchIds: string[]
  frequency: MonitoringFrequency
  windowDays: number
}

interface Props {
  open: boolean
  onOpenChange: (open: boolean) => void
  title: string
  submitLabel: string
  isAdmin: boolean
  availableSources: string[]
  availableBranches: BranchOption[]
  initial?: Partial<MonitoringConfigValues>
  submitting?: boolean
  errorMessage?: string | null
  onSubmit: (values: MonitoringConfigValues) => void
}

export function MonitoringConfigDialog({
  open,
  onOpenChange,
  title,
  submitLabel,
  isAdmin,
  availableSources,
  availableBranches,
  initial,
  submitting,
  errorMessage,
  onSubmit,
}: Props) {
  const freqOptions = frequenciesForRole(isAdmin)
  const defaultFreq: MonitoringFrequency = isAdmin ? 'Daily' : 'Daily'

  const [sources, setSources] = useState<Set<string>>(new Set())
  const [branchIds, setBranchIds] = useState<Set<string>>(new Set())
  const [frequency, setFrequency] = useState<MonitoringFrequency>(defaultFreq)
  const [windowDays, setWindowDays] = useState<number>(DEFAULT_WINDOW_DAYS)
  const [localError, setLocalError] = useState<string | null>(null)

  // Сброс/инициализация при открытии: по умолчанию выбраны все источники/филиалы анализа.
  useEffect(() => {
    if (!open) return
    setSources(new Set(initial?.sources ?? availableSources))
    setBranchIds(new Set(initial?.branchIds ?? availableBranches.map((b) => b.branchId)))
    const initFreq = initial?.frequency
    setFrequency(initFreq && freqOptions.includes(initFreq) ? initFreq : defaultFreq)
    const initWindow = initial?.windowDays
    setWindowDays(
      initWindow && (WINDOW_DAYS_OPTIONS as readonly number[]).includes(initWindow)
        ? initWindow
        : DEFAULT_WINDOW_DAYS,
    )
    setLocalError(null)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open])

  const toggle = (set: Set<string>, value: string): Set<string> => {
    const next = new Set(set)
    if (next.has(value)) next.delete(value)
    else next.add(value)
    return next
  }

  const submit = () => {
    if (sources.size === 0) {
      setLocalError('Выберите хотя бы один источник.')
      return
    }
    if (branchIds.size === 0) {
      setLocalError('Выберите хотя бы один филиал.')
      return
    }
    setLocalError(null)
    onSubmit({
      sources: [...sources],
      branchIds: [...branchIds],
      frequency,
      windowDays,
    })
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-xl max-h-[85vh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <Bell size={18} className="text-brand" />
            {title}
          </DialogTitle>
          <DialogDescription>
            Система будет регулярно дособирать только новые отзывы и обновлять дашборд.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-5">
          {/* Источники */}
          <Field label="Источники">
            <div className="flex flex-wrap gap-2">
              {availableSources.map((s) => (
                <Chip
                  key={s}
                  selected={sources.has(s)}
                  onClick={() => setSources((prev) => toggle(prev, s))}
                >
                  {SOURCE_LABEL[s] ?? s}
                </Chip>
              ))}
            </div>
          </Field>

          {/* Филиалы */}
          <Field label="Филиалы">
            <div className="space-y-1.5 max-h-52 overflow-y-auto pr-1">
              {availableBranches.map((b) => {
                const checked = branchIds.has(b.branchId)
                const label = b.address ?? b.name ?? 'Филиал'
                // Единственный филиал нельзя снять — иначе мониторинг остался бы без филиалов.
                const locked = availableBranches.length === 1
                return (
                  <button
                    type="button"
                    key={b.branchId}
                    onClick={locked ? undefined : () => setBranchIds((prev) => toggle(prev, b.branchId))}
                    disabled={locked}
                    title={locked ? 'Единственный филиал — обязателен для мониторинга' : undefined}
                    className={cn(
                      'w-full flex items-start gap-2 rounded-lg border px-3 py-2 text-left transition-colors',
                      checked
                        ? 'border-brand bg-state-active-bg'
                        : 'border-border-subtle bg-card hover:bg-page-bg',
                      locked && 'cursor-default',
                    )}
                  >
                    <span
                      className={cn(
                        'mt-0.5 flex h-4 w-4 shrink-0 items-center justify-center rounded border',
                        checked ? 'bg-brand border-brand text-white' : 'border-border-subtle',
                      )}
                    >
                      {checked && <span className="text-[10px] leading-none">✓</span>}
                    </span>
                    <span className="min-w-0">
                      <span className="block text-sm text-text-primary truncate flex items-center gap-1">
                        <MapPin size={12} className="text-text-tertiary shrink-0" />
                        <span className="truncate">{label}</span>
                      </span>
                      {b.city && <span className="block text-xs text-text-tertiary">{b.city}</span>}
                    </span>
                  </button>
                )
              })}
              {availableBranches.length === 0 && (
                <div className="text-sm text-text-tertiary">Нет доступных филиалов.</div>
              )}
            </div>
          </Field>

          {/* Частота */}
          <Field label="Частота обновления">
            <div className="flex flex-wrap gap-2">
              {freqOptions.map((f) => (
                <Chip key={f} selected={frequency === f} onClick={() => setFrequency(f)}>
                  {FREQUENCY_LABEL[f]}
                </Chip>
              ))}
            </div>
            {isAdmin && (
              <p className="mt-1.5 text-xs text-text-tertiary">
                Частые интервалы (10/30 мин) доступны только администратору — для тестирования.
              </p>
            )}
          </Field>

          {/* Период окна — за какой последний период показываются данные в дашборде. */}
          <Field label="Период окна">
            <div className="flex flex-wrap gap-2">
              {WINDOW_DAYS_OPTIONS.map((d) => (
                <Chip key={d} selected={windowDays === d} onClick={() => setWindowDays(d)}>
                  {WINDOW_DAYS_LABEL[d]}
                </Chip>
              ))}
            </div>
          </Field>

          {(localError || errorMessage) && (
            <div className="rounded-lg bg-destructive/10 px-3 py-2 text-sm text-destructive">
              {localError ?? errorMessage}
            </div>
          )}
        </div>

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={submitting}>
            Отмена
          </Button>
          <Button onClick={submit} disabled={submitting} className="gap-2">
            {submitting && <Loader2 size={14} className="animate-spin" />}
            {submitLabel}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <div className="mb-2 text-sm font-medium text-text-primary">{label}</div>
      {children}
    </div>
  )
}

function Chip({
  selected,
  onClick,
  children,
}: {
  selected: boolean
  onClick: () => void
  children: React.ReactNode
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        'rounded-full border px-3 py-1.5 text-sm transition-colors',
        selected
          ? 'border-brand bg-brand text-white'
          : 'border-border-subtle bg-card text-text-secondary hover:bg-page-bg',
      )}
    >
      {children}
    </button>
  )
}

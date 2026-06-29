import { Check } from 'lucide-react'
import { cn } from '@/lib/utils'

export type AnalysisStep = 1 | 2 | 3

const steps = [
  { id: 1, label: 'Шаг 1: Параметры' },
  { id: 2, label: 'Шаг 2: Филиалы' },
  { id: 3, label: 'Шаг 3: Запуск' },
] as const

export function AnalysisStepper({ current }: { current: AnalysisStep }) {
  return (
    <div className="mb-10 flex items-center gap-4 flex-wrap">
      {steps.map((step, idx) => {
        const state: 'done' | 'active' | 'pending' =
          step.id < current ? 'done' : step.id === current ? 'active' : 'pending'
        const isLast = idx === steps.length - 1
        return (
          <div key={step.id} className="flex items-center gap-4">
            <div
              className={cn(
                'flex items-center gap-2 px-4 py-2 rounded-full text-sm font-medium transition-colors',
                state === 'active' && 'bg-card border border-brand/30 text-brand shadow-sm',
                state === 'done' && 'text-text-secondary',
                state === 'pending' && 'text-text-tertiary',
              )}
            >
              <span
                className={cn(
                  'flex items-center justify-center w-6 h-6 rounded-full text-[12px] font-bold',
                  state === 'active' && 'bg-brand text-white',
                  state === 'done' && 'bg-sentiment-positive/15 text-sentiment-positive',
                  state === 'pending' && 'bg-page-bg text-text-tertiary',
                )}
              >
                {state === 'done' ? <Check size={14} /> : step.id}
              </span>
              <span>{step.label}</span>
            </div>
            {!isLast && <div className="h-px w-12 bg-border-subtle" />}
          </div>
        )
      })}
    </div>
  )
}

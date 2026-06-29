import { useMemo } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import {
  ArrowRight,
  Building2,
  CalendarRange,
  ChevronRight,
  FileEdit,
  Layers,
  Plus,
  RefreshCcw,
} from 'lucide-react'
import { AppLayout } from '@/layouts/AppLayout'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { analysesApi, type AnalysisJob } from '@/api/analyses'
import { companiesApi, type CompanyDto } from '@/api/companies'
import { cn } from '@/lib/utils'
import {
  approximateProgress,
  isTerminal,
  localizeAnalysisError,
  SOURCE_LABEL,
  statusMetaFor,
} from './analysisStatus'

export default function HistoryListPage() {
  const navigate = useNavigate()

  const analysesQuery = useQuery({
    queryKey: ['analyses', 'mine'],
    queryFn: () => analysesApi.list({ limit: 100 }),
    // Поллим раз в 5 секунд, ПОКА есть хоть один non-terminal job в выдаче.
    // Когда все завершены — переходим на пассивный режим (refetchInterval=false).
    refetchInterval: (q) => {
      const data = q.state.data
      if (!data) return 5000
      const hasRunning = data.items.some((i) => !isTerminal(i.status))
      return hasRunning ? 5000 : false
    },
    refetchIntervalInBackground: false,
  })

  // Маппа companyId → name. Загружаем один раз — список меняется редко.
  const companiesQuery = useQuery({
    queryKey: ['companies', 'mine'],
    queryFn: () => companiesApi.listMine(),
    staleTime: 60_000,
  })

  const companyName = useMemo(() => {
    const map = new Map<string, string>()
    for (const c of companiesQuery.data ?? []) map.set(c.id, c.name)
    return (id: string) => map.get(id) ?? id.slice(0, 8) + '…'
  }, [companiesQuery.data])

  const items = analysesQuery.data?.items ?? []

  // «Черновики» = компании с уже сгруппированными филиалами, по которым ещё ни разу
  // не запускался анализ. Юзер мог дойти до шага 3 и закрыть вкладку — пусть видит
  // entry-point чтобы вернуться и продолжить.
  const drafts = useMemo<CompanyDto[]>(() => {
    const companies = companiesQuery.data ?? []
    if (companies.length === 0) return []
    const companiesWithJobs = new Set<string>()
    for (const j of items) companiesWithJobs.add(j.companyId)
    return companies.filter(
      (c) => c.logicalBranchCount > 0 && !companiesWithJobs.has(c.id),
    )
  }, [companiesQuery.data, items])

  return (
    <AppLayout breadcrumbs={[{ label: 'История анализов' }]}>
      <div className="max-w-4xl mx-auto">
        <div className="mb-8 flex items-start justify-between gap-4">
          <div>
            <h1 className="text-h1 text-text-primary mb-2">История анализов</h1>
            <p className="text-body text-text-secondary">
              Все запущенные анализы по вашим компаниям. Активные обновляются каждые 5 секунд.
            </p>
          </div>
          <Button onClick={() => navigate('/analyses/new')} className="gap-2 shrink-0">
            <Plus size={16} />
            Новый анализ
          </Button>
        </div>

        {drafts.length > 0 && (
          <section className="mb-8">
            <div className="mb-3 flex items-center gap-2 text-xs uppercase tracking-wide text-text-tertiary">
              <FileEdit size={12} />
              Продолжить настройку
            </div>
            <div className="space-y-2">
              {drafts.map((c) => (
                <DraftCard key={c.id} company={c} />
              ))}
            </div>
          </section>
        )}

        {analysesQuery.isLoading ? (
          <Card className="p-8 text-text-secondary">Загружаем историю…</Card>
        ) : analysesQuery.isError ? (
          <Card className="p-8 text-destructive">
            Не удалось загрузить историю: {(analysesQuery.error as Error).message}
          </Card>
        ) : items.length === 0 && drafts.length === 0 ? (
          <Card className="p-10 text-center">
            <div className="text-text-primary text-h3 mb-2">Анализов пока нет</div>
            <div className="text-sm text-text-secondary mb-6">
              Запустите первый анализ, чтобы здесь появились карточки с прогрессом и результатами.
            </div>
            <Button onClick={() => navigate('/analyses/new')} className="gap-2">
              <Plus size={16} />
              Запустить первый анализ
            </Button>
          </Card>
        ) : items.length === 0 ? null : (
          <div className="space-y-3">
            {items.map((job) => (
              <AnalysisCard key={job.id} job={job} companyName={companyName(job.companyId)} />
            ))}
          </div>
        )}
      </div>
    </AppLayout>
  )
}

function AnalysisCard({ job, companyName }: { job: AnalysisJob; companyName: string }) {
  const meta = statusMetaFor(job.status)
  const progress = approximateProgress(job.status, job.collectionProgress)
  const isActive = !isTerminal(job.status)

  return (
    <Link to={`/history/${job.id}`} className="block group">
      <Card className="p-5 transition-colors group-hover:border-brand/30">
        <div className="flex flex-col sm:flex-row sm:items-start gap-3 sm:gap-6">
          <div className="flex-1 min-w-0">
            <div className="flex items-center gap-2 flex-wrap mb-1.5">
              <span
                className={cn(
                  'inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-semibold border',
                  meta.badge,
                )}
              >
                {meta.label}
              </span>
              {isActive && (
                <RefreshCcw size={12} className="text-text-tertiary animate-spin [animation-duration:3s]" />
              )}
            </div>
            <div className="flex items-center gap-2 text-text-primary mb-1">
              <Building2 size={14} className="text-text-tertiary shrink-0" />
              <span className="text-sm font-semibold truncate">{companyName}</span>
            </div>
            <div className="flex items-center gap-2 text-xs text-text-tertiary">
              <CalendarRange size={12} />
              <span>Запущен: {formatDateTime(job.createdAt)}</span>
              {job.completedAt && (
                <span>· Завершён: {formatDateTime(job.completedAt)}</span>
              )}
            </div>
            {job.error && (
              <div
                className="mt-2 text-xs text-rose-700 truncate"
                title={localizeAnalysisError(job.error) ?? undefined}
              >
                {localizeAnalysisError(job.error)}
              </div>
            )}
          </div>
          <div className="flex-1 min-w-0 sm:max-w-[280px]">
            <ProgressBar value={progress} tone={meta.tone} />
            <div className="mt-2 flex items-center justify-between text-xs text-text-tertiary">
              <span>{isActive ? `Прогресс: ${progress}%` : 'Готово'}</span>
              <span>
                {job.reviewCount > 0
                  ? `${job.reviewCount.toLocaleString('ru-RU')} отзывов`
                  : '— отзывов'}
              </span>
            </div>
          </div>
          <ArrowRight
            size={18}
            className="hidden sm:block text-text-tertiary group-hover:text-brand transition-colors self-center shrink-0"
          />
        </div>
      </Card>
    </Link>
  )
}

function DraftCard({ company }: { company: CompanyDto }) {
  const periodLabel = formatDraftPeriod(company.draftPeriodFrom, company.draftPeriodTo)
  const sourcesLabel = (company.draftSources && company.draftSources.length > 0
    ? company.draftSources
    : []
  )
    .map((s) => SOURCE_LABEL[s] ?? s)
    .join(', ')

  return (
    <Link to={`/analyses/new/${company.id}/summary`} className="block group">
      <Card className="p-4 transition-colors group-hover:border-brand/40 border-dashed">
        <div className="flex items-center gap-4">
          <div className="flex-1 min-w-0">
            <div className="flex items-center gap-2 mb-1">
              <Building2 size={14} className="text-text-tertiary shrink-0" />
              <span className="text-sm font-semibold text-text-primary truncate">
                {company.name}
              </span>
              <span className="inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-medium bg-amber-50 text-amber-800 border border-amber-200 shrink-0">
                Черновик
              </span>
            </div>
            <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-text-tertiary">
              <span className="inline-flex items-center gap-1">
                <Layers size={12} />
                {company.logicalBranchCount}{' '}
                {pluralizeBranches(company.logicalBranchCount)}
              </span>
              {periodLabel && (
                <span className="inline-flex items-center gap-1">
                  <CalendarRange size={12} />
                  {periodLabel}
                </span>
              )}
              {sourcesLabel && <span className="truncate">{sourcesLabel}</span>}
            </div>
          </div>
          <span className="hidden sm:inline-flex items-center gap-1 text-sm font-medium text-brand group-hover:text-brand-hover transition-colors shrink-0">
            Продолжить
            <ChevronRight size={14} />
          </span>
        </div>
      </Card>
    </Link>
  )
}

function formatDraftPeriod(from: string | null, to: string | null): string | null {
  if (!from || !to) return 'С самого начала'
  return `${from.slice(0, 10).split('-').reverse().join('.')} — ${to.slice(0, 10).split('-').reverse().join('.')}`
}

function pluralizeBranches(n: number): string {
  const mod10 = n % 10
  const mod100 = n % 100
  if (mod10 === 1 && mod100 !== 11) return 'филиал'
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return 'филиала'
  return 'филиалов'
}

function ProgressBar({
  value,
  tone,
}: {
  value: number
  tone: 'pending' | 'progress' | 'ok' | 'warn' | 'error'
}) {
  const color =
    tone === 'ok'
      ? 'bg-emerald-500'
      : tone === 'warn'
      ? 'bg-amber-500'
      : tone === 'error'
      ? 'bg-rose-500'
      : tone === 'progress'
      ? 'bg-brand'
      : 'bg-slate-400'
  return (
    <div className="h-2 w-full rounded-full bg-page-bg overflow-hidden">
      <div
        className={cn('h-full transition-all', color)}
        style={{ width: `${Math.max(2, Math.min(100, value))}%` }}
      />
    </div>
  )
}

function formatDateTime(iso: string): string {
  try {
    const d = new Date(iso)
    return d.toLocaleString('ru-RU', {
      day: '2-digit',
      month: '2-digit',
      year: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
    })
  } catch {
    return iso
  }
}

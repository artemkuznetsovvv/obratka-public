import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ChevronDown, ChevronRight, ExternalLink } from 'lucide-react'
import { AppLayout } from '@/layouts/AppLayout'
import { Card } from '@/components/ui/card'
import { Badge, type BadgeProps } from '@/components/ui/badge'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { adminAnalysesApi, type AnalysisJob, type CollectionProgressEntry } from '@/api/admin'
import { cn } from '@/lib/utils'

const JOB_STATUS_VARIANT: Record<string, BadgeProps['variant']> = {
  pending: 'muted',
  collecting: 'secondary',
  sent_to_llm: 'secondary',
  computing_aggregates: 'secondary',
  completed: 'success',
  partial: 'warning',
  failed: 'destructive',
}

const TASK_STATUS_VARIANT: Record<string, BadgeProps['variant']> = {
  pending: 'muted',
  running: 'secondary',
  completed: 'success',
  failed: 'destructive',
}

const STATUS_OPTIONS = [
  '',
  'pending',
  'collecting',
  'sent_to_llm',
  'computing_aggregates',
  'completed',
  'partial',
  'failed',
] as const

export default function AnalysesPage() {
  const [statusFilter, setStatusFilter] = useState<string>('')
  const [companyIdFilter, setCompanyIdFilter] = useState<string>('')
  const [expanded, setExpanded] = useState<Set<string>>(new Set())

  const { data, isLoading, isError, error, refetch, isFetching } = useQuery({
    queryKey: ['admin', 'analyses', statusFilter, companyIdFilter],
    queryFn: () =>
      adminAnalysesApi.list({
        status: statusFilter || undefined,
        companyId: companyIdFilter || undefined,
        limit: 100,
      }),
    refetchInterval: 5000,
  })

  const toggle = (jobId: string) => {
    setExpanded((prev) => {
      const next = new Set(prev)
      if (next.has(jobId)) next.delete(jobId)
      else next.add(jobId)
      return next
    })
  }

  return (
    <AppLayout breadcrumbs={[{ label: 'Админ' }, { label: 'Анализы' }]}>
      <div className="mb-8 flex items-start justify-between">
        <div>
          <h1 className="text-h1 text-text-primary">Анализы</h1>
          <p className="text-body text-text-secondary mt-1">
            Задачи Processing-Gateway. Раскрой строку, чтобы увидеть статусы подзадач сбора (collection_progress).
            Список обновляется каждые 5 секунд.
          </p>
        </div>
        <button
          onClick={() => refetch()}
          className="text-sm text-brand hover:underline"
          disabled={isFetching}
        >
          Обновить
        </button>
      </div>

      <Card className="p-4 mb-4">
        <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
          <div className="space-y-2">
            <Label className="text-caption uppercase text-text-secondary">Статус</Label>
            <select
              className="flex h-10 w-full rounded-lg border border-border-subtle bg-card px-3 text-sm"
              value={statusFilter}
              onChange={(e) => setStatusFilter(e.target.value)}
            >
              {STATUS_OPTIONS.map((s) => (
                <option key={s} value={s}>
                  {s || 'Все'}
                </option>
              ))}
            </select>
          </div>
          <div className="space-y-2 md:col-span-2">
            <Label className="text-caption uppercase text-text-secondary">Company ID (UUID, опционально)</Label>
            <Input
              placeholder="446261a9-3ca0-44ff-9bbb-0ac9e75be4af"
              value={companyIdFilter}
              onChange={(e) => setCompanyIdFilter(e.target.value)}
            />
          </div>
        </div>
      </Card>

      <Card>
        {isLoading && <div className="p-6 text-text-secondary text-sm">Загрузка…</div>}
        {isError && (
          <div className="p-6 text-destructive text-sm">
            Не удалось загрузить: {(error as Error).message}
          </div>
        )}
        {data && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="w-8" />
                <TableHead>ID</TableHead>
                <TableHead>Company</TableHead>
                <TableHead>Статус</TableHead>
                <TableHead>Отзывов</TableHead>
                <TableHead>Реков.</TableHead>
                <TableHead>Создан</TableHead>
                <TableHead>Завершён</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.items.length === 0 && (
                <TableRow>
                  <TableCell colSpan={8} className="text-center text-text-secondary py-12">
                    Анализов нет
                  </TableCell>
                </TableRow>
              )}
              {data.items.map((job) => (
                <JobRow
                  key={job.id}
                  job={job}
                  expanded={expanded.has(job.id)}
                  onToggle={() => toggle(job.id)}
                />
              ))}
            </TableBody>
          </Table>
        )}
      </Card>
    </AppLayout>
  )
}

function JobRow({ job, expanded, onToggle }: { job: AnalysisJob; expanded: boolean; onToggle: () => void }) {
  const sourcesList = useMemo(() => Object.entries(job.collectionProgress), [job.collectionProgress])
  return (
    <>
      <TableRow className="cursor-pointer" onClick={onToggle}>
        <TableCell className="w-8 text-text-tertiary">
          {expanded ? <ChevronDown size={16} /> : <ChevronRight size={16} />}
        </TableCell>
        <TableCell className="font-mono text-xs">{job.id.slice(0, 8)}…</TableCell>
        <TableCell className="font-mono text-xs text-text-secondary">{job.companyId.slice(0, 8)}…</TableCell>
        <TableCell>
          <Badge variant={JOB_STATUS_VARIANT[job.status] ?? 'muted'}>{job.status}</Badge>
        </TableCell>
        <TableCell>{job.reviewCount}</TableCell>
        <TableCell>{job.recommendationsCount}</TableCell>
        <TableCell className="text-text-secondary">
          {new Date(job.createdAt).toLocaleString('ru-RU')}
        </TableCell>
        <TableCell className="text-text-secondary">
          {job.completedAt ? new Date(job.completedAt).toLocaleString('ru-RU') : '—'}
        </TableCell>
      </TableRow>
      {expanded && (
        <TableRow className="bg-page-bg hover:bg-page-bg">
          <TableCell colSpan={8} className="p-0">
            <div className="p-6 space-y-6">
              {sourcesList.length > 0 && <SourcesBlock sources={sourcesList} />}
              <UrlsBlock job={job} />
              {job.summary && <SummaryBlock summary={job.summary} />}
              {job.error && (
                <div className="rounded-md border border-destructive/30 bg-destructive/5 p-3 text-sm text-destructive">
                  <span className="font-medium">Ошибка job:</span> {job.error}
                </div>
              )}
            </div>
          </TableCell>
        </TableRow>
      )}
    </>
  )
}

function SourcesBlock({ sources }: { sources: [string, CollectionProgressEntry][] }) {
  return (
    <div className="space-y-2">
      <h3 className="text-h3 text-text-primary">Подзадачи сбора</h3>
      <div className="rounded-lg border border-border-subtle bg-card overflow-hidden">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Source</TableHead>
              <TableHead>Task ID</TableHead>
              <TableHead>Статус</TableHead>
              <TableHead>Прогресс</TableHead>
              <TableHead>Отзывов</TableHead>
              <TableHead>S3</TableHead>
              <TableHead>Ошибка</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {sources.map(([source, entry]) => (
              <TableRow key={source}>
                <TableCell><Badge variant="muted">{source}</Badge></TableCell>
                <TableCell className="font-mono text-xs">{entry.taskId.slice(0, 8)}…</TableCell>
                <TableCell>
                  <Badge variant={TASK_STATUS_VARIANT[entry.status] ?? 'muted'}>{entry.status}</Badge>
                </TableCell>
                <TableCell className="text-text-secondary">{entry.progress}%</TableCell>
                <TableCell>{entry.reviewCount ?? '—'}</TableCell>
                <TableCell className="max-w-xs truncate text-text-secondary text-xs">
                  {entry.s3Url ?? '—'}
                </TableCell>
                <TableCell className={cn('max-w-xs truncate text-xs', entry.error && 'text-destructive')}>
                  {entry.error ?? '—'}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </div>
    </div>
  )
}

function UrlsBlock({ job }: { job: AnalysisJob }) {
  const urls = [
    ['Payload', job.payloadUrl],
    ['Reviews (output)', job.resultReviewsUrl],
    ['Summary (output)', job.resultSummaryUrl],
  ] as const
  return (
    <div className="space-y-2">
      <h3 className="text-h3 text-text-primary">S3 артефакты</h3>
      <div className="text-sm space-y-1">
        {urls.map(([label, url]) => (
          <div key={label} className="flex items-center gap-2">
            <span className="w-40 text-text-secondary">{label}:</span>
            {url ? (
              <span className="font-mono text-xs text-text-primary flex items-center gap-1">
                {url}
                <ExternalLink size={12} className="text-text-tertiary" />
              </span>
            ) : (
              <span className="text-text-tertiary">—</span>
            )}
          </div>
        ))}
      </div>
    </div>
  )
}

function SummaryBlock({ summary }: { summary: string }) {
  return (
    <div className="space-y-2">
      <h3 className="text-h3 text-text-primary">Summary</h3>
      <p className="text-sm text-text-secondary leading-relaxed">{summary}</p>
    </div>
  )
}

import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, Download, RefreshCw, RotateCw, Sparkles } from 'lucide-react'
import { AppLayout } from '@/layouts/AppLayout'
import { Card } from '@/components/ui/card'
import { Badge, type BadgeProps } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { adminAnalysesApi, type CollectionProgressEntry, type JobBlobItem } from '@/api/admin'
import { localizeAnalysisError } from '@/pages/history/analysisStatus'
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

const TERMINAL = new Set(['completed', 'partial', 'failed'])

export default function AnalysisDetailPage() {
  const { jobId } = useParams<{ jobId: string }>()
  const navigate = useNavigate()
  const queryClient = useQueryClient()

  const jobQuery = useQuery({
    queryKey: ['admin', 'analyses', jobId],
    queryFn: () => adminAnalysesApi.get(jobId!),
    enabled: !!jobId,
    refetchInterval: (q) => {
      const status = q.state.data?.status
      // Stop polling once the pipeline reaches a terminal state.
      return status && TERMINAL.has(status) ? false : 3000
    },
  })

  const [restartSource, setRestartSource] = useState<string | null>(null)
  const [actionMessage, setActionMessage] = useState<{ kind: 'success' | 'error'; text: string } | null>(null)

  const replay = useMutation({
    mutationFn: () => adminAnalysesApi.llmReplay(jobId!),
    onSuccess: () => {
      setActionMessage({ kind: 'success', text: 'LLM replay поставлен в очередь' })
      queryClient.invalidateQueries({ queryKey: ['admin', 'analyses', jobId] })
    },
    onError: (err) =>
      setActionMessage({
        kind: 'error',
        text: err instanceof Error ? err.message : 'Не удалось запустить LLM replay',
      }),
  })

  return (
    <AppLayout
      breadcrumbs={[
        { label: 'Админ' },
        { label: 'Анализы', to: '/admin/analyses' },
        { label: jobId ? jobId.slice(0, 8) + '…' : '—' },
      ]}
    >
      <div className="mb-6 flex items-start justify-between gap-4 flex-wrap">
        <div className="flex items-center gap-3">
          <Button variant="outline" size="sm" onClick={() => navigate('/admin/analyses')}>
            <ArrowLeft size={14} />
            К списку
          </Button>
          <div>
            <h1 className="text-h1 text-text-primary">Анализ</h1>
            <div className="text-xs text-text-tertiary font-mono mt-1">{jobId}</div>
          </div>
        </div>
        <Button
          variant="outline"
          size="sm"
          onClick={() => jobQuery.refetch()}
          disabled={jobQuery.isFetching}
        >
          <RefreshCw size={14} />
          Обновить
        </Button>
      </div>

      {jobQuery.isLoading && (
        <Card className="p-6 text-sm text-text-secondary">Загрузка…</Card>
      )}
      {jobQuery.isError && (
        <Card className="p-6 text-sm text-destructive">
          Не удалось загрузить: {(jobQuery.error as Error).message}
        </Card>
      )}

      {jobQuery.data && (
        <div className="space-y-6">
          <Card className="p-6">
            <div className="grid grid-cols-2 md:grid-cols-4 gap-4 text-sm">
              <Cell label="Статус">
                <Badge variant={JOB_STATUS_VARIANT[jobQuery.data.status] ?? 'muted'}>
                  {jobQuery.data.status}
                </Badge>
              </Cell>
              <Cell label="Отзывов">{jobQuery.data.reviewCount}</Cell>
              <Cell label="Рекомендаций">{jobQuery.data.recommendationsCount}</Cell>
              <Cell label="Company ID">
                <Link
                  to={`/admin/companies?from=${jobQuery.data.companyId}`}
                  className="font-mono text-xs text-brand hover:underline"
                >
                  {jobQuery.data.companyId.slice(0, 8)}…
                </Link>
              </Cell>
              <Cell label="Создан">
                {new Date(jobQuery.data.createdAt).toLocaleString('ru-RU')}
              </Cell>
              <Cell label="Отправлен в LLM">
                {jobQuery.data.sentAt ? new Date(jobQuery.data.sentAt).toLocaleString('ru-RU') : '—'}
              </Cell>
              <Cell label="Завершён">
                {jobQuery.data.completedAt
                  ? new Date(jobQuery.data.completedAt).toLocaleString('ru-RU')
                  : '—'}
              </Cell>
              <Cell label="Авто-обновление">
                {TERMINAL.has(jobQuery.data.status) ? 'Выкл (terminal)' : 'Раз в 3 сек'}
              </Cell>
            </div>
            {jobQuery.data.error && (
              <div className="mt-4 rounded-md border border-destructive/30 bg-destructive/5 p-3 text-sm text-destructive">
                <span className="font-medium">Ошибка job:</span> {localizeAnalysisError(jobQuery.data.error)}
              </div>
            )}
          </Card>

          <Card className="p-6 space-y-3">
            <h2 className="text-h3 text-text-primary">Подзадачи сбора</h2>
            {Object.keys(jobQuery.data.collectionProgress).length === 0 ? (
              <p className="text-sm text-text-tertiary">Сбор ещё не запускался.</p>
            ) : (
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Source</TableHead>
                    <TableHead>Task ID</TableHead>
                    <TableHead>Статус</TableHead>
                    <TableHead>Прогресс</TableHead>
                    <TableHead>Отзывов</TableHead>
                    <TableHead className="max-w-[20ch]">Ошибка</TableHead>
                    <TableHead className="text-right">Действие</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {Object.entries(jobQuery.data.collectionProgress).map(([source, entry]) => (
                    <SourceRow
                      key={source}
                      source={source}
                      entry={entry}
                      onRestart={() => setRestartSource(source)}
                    />
                  ))}
                </TableBody>
              </Table>
            )}
          </Card>

          <Card className="p-6 space-y-3">
            <div className="flex items-center justify-between flex-wrap gap-3">
              <h2 className="text-h3 text-text-primary">LLM</h2>
              <Button
                variant="outline"
                size="sm"
                onClick={() => replay.mutate()}
                disabled={replay.isPending}
              >
                <Sparkles size={14} />
                {replay.isPending ? 'Replay…' : 'LLM replay'}
              </Button>
            </div>
            <p className="text-xs text-text-tertiary">
              Replay перечитает собранные отзывы и заново отправит LLM-запрос. Безопасен идемпотентно
              (выводы перезаписываются по UNIQUE-ключам).
            </p>
            {jobQuery.data.summary && (
              <div className="rounded-lg bg-page-bg p-3 text-sm leading-relaxed text-text-primary">
                {jobQuery.data.summary}
              </div>
            )}
          </Card>

          <BlobsCard jobId={jobId!} />

          <Card className="p-6 space-y-3">
            <h2 className="text-h3 text-text-primary">S3 URL (для справки)</h2>
            <UrlsBlock job={jobQuery.data} />
          </Card>

          {actionMessage && (
            <div
              className={cn(
                'rounded-lg border px-4 py-3 text-sm',
                actionMessage.kind === 'success'
                  ? 'border-sentiment-positive/30 bg-sentiment-positive/5 text-sentiment-positive'
                  : 'border-destructive/30 bg-destructive/5 text-destructive',
              )}
            >
              {actionMessage.text}
            </div>
          )}
        </div>
      )}

      <RestartSourceDialog
        jobId={jobId ?? ''}
        source={restartSource}
        onClose={(message) => {
          setRestartSource(null)
          if (message) setActionMessage(message)
          queryClient.invalidateQueries({ queryKey: ['admin', 'analyses', jobId] })
        }}
      />
    </AppLayout>
  )
}

function BlobsCard({ jobId }: { jobId: string }) {
  const blobsQuery = useQuery({
    queryKey: ['admin', 'analyses', jobId, 'blobs'],
    queryFn: () => adminAnalysesApi.listBlobs(jobId),
    refetchInterval: 5000,
  })
  const [downloadingKey, setDownloadingKey] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const blobNameFromKey = (key: string) => {
    // key looks like "<jobId>/input.json" or "<jobId>/raw/yandex.json".
    // PG accepts: input | output_reviews | output_summary | raw/<source>.
    const rel = key.startsWith(`${jobId}/`) ? key.slice(jobId.length + 1) : key
    if (rel.startsWith('raw/')) return rel.replace(/\.json$/, '')
    return rel.replace(/\.json$/, '')
  }

  const downloadOne = async (key: string) => {
    setDownloadingKey(key)
    setError(null)
    try {
      const name = blobNameFromKey(key)
      const { blob, fileName } = await adminAnalysesApi.downloadBlob(jobId, name)
      triggerBrowserDownload(blob, fileName)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось скачать файл')
    } finally {
      setDownloadingKey(null)
    }
  }

  return (
    <Card className="p-6 space-y-3">
      <div className="flex items-center justify-between flex-wrap gap-3">
        <h2 className="text-h3 text-text-primary">S3 артефакты (MinIO)</h2>
        {blobsQuery.data && (
          <span className="text-xs text-text-tertiary">
            bucket: <span className="font-mono">{blobsQuery.data.bucket}</span> · prefix:{' '}
            <span className="font-mono">{blobsQuery.data.prefix}</span>
          </span>
        )}
      </div>
      {blobsQuery.isLoading && <p className="text-sm text-text-secondary">Загрузка…</p>}
      {blobsQuery.isError && (
        <p className="text-sm text-destructive">
          Не удалось загрузить листинг: {(blobsQuery.error as Error).message}
        </p>
      )}
      {blobsQuery.data && blobsQuery.data.items.length === 0 && (
        <p className="text-sm text-text-tertiary">В бакете пока нет файлов для этого job-а.</p>
      )}
      {blobsQuery.data && blobsQuery.data.items.length > 0 && (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Ключ</TableHead>
              <TableHead className="text-right">Размер</TableHead>
              <TableHead>Изменён</TableHead>
              <TableHead className="text-right">Действие</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {blobsQuery.data.items.map((b) => (
              <BlobRow
                key={b.key}
                blob={b}
                jobId={jobId}
                downloading={downloadingKey === b.key}
                onDownload={() => downloadOne(b.key)}
              />
            ))}
          </TableBody>
        </Table>
      )}
      {error && (
        <div className="rounded-md border border-destructive/30 bg-destructive/5 p-3 text-sm text-destructive">
          {error}
        </div>
      )}
    </Card>
  )
}

function BlobRow({
  blob,
  jobId,
  downloading,
  onDownload,
}: {
  blob: JobBlobItem
  jobId: string
  downloading: boolean
  onDownload: () => void
}) {
  const rel = blob.key.startsWith(`${jobId}/`) ? blob.key.slice(jobId.length + 1) : blob.key
  return (
    <TableRow>
      <TableCell className="font-mono text-xs">{rel}</TableCell>
      <TableCell className="text-right text-sm tabular-nums">{formatBytes(blob.size)}</TableCell>
      <TableCell className="text-text-secondary text-xs">
        {new Date(blob.lastModified).toLocaleString('ru-RU')}
      </TableCell>
      <TableCell className="text-right">
        <Button variant="outline" size="sm" onClick={onDownload} disabled={downloading}>
          <Download size={12} />
          {downloading ? 'Скачиваем…' : 'Скачать'}
        </Button>
      </TableCell>
    </TableRow>
  )
}

function triggerBrowserDownload(blob: Blob, fileName: string) {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  document.body.appendChild(link)
  link.click()
  link.remove()
  // Free the URL after the click so the browser keeps the download alive but we don't leak memory.
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`
}

function Cell({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex flex-col gap-1 text-sm">
      <span className="text-xs uppercase text-text-tertiary">{label}</span>
      <span className="text-text-primary">{children}</span>
    </div>
  )
}

function SourceRow({
  source,
  entry,
  onRestart,
}: {
  source: string
  entry: CollectionProgressEntry
  onRestart: () => void
}) {
  return (
    <TableRow>
      <TableCell>
        <Badge variant="muted">{source}</Badge>
      </TableCell>
      <TableCell className="font-mono text-xs">{entry.taskId.slice(0, 8)}…</TableCell>
      <TableCell>
        <Badge variant={TASK_STATUS_VARIANT[entry.status] ?? 'muted'}>{entry.status}</Badge>
      </TableCell>
      <TableCell className="text-text-secondary">{entry.progress}%</TableCell>
      <TableCell>{entry.reviewCount ?? '—'}</TableCell>
      <TableCell className={cn('max-w-[20ch] truncate text-xs', entry.error && 'text-destructive')}>
        {localizeAnalysisError(entry.error) ?? '—'}
      </TableCell>
      <TableCell className="text-right">
        <Button variant="outline" size="sm" onClick={onRestart}>
          <RotateCw size={12} />
          Рестарт
        </Button>
      </TableCell>
    </TableRow>
  )
}

function UrlsBlock({
  job,
}: {
  job: { payloadUrl: string | null; resultReviewsUrl: string | null; resultSummaryUrl: string | null }
}) {
  const urls = [
    ['Payload (input.json)', job.payloadUrl],
    ['Reviews (output)', job.resultReviewsUrl],
    ['Summary (output)', job.resultSummaryUrl],
  ] as const
  return (
    <div className="text-sm space-y-1.5">
      {urls.map(([label, url]) => (
        <div key={label} className="flex items-start gap-2 min-w-0">
          <span className="w-40 text-text-secondary shrink-0">{label}:</span>
          {url ? (
            <span className="font-mono text-xs text-text-primary break-all">{url}</span>
          ) : (
            <span className="text-text-tertiary">—</span>
          )}
        </div>
      ))}
    </div>
  )
}

function RestartSourceDialog({
  jobId,
  source,
  onClose,
}: {
  jobId: string
  source: string | null
  onClose: (message?: { kind: 'success' | 'error'; text: string }) => void
}) {
  const [dateFrom, setDateFrom] = useState('')
  const [dateTo, setDateTo] = useState('')
  const restart = useMutation({
    mutationFn: () => {
      if (!source) throw new Error('No source')
      return adminAnalysesApi.restartSource(jobId, source, {
        dateFrom: toIsoOrNull(dateFrom),
        dateTo: toIsoOrNull(dateTo),
      })
    },
    onSuccess: (response) => {
      onClose({
        kind: 'success',
        text: `Source ${response.source} перезапущен (task ${response.taskId.slice(0, 8)}…), статус job: ${response.currentStatus}`,
      })
      setDateFrom('')
      setDateTo('')
    },
    onError: (err) =>
      onClose({
        kind: 'error',
        text: err instanceof Error ? err.message : 'Не удалось перезапустить source',
      }),
  })

  const open = !!source
  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        if (!next) onClose()
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Рестарт source: {source}</DialogTitle>
          <DialogDescription>
            Создаст новую parser-задачу для выбранного источника на тех же филиалах (IsSelected=true).
            Job из терминального статуса откатится в <code>collecting</code>.
          </DialogDescription>
        </DialogHeader>

        <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
          <div>
            <label className="block text-sm font-medium text-text-secondary mb-1">С даты</label>
            <Input type="date" value={dateFrom} onChange={(e) => setDateFrom(e.target.value)} />
          </div>
          <div>
            <label className="block text-sm font-medium text-text-secondary mb-1">По дату</label>
            <Input type="date" value={dateTo} onChange={(e) => setDateTo(e.target.value)} />
          </div>
        </div>

        <DialogFooter>
          <DialogClose asChild>
            <Button variant="outline">Отмена</Button>
          </DialogClose>
          <Button onClick={() => restart.mutate()} disabled={restart.isPending} className="gap-2">
            <RotateCw size={14} />
            {restart.isPending ? 'Запускаем…' : 'Перезапустить'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function toIsoOrNull(value: string): string | null {
  if (!value) return null
  const date = new Date(`${value}T00:00:00Z`)
  return isNaN(date.getTime()) ? null : date.toISOString()
}

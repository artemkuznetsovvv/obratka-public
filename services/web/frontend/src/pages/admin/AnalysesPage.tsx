import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { ChevronRight, ExternalLink } from 'lucide-react'
import { AppLayout } from '@/layouts/AppLayout'
import { Card } from '@/components/ui/card'
import { Badge, type BadgeProps } from '@/components/ui/badge'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { adminAnalysesApi } from '@/api/admin'

const JOB_STATUS_VARIANT: Record<string, BadgeProps['variant']> = {
  pending: 'muted',
  collecting: 'secondary',
  sent_to_llm: 'secondary',
  computing_aggregates: 'secondary',
  completed: 'success',
  partial: 'warning',
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
  const navigate = useNavigate()
  const [statusFilter, setStatusFilter] = useState<string>('')
  const [companyIdFilter, setCompanyIdFilter] = useState<string>('')

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

  return (
    <AppLayout breadcrumbs={[{ label: 'Админ' }, { label: 'Анализы' }]}>
      <div className="mb-8 flex items-start justify-between">
        <div>
          <h1 className="text-h1 text-text-primary">Анализы</h1>
          <p className="text-body text-text-secondary mt-1">
            Задачи Processing-Gateway. Нажмите строку, чтобы открыть детали, прогресс источников и
            действия (рестарт источника, LLM replay). Список обновляется каждые 5 секунд.
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
            <Label className="text-caption uppercase text-text-secondary">
              Company ID (UUID, опционально)
            </Label>
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
                <TableHead>ID</TableHead>
                <TableHead>Company</TableHead>
                <TableHead>Статус</TableHead>
                <TableHead className="text-right">Отзывов</TableHead>
                <TableHead className="text-right">Реков.</TableHead>
                <TableHead>Источники</TableHead>
                <TableHead>Создан</TableHead>
                <TableHead className="w-10" />
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
              {data.items.map((job) => {
                const sources = Object.keys(job.collectionProgress)
                return (
                  <TableRow
                    key={job.id}
                    className="cursor-pointer"
                    onClick={() => navigate(`/admin/analyses/${job.id}`)}
                  >
                    <TableCell className="font-mono text-xs">{job.id.slice(0, 8)}…</TableCell>
                    <TableCell className="font-mono text-xs text-text-secondary">
                      {job.companyId.slice(0, 8)}…
                    </TableCell>
                    <TableCell>
                      <Badge variant={JOB_STATUS_VARIANT[job.status] ?? 'muted'}>{job.status}</Badge>
                    </TableCell>
                    <TableCell className="text-right">{job.reviewCount}</TableCell>
                    <TableCell className="text-right">{job.recommendationsCount}</TableCell>
                    <TableCell>
                      {sources.length === 0 ? (
                        <span className="text-text-tertiary text-xs">—</span>
                      ) : (
                        <div className="flex flex-wrap gap-1">
                          {sources.map((s) => (
                            <Badge key={s} variant="muted">
                              {s}
                            </Badge>
                          ))}
                        </div>
                      )}
                    </TableCell>
                    <TableCell className="text-text-secondary text-xs">
                      {new Date(job.createdAt).toLocaleString('ru-RU')}
                    </TableCell>
                    <TableCell className="text-text-tertiary">
                      <ChevronRight size={16} />
                    </TableCell>
                  </TableRow>
                )
              })}
            </TableBody>
          </Table>
        )}
      </Card>

      <p className="mt-4 text-xs text-text-tertiary inline-flex items-center gap-1">
        <ExternalLink size={12} />
        Запуск нового анализа — со страницы /admin/companies (кнопка «Запустить» на компании).
      </p>
    </AppLayout>
  )
}

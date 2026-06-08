import { useQuery } from '@tanstack/react-query'
import { AppLayout } from '@/layouts/AppLayout'
import { Card } from '@/components/ui/card'
import { Badge, type BadgeProps } from '@/components/ui/badge'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { adminParserTasksApi } from '@/api/admin'

const STATUS_VARIANT: Record<string, BadgeProps['variant']> = {
  pending: 'muted',
  running: 'secondary',
  completed: 'success',
  failed: 'destructive',
}

export default function ParserTasksPage() {
  const { data, isLoading, isError, error, refetch, isFetching } = useQuery({
    queryKey: ['admin', 'parser-tasks'],
    queryFn: () => adminParserTasksApi.list({ limit: 100 }),
    refetchInterval: 5000,
  })

  return (
    <AppLayout breadcrumbs={[{ label: 'Админ' }, { label: 'Парсер-задачи' }]}>
      <div className="mb-8 flex items-start justify-between">
        <div>
          <h1 className="text-h1 text-text-primary">Парсер-задачи</h1>
          <p className="text-body text-text-secondary mt-1">
            Список задач сбора отзывов в Parser-Service. Обновляется автоматически каждые 5 секунд.
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
                <TableHead>Task ID</TableHead>
                <TableHead>Source</TableHead>
                <TableHead>Статус</TableHead>
                <TableHead>Отзывов</TableHead>
                <TableHead>Создан</TableHead>
                <TableHead>S3 / Ошибка</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.items.length === 0 && (
                <TableRow>
                  <TableCell colSpan={6} className="text-center text-text-secondary py-12">
                    Задач пока нет
                  </TableCell>
                </TableRow>
              )}
              {data.items.map((task) => (
                <TableRow key={task.taskId}>
                  <TableCell className="font-mono text-xs">{task.taskId.slice(0, 8)}…</TableCell>
                  <TableCell><Badge variant="muted">{task.source}</Badge></TableCell>
                  <TableCell>
                    <Badge variant={STATUS_VARIANT[task.status] ?? 'muted'}>{task.status}</Badge>
                  </TableCell>
                  <TableCell>{task.reviewCount ?? '—'}</TableCell>
                  <TableCell className="text-text-secondary">
                    {new Date(task.createdAt).toLocaleString('ru-RU')}
                  </TableCell>
                  <TableCell className="max-w-xs truncate text-text-secondary">
                    {task.error ? (
                      <span className="text-destructive">{task.error}</span>
                    ) : task.s3Url ? (
                      <a
                        href={task.s3Url}
                        target="_blank"
                        rel="noreferrer"
                        className="text-brand hover:underline"
                      >
                        S3
                      </a>
                    ) : (
                      '—'
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Card>
    </AppLayout>
  )
}

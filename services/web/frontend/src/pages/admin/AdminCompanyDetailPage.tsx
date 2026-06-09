import { useNavigate, useParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { ArrowLeft, MapPin } from 'lucide-react'
import { AppLayout } from '@/layouts/AppLayout'
import { Card } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { adminCompaniesApi } from '@/api/admin'
import { cn } from '@/lib/utils'
import { SOURCE_LABEL, statusMetaFor } from '@/pages/history/analysisStatus'
import { FREQUENCY_LABEL, MONITORING_STATUS_LABEL } from '@/api/monitorings'

const fmtDate = (iso: string) => new Date(iso).toLocaleDateString('ru-RU')
const fmtDateTime = (iso: string) => new Date(iso).toLocaleString('ru-RU')

export default function AdminCompanyDetailPage() {
  const { companyId } = useParams<{ companyId: string }>()
  const navigate = useNavigate()

  const { data, isLoading, isError, error } = useQuery({
    queryKey: ['admin', 'companies', 'details', companyId],
    queryFn: () => adminCompaniesApi.get(companyId!),
    enabled: !!companyId,
  })

  const headerLabel = data?.name ?? (companyId ? `${companyId.slice(0, 8)}…` : '—')
  // Источники компании — distinct по карточкам филиалов.
  const sources = data ? Array.from(new Set(data.branches.map((b) => b.source))) : []

  return (
    <AppLayout
      breadcrumbs={[
        { label: 'Админ' },
        { label: 'Компании', to: '/admin/companies' },
        { label: headerLabel },
      ]}
    >
      <div className="mb-6">
        <Button variant="outline" size="sm" onClick={() => navigate(-1)} className="gap-2">
          <ArrowLeft size={14} />
          Назад
        </Button>
      </div>

      {isLoading && <Card className="p-6 text-sm text-text-secondary">Загрузка…</Card>}
      {isError && (
        <Card className="p-6 text-sm text-destructive">
          Не удалось загрузить: {(error as Error).message}
        </Card>
      )}

      {data && (
        <div className="space-y-6">
          <Card className="p-6 space-y-4">
            <div>
              <h1 className="text-h1 text-text-primary">{data.name}</h1>
              <p className="text-sm text-text-secondary mt-1">
                Владелец: {data.ownerFullName || '—'} ({data.ownerEmail})
              </p>
            </div>
            <div className="grid grid-cols-2 md:grid-cols-3 gap-4 text-sm">
              <Cell label="Города">{data.cities.length ? data.cities.join(', ') : '—'}</Cell>
              <Cell label="Источники">
                <div className="flex flex-wrap gap-1">
                  {sources.length
                    ? sources.map((s) => (
                        <Badge key={s} variant="muted">{SOURCE_LABEL[s] ?? s}</Badge>
                      ))
                    : '—'}
                </div>
              </Cell>
              <Cell label="Создана">{fmtDateTime(data.createdAt)}</Cell>
            </div>
          </Card>

          <Card className="p-6 space-y-3">
            <h2 className="text-h3 text-text-primary">Филиалы ({data.logicalBranches.length})</h2>
            {data.logicalBranches.length === 0 ? (
              <p className="text-sm text-text-secondary">Нет филиалов.</p>
            ) : (
              <ul className="space-y-2">
                {data.logicalBranches.map((b) => (
                  <li key={b.id} className="rounded-xl border border-border-subtle bg-card/40 px-4 py-3">
                    <div className="text-sm font-medium text-text-primary flex items-center gap-1">
                      <MapPin size={12} className="text-text-tertiary shrink-0" />
                      <span className="truncate">{b.name}</span>
                    </div>
                    <div className="text-xs text-text-tertiary mt-0.5">
                      {[b.address, b.city].filter(Boolean).join(', ') || '—'}
                    </div>
                    <div className="text-[11px] font-mono text-text-tertiary mt-1">branch_id: {b.id}</div>
                  </li>
                ))}
              </ul>
            )}
          </Card>

          <Card className="p-6 space-y-3">
            <h2 className="text-h3 text-text-primary">Последние анализы</h2>
            {data.recentAnalyses.length === 0 ? (
              <p className="text-sm text-text-secondary">Анализов нет.</p>
            ) : (
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Дата</TableHead>
                    <TableHead>Период (запуск → завершение)</TableHead>
                    <TableHead>Статус</TableHead>
                    <TableHead>Отзывов</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {data.recentAnalyses.map((a) => {
                    const meta = statusMetaFor(a.status)
                    return (
                      <TableRow key={a.id}>
                        <TableCell className="text-text-secondary">{fmtDateTime(a.createdAt)}</TableCell>
                        <TableCell className="text-text-secondary">
                          {fmtDate(a.createdAt)} → {a.completedAt ? fmtDate(a.completedAt) : 'в работе'}
                        </TableCell>
                        <TableCell>
                          <span
                            className={cn(
                              'inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-semibold border',
                              meta.badge,
                            )}
                          >
                            {meta.label}
                          </span>
                        </TableCell>
                        <TableCell className="text-text-secondary">{a.reviewCount}</TableCell>
                      </TableRow>
                    )
                  })}
                </TableBody>
              </Table>
            )}
          </Card>

          <Card className="p-6 space-y-3">
            <h2 className="text-h3 text-text-primary">Live-мониторинг</h2>
            {data.monitoring ? (
              <div className="grid grid-cols-2 md:grid-cols-3 gap-4 text-sm">
                <Cell label="Статус">
                  {(MONITORING_STATUS_LABEL as Record<string, string>)[data.monitoring.status] ??
                    data.monitoring.status}
                </Cell>
                <Cell label="Частота">
                  {(FREQUENCY_LABEL as Record<string, string>)[data.monitoring.frequency] ??
                    data.monitoring.frequency}
                </Cell>
                <Cell label="Окно">{data.monitoring.windowDays} дн.</Cell>
                <Cell label="Последнее обновление">
                  {data.monitoring.lastCollectedAt ? fmtDateTime(data.monitoring.lastCollectedAt) : '—'}
                </Cell>
                <Cell label="Последний прогон">{data.monitoring.lastRunStatus ?? '—'}</Cell>
                <Cell label="Источники">
                  <div className="flex flex-wrap gap-1">
                    {data.monitoring.sources.map((s) => (
                      <Badge key={s} variant="muted">{SOURCE_LABEL[s] ?? s}</Badge>
                    ))}
                  </div>
                </Cell>
                <Cell label="Уведомления">{data.monitoring.notificationsEnabled ? 'Вкл' : 'Выкл'}</Cell>
              </div>
            ) : (
              <p className="text-sm text-text-secondary">Мониторинг не настроен.</p>
            )}
          </Card>
        </div>
      )}
    </AppLayout>
  )
}

function Cell({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <div className="text-caption uppercase text-text-tertiary mb-1">{label}</div>
      <div className="text-text-primary">{children}</div>
    </div>
  )
}

import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useMutation, useQuery } from '@tanstack/react-query'
import { ChevronDown, ChevronRight, MapPin, Play, Search, Star } from 'lucide-react'
import { AppLayout } from '@/layouts/AppLayout'
import { Card } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { adminAnalysesApi, adminCompaniesApi, type AdminCompanyDetails, type AdminCompanyListItem } from '@/api/admin'
import { cn } from '@/lib/utils'

const SOURCE_META: Record<string, { label: string; color: string }> = {
  '2gis': { label: '2ГИС', color: 'bg-emerald-100 text-emerald-700' },
  yandex: { label: 'Яндекс.Карты', color: 'bg-amber-100 text-amber-700' },
  google: { label: 'Google Maps', color: 'bg-blue-100 text-blue-700' },
}

export default function CompaniesPage() {
  const [search, setSearch] = useState('')
  const [debouncedSearch, setDebouncedSearch] = useState('')
  const [expandedId, setExpandedId] = useState<string | null>(null)
  const [launchTarget, setLaunchTarget] = useState<AdminCompanyListItem | null>(null)

  const listQuery = useQuery({
    queryKey: ['admin', 'companies', debouncedSearch],
    queryFn: () =>
      adminCompaniesApi.list({
        limit: 100,
        search: debouncedSearch.trim() || undefined,
      }),
  })

  return (
    <AppLayout breadcrumbs={[{ label: 'Админ' }, { label: 'Компании' }]}>
      <div className="mb-8 flex items-start justify-between gap-4 flex-wrap">
        <div>
          <h1 className="text-h1 text-text-primary">Компании</h1>
          <p className="text-body text-text-secondary mt-1">
            Реестр компаний, созданных пользователями. Раскройте строку, чтобы увидеть филиалы.
          </p>
        </div>
        <form
          onSubmit={(e) => {
            e.preventDefault()
            setDebouncedSearch(search)
          }}
          className="relative w-full sm:w-80"
        >
          <Search
            size={16}
            className="absolute left-3 top-1/2 -translate-y-1/2 text-text-tertiary pointer-events-none"
          />
          <Input
            className="pl-9"
            placeholder="Поиск по названию, email или имени владельца"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            onBlur={() => setDebouncedSearch(search)}
          />
        </form>
      </div>

      <Card>
        {listQuery.isLoading && (
          <div className="p-6 text-text-secondary text-sm">Загрузка…</div>
        )}
        {listQuery.isError && (
          <div className="p-6 text-destructive text-sm">
            Не удалось загрузить список: {(listQuery.error as Error).message}
          </div>
        )}
        {listQuery.data && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="w-8"></TableHead>
                <TableHead>Название</TableHead>
                <TableHead>Владелец</TableHead>
                <TableHead>Категория</TableHead>
                <TableHead>Города</TableHead>
                <TableHead className="text-right">Выбрано / найдено</TableHead>
                <TableHead>Создана</TableHead>
                <TableHead className="w-44 text-right" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {listQuery.data.items.length === 0 && (
                <TableRow>
                  <TableCell colSpan={8} className="text-center text-text-secondary py-12">
                    Компаний пока нет
                  </TableCell>
                </TableRow>
              )}
              {listQuery.data.items.map((c) => {
                const isOpen = expandedId === c.id
                return (
                  <RowGroup key={c.id}>
                    <TableRow
                      className={cn('cursor-pointer', isOpen && 'bg-page-bg/60')}
                      onClick={() => setExpandedId(isOpen ? null : c.id)}
                    >
                      <TableCell className="text-text-tertiary">
                        {isOpen ? <ChevronDown size={16} /> : <ChevronRight size={16} />}
                      </TableCell>
                      <TableCell className="font-medium text-text-primary">{c.name}</TableCell>
                      <TableCell>
                        <div className="text-sm text-text-primary">{c.ownerFullName || '—'}</div>
                        <div className="text-xs text-text-tertiary">{c.ownerEmail}</div>
                      </TableCell>
                      <TableCell className="text-sm text-text-secondary">
                        {c.category ? (
                          <>
                            {c.category}
                            {c.subcategory && (
                              <span className="text-text-tertiary"> / {c.subcategory}</span>
                            )}
                          </>
                        ) : (
                          '—'
                        )}
                      </TableCell>
                      <TableCell>
                        <div className="flex flex-wrap gap-1">
                          {c.cities.length === 0 ? (
                            <span className="text-text-tertiary text-sm">—</span>
                          ) : (
                            c.cities.map((city) => (
                              <Badge key={city} variant="muted">
                                {city}
                              </Badge>
                            ))
                          )}
                        </div>
                      </TableCell>
                      <TableCell className="text-right">
                        {c.branchCount > 0 ? (
                          <div className="flex items-center justify-end gap-2">
                            <Badge variant="success">{c.selectedBranchCount}</Badge>
                            <span className="text-xs text-text-tertiary">/ {c.branchCount}</span>
                          </div>
                        ) : (
                          <span className="text-text-tertiary text-sm">0</span>
                        )}
                      </TableCell>
                      <TableCell className="text-text-secondary text-sm">
                        {new Date(c.createdAt).toLocaleDateString('ru-RU')}
                      </TableCell>
                      <TableCell className="text-right">
                        <Button
                          size="sm"
                          variant="outline"
                          disabled={c.selectedBranchCount === 0}
                          title={
                            c.selectedBranchCount === 0
                              ? 'У компании нет выбранных филиалов'
                              : 'Запустить анализ выбранных филиалов'
                          }
                          onClick={(e) => {
                            e.stopPropagation()
                            setLaunchTarget(c)
                          }}
                        >
                          <Play size={14} />
                          Запустить
                        </Button>
                      </TableCell>
                    </TableRow>
                    {isOpen && <CompanyDetailsRow id={c.id} colSpan={8} />}
                  </RowGroup>
                )
              })}
            </TableBody>
          </Table>
        )}
      </Card>

      <LaunchAnalysisDialog
        company={launchTarget}
        onClose={() => setLaunchTarget(null)}
      />
    </AppLayout>
  )
}

function LaunchAnalysisDialog({
  company,
  onClose,
}: {
  company: AdminCompanyListItem | null
  onClose: () => void
}) {
  const navigate = useNavigate()
  const [dateFrom, setDateFrom] = useState('')
  const [dateTo, setDateTo] = useState('')
  const [error, setError] = useState<string | null>(null)

  const launch = useMutation({
    mutationFn: () => {
      if (!company) throw new Error('No company selected')
      return adminAnalysesApi.start({
        companyId: company.id,
        dateFrom: toIsoOrNull(dateFrom),
        dateTo: toIsoOrNull(dateTo),
      })
    },
    onSuccess: (response) => {
      onClose()
      setDateFrom('')
      setDateTo('')
      setError(null)
      navigate(`/admin/analyses/${response.analysisJobId}`)
    },
    onError: (err) => setError(err instanceof Error ? err.message : 'Не удалось запустить анализ'),
  })

  const open = !!company
  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        if (!next) {
          onClose()
          setError(null)
        }
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Запуск анализа</DialogTitle>
          <DialogDescription>
            {company && (
              <>
                Компания <span className="font-medium text-text-primary">{company.name}</span> —
                будут отправлены {company.selectedBranchCount} выбранных филиал(ов).
              </>
            )}
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
        <p className="text-xs text-text-tertiary">
          Оба поля опциональны. Пусто = парсер собирает весь доступный диапазон.
        </p>

        {error && (
          <div className="rounded-lg border border-destructive/30 bg-destructive/5 px-3 py-2 text-sm text-destructive">
            {error}
          </div>
        )}

        <DialogFooter>
          <DialogClose asChild>
            <Button variant="outline">Отмена</Button>
          </DialogClose>
          <Button onClick={() => launch.mutate()} disabled={launch.isPending} className="gap-2">
            <Play size={14} />
            {launch.isPending ? 'Запускаем…' : 'Запустить'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function toIsoOrNull(value: string): string | null {
  if (!value) return null
  // value is a date-only string from <input type="date">, treat it as UTC midnight.
  const date = new Date(`${value}T00:00:00Z`)
  return isNaN(date.getTime()) ? null : date.toISOString()
}

function RowGroup({ children }: { children: React.ReactNode }) {
  return <>{children}</>
}

function CompanyDetailsRow({ id, colSpan }: { id: string; colSpan: number }) {
  const { data, isLoading, isError, error } = useQuery({
    queryKey: ['admin', 'companies', 'details', id],
    queryFn: () => adminCompaniesApi.get(id),
  })

  return (
    <TableRow className="bg-page-bg/40 hover:bg-page-bg/40">
      <TableCell colSpan={colSpan} className="p-0">
        <div className="px-6 py-5">
          {isLoading && <div className="text-sm text-text-secondary">Загрузка деталей…</div>}
          {isError && (
            <div className="text-sm text-destructive">
              Не удалось загрузить детали: {(error as Error).message}
            </div>
          )}
          {data && <CompanyDetails data={data} />}
        </div>
      </TableCell>
    </TableRow>
  )
}

function CompanyDetails({ data }: { data: AdminCompanyDetails }) {
  return (
    <div className="space-y-5">
      {data.description && (
        <div>
          <div className="text-xs uppercase text-text-tertiary mb-1">Дополнительный контекст</div>
          <p className="text-sm text-text-primary whitespace-pre-line">{data.description}</p>
        </div>
      )}

      <div className="grid grid-cols-1 sm:grid-cols-2 gap-x-6 gap-y-2 text-sm">
        <div>
          <span className="text-text-tertiary">ID компании:</span>{' '}
          <span className="font-mono text-text-secondary">{data.id}</span>
        </div>
        <div>
          <span className="text-text-tertiary">Обновлена:</span>{' '}
          <span className="text-text-secondary">
            {new Date(data.updatedAt).toLocaleString('ru-RU')}
          </span>
        </div>
      </div>

      <div>
        <div className="flex items-baseline gap-3 mb-2">
          <div className="text-xs uppercase text-text-tertiary">
            Филиалы ({data.branches.length})
          </div>
          {data.branches.length > 0 && (
            <div className="text-xs text-text-tertiary">
              Выбрано пользователем: {data.branches.filter((b) => b.isSelected).length}
            </div>
          )}
        </div>
        {data.branches.length === 0 ? (
          <div className="text-sm text-text-tertiary">
            Пока ничего не найдено для этой компании.
          </div>
        ) : (
          <ul className="divide-y divide-border-subtle rounded-lg border border-border-subtle bg-card">
            {data.branches.map((b) => {
              const meta = SOURCE_META[b.source] ?? {
                label: b.source,
                color: 'bg-page-bg text-text-secondary',
              }
              return (
                <li
                  key={b.id}
                  className={cn(
                    'flex items-center gap-4 px-4 py-3',
                    !b.isSelected && 'opacity-70',
                  )}
                >
                  <span
                    className={cn(
                      'inline-flex items-center justify-center min-w-[44px] px-2 h-6 rounded text-xs font-semibold',
                      meta.color,
                    )}
                  >
                    {meta.label}
                  </span>
                  <div className="flex-1 min-w-0">
                    <div className="flex items-center gap-2 text-sm font-medium text-text-primary">
                      <a
                        href={b.externalUrl}
                        target="_blank"
                        rel="noreferrer"
                        className="hover:underline truncate"
                      >
                        {b.name}
                      </a>
                      {b.isSelected ? (
                        <Badge variant="success">Выбран</Badge>
                      ) : (
                        <Badge variant="muted">Кандидат</Badge>
                      )}
                    </div>
                    <div className="flex items-center gap-1 text-xs text-text-tertiary">
                      <MapPin size={12} />
                      <span>{b.city}</span>
                      {b.address && (
                        <>
                          <span>·</span>
                          <span className="truncate">{b.address}</span>
                        </>
                      )}
                    </div>
                  </div>
                  {b.rating !== null && (
                    <div className="flex items-center gap-1 text-sm text-text-secondary shrink-0">
                      <Star size={14} className="text-amber-500 fill-amber-500" />
                      <span className="font-medium text-text-primary">{b.rating.toFixed(1)}</span>
                      {b.reviewCount !== null && (
                        <span className="text-xs text-text-tertiary">({b.reviewCount})</span>
                      )}
                    </div>
                  )}
                </li>
              )
            })}
          </ul>
        )}
      </div>
    </div>
  )
}

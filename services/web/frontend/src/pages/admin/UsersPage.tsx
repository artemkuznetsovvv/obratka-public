import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { Search } from 'lucide-react'
import { AppLayout } from '@/layouts/AppLayout'
import { Card } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Badge } from '@/components/ui/badge'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { adminUsersApi } from '@/api/admin'

export default function UsersPage() {
  const navigate = useNavigate()
  const [search, setSearch] = useState('')
  const [debouncedSearch, setDebouncedSearch] = useState('')
  const [status, setStatus] = useState('') // '' | 'active' | 'blocked'

  const { data, isLoading, isError, error } = useQuery({
    queryKey: ['admin', 'users', debouncedSearch, status],
    queryFn: () =>
      adminUsersApi.list({
        limit: 100,
        search: debouncedSearch.trim() || undefined,
        status: status || undefined,
      }),
  })

  return (
    <AppLayout breadcrumbs={[{ label: 'Админ' }, { label: 'Пользователи' }]}>
      <div className="mb-6">
        <h1 className="text-h1 text-text-primary">Пользователи</h1>
        <p className="text-body text-text-secondary mt-1">
          Все зарегистрированные пользователи. Кликните строку, чтобы открыть карточку.
        </p>
      </div>

      <div className="mb-4 flex items-center gap-3 flex-wrap">
        <form
          onSubmit={(e) => {
            e.preventDefault()
            setDebouncedSearch(search)
          }}
          className="relative flex-1 min-w-[260px] max-w-md"
        >
          <Search size={16} className="absolute left-3 top-1/2 -translate-y-1/2 text-text-tertiary" />
          <Input
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            onBlur={() => setDebouncedSearch(search)}
            placeholder="Поиск по email, имени или ID"
            className="pl-9"
          />
        </form>
        <select
          value={status}
          onChange={(e) => setStatus(e.target.value)}
          className="h-10 rounded-lg border border-border-subtle bg-card px-3 text-sm"
        >
          <option value="">Все статусы</option>
          <option value="active">Активные</option>
          <option value="blocked">Заблокированные</option>
        </select>
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
                <TableHead>ID</TableHead>
                <TableHead>Email</TableHead>
                <TableHead>Имя</TableHead>
                <TableHead>Статус</TableHead>
                <TableHead>Компаний</TableHead>
                <TableHead>Последняя активность</TableHead>
                <TableHead>Регистрация</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.items.length === 0 && (
                <TableRow>
                  <TableCell colSpan={7} className="text-center text-text-secondary py-12">
                    Пользователи не найдены
                  </TableCell>
                </TableRow>
              )}
              {data.items.map((u) => (
                <TableRow
                  key={u.id}
                  className="cursor-pointer"
                  onClick={() => navigate(`/admin/users/${u.id}`)}
                >
                  <TableCell className="font-mono text-text-tertiary">{u.id.slice(0, 8)}…</TableCell>
                  <TableCell className="font-medium">{u.email}</TableCell>
                  <TableCell>{u.fullName || '—'}</TableCell>
                  <TableCell>
                    {u.isBlocked ? (
                      <Badge variant="destructive">Заблокирован</Badge>
                    ) : (
                      <Badge variant="success">Активен</Badge>
                    )}
                  </TableCell>
                  <TableCell className="text-text-secondary">{u.companiesCount}</TableCell>
                  <TableCell className="text-text-secondary">
                    {u.lastActivityAt ? new Date(u.lastActivityAt).toLocaleString('ru-RU') : '—'}
                  </TableCell>
                  <TableCell className="text-text-secondary">
                    {new Date(u.createdAt).toLocaleDateString('ru-RU')}
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

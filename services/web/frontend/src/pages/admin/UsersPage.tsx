import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Ban, CheckCircle2, KeyRound } from 'lucide-react'
import { AppLayout } from '@/layouts/AppLayout'
import { Card } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { adminUsersApi, type AdminUserListItem } from '@/api/admin'
import { SetPasswordDialog } from './SetPasswordDialog'

export default function UsersPage() {
  const queryClient = useQueryClient()
  const [pwdUser, setPwdUser] = useState<AdminUserListItem | null>(null)
  const { data, isLoading, isError, error } = useQuery({
    queryKey: ['admin', 'users'],
    queryFn: () => adminUsersApi.list({ limit: 100 }),
  })

  const blockMutation = useMutation({
    mutationFn: (id: string) => adminUsersApi.block(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['admin', 'users'] }),
  })
  const unblockMutation = useMutation({
    mutationFn: (id: string) => adminUsersApi.unblock(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['admin', 'users'] }),
  })

  return (
    <AppLayout breadcrumbs={[{ label: 'Админ' }, { label: 'Пользователи' }]}>
      <div className="mb-8">
        <h1 className="text-h1 text-text-primary">Пользователи</h1>
        <p className="text-body text-text-secondary mt-1">
          Список зарегистрированных пользователей платформы и управление доступом
        </p>
      </div>

      <Card>
        {isLoading && <div className="p-6 text-text-secondary text-sm">Загрузка…</div>}
        {isError && (
          <div className="p-6 text-destructive text-sm">
            Не удалось загрузить список: {(error as Error).message}
          </div>
        )}
        {data && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Email</TableHead>
                <TableHead>Имя</TableHead>
                <TableHead>Роли</TableHead>
                <TableHead>Статус</TableHead>
                <TableHead>Создан</TableHead>
                <TableHead className="text-right">Действия</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.items.length === 0 && (
                <TableRow>
                  <TableCell colSpan={6} className="text-center text-text-secondary py-12">
                    Пользователей пока нет
                  </TableCell>
                </TableRow>
              )}
              {data.items.map((user) => (
                <TableRow key={user.id}>
                  <TableCell className="font-medium">{user.email}</TableCell>
                  <TableCell>{user.fullName || '—'}</TableCell>
                  <TableCell className="space-x-1">
                    {user.roles.map((r) => (
                      <Badge key={r} variant={r === 'Admin' ? 'secondary' : 'muted'}>{r}</Badge>
                    ))}
                  </TableCell>
                  <TableCell>
                    {user.isBlocked ? (
                      <Badge variant="destructive">Заблокирован</Badge>
                    ) : (
                      <Badge variant="success">Активен</Badge>
                    )}
                  </TableCell>
                  <TableCell className="text-text-secondary">
                    {new Date(user.createdAt).toLocaleDateString('ru-RU')}
                  </TableCell>
                  <TableCell className="text-right">
                    <div className="inline-flex items-center gap-2">
                      <Button
                        size="sm"
                        variant="outline"
                        className="gap-1"
                        onClick={() => setPwdUser(user)}
                      >
                        <KeyRound size={14} />
                        Пароль
                      </Button>
                      {user.isBlocked ? (
                        <Button
                          size="sm"
                          variant="outline"
                          onClick={() => unblockMutation.mutate(user.id)}
                          disabled={unblockMutation.isPending}
                        >
                          <CheckCircle2 size={14} />
                          Разблокировать
                        </Button>
                      ) : (
                        <Button
                          size="sm"
                          variant="outline"
                          onClick={() => blockMutation.mutate(user.id)}
                          disabled={blockMutation.isPending}
                        >
                          <Ban size={14} />
                          Заблокировать
                        </Button>
                      )}
                    </div>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Card>

      {pwdUser && (
        <SetPasswordDialog
          open
          onOpenChange={(o) => !o && setPwdUser(null)}
          userId={pwdUser.id}
          userEmail={pwdUser.email}
        />
      )}
    </AppLayout>
  )
}

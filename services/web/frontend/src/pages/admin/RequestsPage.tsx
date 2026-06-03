import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Check, KeyRound } from 'lucide-react'
import { AppLayout } from '@/layouts/AppLayout'
import { Card } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { adminUserRequestsApi, type AdminUserRequest } from '@/api/admin'
import { SetPasswordDialog } from './SetPasswordDialog'

const TYPE_LABEL: Record<string, string> = {
  passwordreset: 'Сброс пароля',
}

export default function RequestsPage() {
  const queryClient = useQueryClient()
  const { data, isLoading, isError, error } = useQuery({
    queryKey: ['admin', 'user-requests'],
    queryFn: () => adminUserRequestsApi.list(),
    refetchInterval: 30_000,
  })

  const resolveMutation = useMutation({
    mutationFn: (id: string) => adminUserRequestsApi.resolve(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['admin', 'user-requests'] }),
  })

  const [setPwdFor, setSetPwdFor] = useState<AdminUserRequest | null>(null)

  return (
    <AppLayout breadcrumbs={[{ label: 'Админ' }, { label: 'Запросы' }]}>
      <div className="mb-8">
        <h1 className="text-h1 text-text-primary">Запросы пользователей</h1>
        <p className="text-body text-text-secondary mt-1">
          Обращения от пользователей (сброс пароля и т.п.). Смените пароль вручную и отметьте обработанным.
        </p>
      </div>

      <Card>
        {isLoading && <div className="p-6 text-text-secondary text-sm">Загрузка…</div>}
        {isError && (
          <div className="p-6 text-destructive text-sm">
            Не удалось загрузить: {(error as Error).message}
          </div>
        )}
        {data && data.items.length === 0 && (
          <div className="p-10 text-center text-text-secondary">Запросов пока нет</div>
        )}
        {data && data.items.length > 0 && (
          <ul className="divide-y divide-border-subtle">
            {data.items.map((r) => (
              <li key={r.id} className="flex items-start gap-4 px-5 py-4 flex-wrap">
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-2 flex-wrap">
                    <span className="text-sm font-medium text-text-primary">{r.email}</span>
                    <Badge variant="muted">{TYPE_LABEL[r.type] ?? r.type}</Badge>
                    {r.status === 'new' ? (
                      <Badge variant="secondary">Новый</Badge>
                    ) : (
                      <Badge variant="success">Обработан</Badge>
                    )}
                    {!r.userId && (
                      <span className="text-xs text-text-tertiary">аккаунт по email не найден</span>
                    )}
                  </div>
                  {r.message && <div className="text-xs text-text-secondary mt-1">{r.message}</div>}
                  <div className="text-xs text-text-tertiary mt-1">
                    {new Date(r.createdAt).toLocaleString('ru-RU')}
                  </div>
                </div>
                <div className="flex items-center gap-2 shrink-0">
                  {r.userId && (
                    <Button size="sm" variant="outline" className="gap-1" onClick={() => setSetPwdFor(r)}>
                      <KeyRound size={14} />
                      Сменить пароль
                    </Button>
                  )}
                  {r.status === 'new' && (
                    <Button
                      size="sm"
                      variant="outline"
                      className="gap-1"
                      disabled={resolveMutation.isPending}
                      onClick={() => resolveMutation.mutate(r.id)}
                    >
                      <Check size={14} />
                      Обработан
                    </Button>
                  )}
                </div>
              </li>
            ))}
          </ul>
        )}
      </Card>

      {setPwdFor?.userId && (
        <SetPasswordDialog
          open
          onOpenChange={(o) => !o && setSetPwdFor(null)}
          userId={setPwdFor.userId}
          userEmail={setPwdFor.email}
        />
      )}
    </AppLayout>
  )
}

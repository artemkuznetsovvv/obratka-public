import { useEffect, useState, type ReactNode } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, Ban, CheckCircle2, KeyRound, Loader2, Pencil } from 'lucide-react'
import { AppLayout } from '@/layouts/AppLayout'
import { Card } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { adminUsersApi, type AdminUpdateUserBody, type AdminUserDetails } from '@/api/admin'
import { describeApiError } from '@/api/errors'
import { cn } from '@/lib/utils'
import { SOURCE_LABEL } from '@/pages/history/analysisStatus'
import { SetPasswordDialog } from './SetPasswordDialog'

export default function UserDetailPage() {
  const { userId } = useParams<{ userId: string }>()
  const navigate = useNavigate()
  const qc = useQueryClient()
  const [editOpen, setEditOpen] = useState(false)
  const [pwdOpen, setPwdOpen] = useState(false)
  const [actionMsg, setActionMsg] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null)

  const { data, isLoading, isError, error } = useQuery({
    queryKey: ['admin', 'users', userId],
    queryFn: () => adminUsersApi.get(userId!),
    enabled: !!userId,
  })

  const invalidate = () => {
    qc.invalidateQueries({ queryKey: ['admin', 'users', userId] })
    qc.invalidateQueries({ queryKey: ['admin', 'users'] })
  }

  const blockM = useMutation({
    mutationFn: () =>
      data!.isBlocked ? adminUsersApi.unblock(userId!) : adminUsersApi.block(userId!),
    onSuccess: () => {
      const wasBlocked = data!.isBlocked
      invalidate()
      setActionMsg({
        kind: 'ok',
        text: wasBlocked
          ? 'Пользователь разблокирован.'
          : 'Пользователь заблокирован, мониторинги поставлены на паузу.',
      })
    },
    onError: (e) => setActionMsg({ kind: 'err', text: describeApiError(e) }),
  })

  const onToggleBlock = () => {
    if (!data) return
    const msg = data.isBlocked
      ? `Разблокировать ${data.email}? Доступ вернётся, но мониторинги останутся на паузе — пользователь перезапустит их сам.`
      : `Заблокировать ${data.email}? Пользователь не сможет войти, активные мониторинги будут поставлены на паузу.`
    if (confirm(msg)) blockM.mutate()
  }

  const headerLabel = data?.email ?? (userId ? `${userId.slice(0, 8)}…` : '—')

  return (
    <AppLayout
      breadcrumbs={[
        { label: 'Админ' },
        { label: 'Пользователи', to: '/admin/users' },
        { label: headerLabel },
      ]}
    >
      <div className="mb-6">
        <Button variant="outline" size="sm" onClick={() => navigate('/admin/users')} className="gap-2">
          <ArrowLeft size={14} />
          К списку
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
          {actionMsg && (
            <div
              className={cn(
                'rounded-lg border px-4 py-3 text-sm',
                actionMsg.kind === 'ok'
                  ? 'bg-emerald-50 border-emerald-200 text-emerald-800'
                  : 'bg-rose-50 border-rose-200 text-rose-700',
              )}
            >
              {actionMsg.text}
            </div>
          )}

          <Card className="p-6 space-y-4">
            <h2 className="text-h3 text-text-primary">Основная информация</h2>
            <div className="grid grid-cols-2 md:grid-cols-3 gap-4 text-sm">
              <Cell label="ID"><span className="font-mono text-xs break-all">{data.id}</span></Cell>
              <Cell label="Email">{data.email}</Cell>
              <Cell label="Имя">{data.fullName || '—'}</Cell>
              <Cell label="Телефон">{data.phoneNumber || '—'}</Cell>
              <Cell label="Статус">
                {data.isBlocked ? (
                  <Badge variant="destructive">Заблокирован</Badge>
                ) : (
                  <Badge variant="success">Активен</Badge>
                )}
              </Cell>
              <Cell label="Роли">{data.roles.length ? data.roles.join(', ') : '—'}</Cell>
              <Cell label="Регистрация">{new Date(data.createdAt).toLocaleString('ru-RU')}</Cell>
              <Cell label="Последняя активность">
                {data.lastActivityAt ? new Date(data.lastActivityAt).toLocaleString('ru-RU') : '—'}
              </Cell>
            </div>
          </Card>

          <Card className="p-6 space-y-3">
            <h2 className="text-h3 text-text-primary">Компании ({data.companies.length})</h2>
            {data.companies.length === 0 ? (
              <p className="text-sm text-text-secondary">У пользователя нет компаний.</p>
            ) : (
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Название</TableHead>
                    <TableHead>Подключена</TableHead>
                    <TableHead>Источники</TableHead>
                    <TableHead>Филиалов</TableHead>
                    <TableHead>Анализов</TableHead>
                    <TableHead>Live-мониторинг</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {data.companies.map((c) => (
                    <TableRow
                      key={c.id}
                      className="cursor-pointer"
                      onClick={() => navigate(`/admin/companies/${c.id}`)}
                    >
                      <TableCell className="font-medium">{c.name}</TableCell>
                      <TableCell className="text-text-secondary">
                        {new Date(c.createdAt).toLocaleDateString('ru-RU')}
                      </TableCell>
                      <TableCell>
                        <div className="flex flex-wrap gap-1">
                          {c.sources.length
                            ? c.sources.map((s) => (
                                <Badge key={s} variant="muted">{SOURCE_LABEL[s] ?? s}</Badge>
                              ))
                            : '—'}
                        </div>
                      </TableCell>
                      <TableCell className="text-text-secondary">{c.branchCount}</TableCell>
                      <TableCell className="text-text-secondary">{c.analysesCount ?? '—'}</TableCell>
                      <TableCell>
                        {c.hasActiveMonitoring ? (
                          <Badge variant="success">Да</Badge>
                        ) : (
                          <Badge variant="muted">Нет</Badge>
                        )}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
          </Card>

          <Card className="p-6 space-y-3">
            <h2 className="text-h3 text-text-primary">Действия</h2>
            <div className="flex flex-wrap gap-3">
              <Button variant="outline" className="gap-2" onClick={() => setEditOpen(true)}>
                <Pencil size={14} />
                Редактировать
              </Button>
              {data.isBlocked ? (
                <Button variant="outline" className="gap-2" onClick={onToggleBlock} disabled={blockM.isPending}>
                  <CheckCircle2 size={14} />
                  Разблокировать
                </Button>
              ) : (
                <Button
                  variant="outline"
                  className="gap-2 text-destructive hover:bg-destructive/10"
                  onClick={onToggleBlock}
                  disabled={blockM.isPending}
                >
                  <Ban size={14} />
                  Заблокировать
                </Button>
              )}
              <Button variant="outline" className="gap-2" onClick={() => setPwdOpen(true)}>
                <KeyRound size={14} />
                Сбросить пароль
              </Button>
            </div>
            <p className="text-xs text-text-tertiary">
              Блокировка ставит мониторинги пользователя на паузу. Разблокировка возвращает доступ, но
              мониторинги пользователь перезапускает сам.
            </p>
          </Card>
        </div>
      )}

      {data && (
        <EditUserDialog
          open={editOpen}
          onOpenChange={setEditOpen}
          user={data}
          onSaved={() => {
            invalidate()
            setActionMsg({ kind: 'ok', text: 'Данные пользователя обновлены.' })
          }}
        />
      )}
      {data && (
        <SetPasswordDialog open={pwdOpen} onOpenChange={setPwdOpen} userId={data.id} userEmail={data.email} />
      )}
    </AppLayout>
  )
}

function Cell({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div>
      <div className="text-caption uppercase text-text-tertiary mb-1">{label}</div>
      <div className="text-text-primary">{children}</div>
    </div>
  )
}

function EditUserDialog({
  open,
  onOpenChange,
  user,
  onSaved,
}: {
  open: boolean
  onOpenChange: (o: boolean) => void
  user: AdminUserDetails
  onSaved: () => void
}) {
  const [email, setEmail] = useState(user.email)
  const [fullName, setFullName] = useState(user.fullName)
  const [phone, setPhone] = useState(user.phoneNumber ?? '')

  useEffect(() => {
    if (!open) return
    setEmail(user.email)
    setFullName(user.fullName)
    setPhone(user.phoneNumber ?? '')
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open])

  const m = useMutation({
    mutationFn: (body: AdminUpdateUserBody) => adminUsersApi.update(user.id, body),
    onSuccess: () => {
      onOpenChange(false)
      onSaved()
    },
  })

  const canSubmit = email.trim().length > 0 && fullName.trim().length > 0 && !m.isPending

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <Pencil size={18} className="text-brand" />
            Редактировать пользователя
          </DialogTitle>
          <DialogDescription>Email, имя и телефон. Пароль меняется отдельно.</DialogDescription>
        </DialogHeader>

        <div className="space-y-4">
          <Field label="Email">
            <Input value={email} onChange={(e) => setEmail(e.target.value)} type="email" autoFocus />
          </Field>
          <Field label="Имя">
            <Input value={fullName} onChange={(e) => setFullName(e.target.value)} />
          </Field>
          <Field label="Телефон (опц.)">
            <Input value={phone} onChange={(e) => setPhone(e.target.value)} placeholder="+7…" />
          </Field>

          {m.isError && <p className="text-sm text-destructive">{describeApiError(m.error)}</p>}

          <DialogFooter>
            <Button variant="outline" onClick={() => onOpenChange(false)} disabled={m.isPending}>
              Отмена
            </Button>
            <Button
              className="gap-2"
              disabled={!canSubmit}
              onClick={() =>
                m.mutate({
                  email: email.trim(),
                  fullName: fullName.trim(),
                  phoneNumber: phone.trim() || null,
                })
              }
            >
              {m.isPending && <Loader2 size={14} className="animate-spin" />}
              Сохранить
            </Button>
          </DialogFooter>
        </div>
      </DialogContent>
    </Dialog>
  )
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="space-y-2">
      <Label className="text-caption uppercase text-text-secondary">{label}</Label>
      {children}
    </div>
  )
}

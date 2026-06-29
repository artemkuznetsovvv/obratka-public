import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { CalendarClock, Check, Pause, Pencil, Play, Plus, RotateCcw, Trash2, X } from 'lucide-react'
import { AppLayout } from '@/layouts/AppLayout'
import { Card } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Badge } from '@/components/ui/badge'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { adminProxiesApi, adminTelegramProxiesApi, type CreateParserProxyRequest } from '@/api/admin'

const proxySchema = z.object({
  host: z.string().min(1, 'Укажите host'),
  port: z.coerce.number().int().min(1).max(65535),
  protocol: z.enum(['http', 'https', 'socks5']),
  username: z.string().optional(),
  password: z.string().optional(),
  notes: z.string().optional(),
  expiresAt: z.string().optional(),
})

// <input type="datetime-local"> отдаёт "YYYY-MM-DDTHH:mm" без таймзоны, трактуется как local.
// Конвертация туда-обратно с ISO-строкой (UTC), которую возвращает/принимает API.
const toLocalInput = (iso: string | null): string => {
  if (!iso) return ''
  const d = new Date(iso)
  const pad = (n: number) => n.toString().padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
}
const fromLocalInput = (s: string): string | null => (s ? new Date(s).toISOString() : null)
const isExpired = (iso: string | null) => iso !== null && new Date(iso) <= new Date()

export default function ProxiesPage() {
  return (
    <AppLayout breadcrumbs={[{ label: 'Админ' }, { label: 'Прокси' }]}>
      <div className="mb-6">
        <h1 className="text-h1 text-text-primary">Прокси</h1>
        <p className="text-body text-text-secondary mt-1">
          Пулы прокси для Parser-Service (сбор отзывов) и Telegram-бота (обход блокировки api.telegram.org).
        </p>
      </div>

      <Tabs defaultValue="parser">
        <TabsList className="mb-4">
          <TabsTrigger value="parser">Parser-Service</TabsTrigger>
          <TabsTrigger value="telegram">Telegram-бот</TabsTrigger>
        </TabsList>

        <TabsContent value="parser">
          <ProxyManager
            api={adminProxiesApi}
            queryKey={['admin', 'proxies']}
            defaultProtocol="http"
            description="CRUD проксируется в Parser-Service. ID — числовой (`int`), назначается на стороне Parser-Service."
          />
        </TabsContent>

        <TabsContent value="telegram">
          <ProxyManager
            api={adminTelegramProxiesApi}
            queryKey={['admin', 'telegram-proxies']}
            defaultProtocol="socks5"
            description="Пул прокси Telegram-бота (хранится в Web API). При connectivity-сбое long-poll бот автоматически переключается на следующий доступный прокси, упавший уходит в cooldown."
          />
        </TabsContent>
      </Tabs>
    </AppLayout>
  )
}

interface ProxyManagerProps {
  api: typeof adminProxiesApi
  queryKey: string[]
  defaultProtocol: 'http' | 'socks5'
  description: string
}

// Переиспользуемый менеджер пула прокси (одинаковая модель/CRUD у Parser и Telegram).
function ProxyManager({ api, queryKey, defaultProtocol, description }: ProxyManagerProps) {
  const queryClient = useQueryClient()
  const [showAdd, setShowAdd] = useState(false)
  const [editingExpiresId, setEditingExpiresId] = useState<number | null>(null)
  const [editingExpiresValue, setEditingExpiresValue] = useState('')

  const { data, isLoading, isError, error } = useQuery({
    queryKey,
    queryFn: () => api.list(),
  })

  const invalidate = () => queryClient.invalidateQueries({ queryKey })

  const create = useMutation({
    mutationFn: (req: CreateParserProxyRequest) => api.create(req),
    onSuccess: () => {
      invalidate()
      setShowAdd(false)
    },
  })
  const del = useMutation({ mutationFn: (id: number) => api.delete(id), onSuccess: invalidate })
  const disable = useMutation({ mutationFn: (id: number) => api.disable(id), onSuccess: invalidate })
  const enable = useMutation({ mutationFn: (id: number) => api.enable(id), onSuccess: invalidate })
  const resetHealth = useMutation({ mutationFn: (id: number) => api.resetHealth(id), onSuccess: invalidate })
  const setExpires = useMutation({
    mutationFn: ({ id, expiresAt }: { id: number; expiresAt: string | null }) =>
      api.setExpiresAt(id, expiresAt),
    onSuccess: () => {
      invalidate()
      setEditingExpiresId(null)
    },
  })

  const startEditExpires = (id: number, current: string | null) => {
    setEditingExpiresId(id)
    setEditingExpiresValue(toLocalInput(current))
  }
  const cancelEditExpires = () => setEditingExpiresId(null)
  const saveEditExpires = (id: number) => {
    setExpires.mutate({ id, expiresAt: fromLocalInput(editingExpiresValue) })
  }

  return (
    <>
      <div className="mb-4 flex items-start justify-between gap-4">
        <p className="text-sm text-text-secondary max-w-3xl">{description}</p>
        <Button onClick={() => setShowAdd((v) => !v)} className="gap-2 shrink-0">
          <Plus size={16} />
          {showAdd ? 'Скрыть форму' : 'Добавить прокси'}
        </Button>
      </div>

      {showAdd && (
        <Card className="p-6 mb-6">
          <AddProxyForm
            defaultProtocol={defaultProtocol}
            isSubmitting={create.isPending}
            submitError={create.error ? (create.error as Error).message : null}
            onSubmit={(values) => create.mutate(values)}
          />
        </Card>
      )}

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
                <TableHead>Прокси</TableHead>
                <TableHead>Протокол</TableHead>
                <TableHead>Статус</TableHead>
                <TableHead>Сбои</TableHead>
                <TableHead>Cooldown</TableHead>
                <TableHead>Окончание срока действия</TableHead>
                <TableHead>Заметки</TableHead>
                <TableHead className="text-right">Действия</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.items.length === 0 && (
                <TableRow>
                  <TableCell colSpan={9} className="text-center text-text-secondary py-12">
                    Прокси не настроены
                  </TableCell>
                </TableRow>
              )}
              {data.items.map((p) => (
                <TableRow key={p.id}>
                  <TableCell className="text-text-tertiary">{p.id}</TableCell>
                  <TableCell className="font-medium">
                    {p.username ? `${p.username}@` : ''}
                    {p.host}:{p.port}
                  </TableCell>
                  <TableCell><Badge variant="muted">{p.protocol}</Badge></TableCell>
                  <TableCell>
                    {p.enabled ? <Badge variant="success">В ротации</Badge> : <Badge variant="muted">Выключен</Badge>}
                  </TableCell>
                  <TableCell className="text-text-secondary">{p.failureCount}</TableCell>
                  <TableCell className="text-text-secondary">
                    {p.cooldownUntil ? new Date(p.cooldownUntil).toLocaleString('ru-RU') : '—'}
                  </TableCell>
                  <TableCell className="text-text-secondary">
                    {editingExpiresId === p.id ? (
                      <div className="flex items-center gap-1">
                        <Input
                          type="datetime-local"
                          value={editingExpiresValue}
                          onChange={(e) => setEditingExpiresValue(e.target.value)}
                          className="h-8 w-44"
                          autoFocus
                        />
                        <Button
                          size="icon"
                          variant="outline"
                          onClick={() => saveEditExpires(p.id)}
                          disabled={setExpires.isPending}
                          title="Сохранить"
                        >
                          <Check size={14} />
                        </Button>
                        <Button size="icon" variant="outline" onClick={cancelEditExpires} title="Отмена">
                          <X size={14} />
                        </Button>
                      </div>
                    ) : (
                      <div className="flex items-center gap-2">
                        {p.expiresAt ? (
                          <span className={isExpired(p.expiresAt) ? 'text-destructive' : ''}>
                            {new Date(p.expiresAt).toLocaleString('ru-RU')}
                          </span>
                        ) : (
                          <span>—</span>
                        )}
                        {isExpired(p.expiresAt) && <Badge variant="destructive">Истёк</Badge>}
                      </div>
                    )}
                  </TableCell>
                  <TableCell className="text-text-secondary max-w-xs truncate">{p.notes || '—'}</TableCell>
                  <TableCell className="text-right space-x-1 whitespace-nowrap">
                    <Button
                      size="icon"
                      variant="outline"
                      onClick={() => startEditExpires(p.id, p.expiresAt)}
                      title="Изменить срок действия"
                    >
                      {p.expiresAt ? <Pencil size={14} /> : <CalendarClock size={14} />}
                    </Button>
                    {p.enabled ? (
                      <Button size="icon" variant="outline" onClick={() => disable.mutate(p.id)} title="Выключить">
                        <Pause size={14} />
                      </Button>
                    ) : (
                      <Button size="icon" variant="outline" onClick={() => enable.mutate(p.id)} title="Включить">
                        <Play size={14} />
                      </Button>
                    )}
                    <Button size="icon" variant="outline" onClick={() => resetHealth.mutate(p.id)} title="Сбросить health">
                      <RotateCcw size={14} />
                    </Button>
                    <Button
                      size="icon"
                      variant="outline"
                      onClick={() => {
                        if (confirm(`Удалить прокси ${p.host}:${p.port}?`)) del.mutate(p.id)
                      }}
                      title="Удалить"
                      className="text-destructive hover:bg-destructive/10"
                    >
                      <Trash2 size={14} />
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Card>
    </>
  )
}

interface AddProxyFormProps {
  onSubmit: (values: CreateParserProxyRequest) => void
  isSubmitting: boolean
  submitError: string | null
  defaultProtocol: 'http' | 'socks5'
}

type ProxyFormValues = z.infer<typeof proxySchema>

function AddProxyForm({ onSubmit, isSubmitting, submitError, defaultProtocol }: AddProxyFormProps) {
  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<ProxyFormValues>({
    resolver: zodResolver(proxySchema),
    defaultValues: { protocol: defaultProtocol, port: defaultProtocol === 'socks5' ? 1080 : 8080 },
  })

  return (
    <form
      onSubmit={handleSubmit((values) =>
        onSubmit({
          host: values.host,
          port: values.port,
          protocol: values.protocol,
          username: values.username || null,
          password: values.password || null,
          notes: values.notes || null,
          expiresAt: fromLocalInput(values.expiresAt ?? ''),
        }),
      )}
      className="grid grid-cols-1 gap-4 md:grid-cols-3"
    >
      <FormField label="Host" error={errors.host?.message}>
        <Input placeholder="185.147.131.141" {...register('host')} />
      </FormField>
      <FormField label="Port" error={errors.port?.message}>
        <Input type="number" {...register('port')} />
      </FormField>
      <FormField label="Protocol" error={errors.protocol?.message}>
        <select
          className="flex h-10 w-full rounded-lg border border-border-subtle bg-card px-3 text-sm"
          {...register('protocol')}
        >
          <option value="http">http</option>
          <option value="https">https</option>
          <option value="socks5">socks5</option>
        </select>
      </FormField>
      <FormField label="Username (опц.)" error={errors.username?.message}>
        <Input {...register('username')} />
      </FormField>
      <FormField label="Password (опц.)" error={errors.password?.message}>
        <Input type="password" {...register('password')} />
      </FormField>
      <FormField label="Заметки (опц.)" error={errors.notes?.message}>
        <Input placeholder="мобильный МТС" {...register('notes')} />
      </FormField>
      <FormField label="Окончание срока действия (опц.)" error={errors.expiresAt?.message}>
        <Input type="datetime-local" {...register('expiresAt')} />
      </FormField>
      <div className="md:col-span-3 flex items-center justify-end gap-3">
        {submitError && <p className="text-sm text-destructive mr-auto">{submitError}</p>}
        <Button type="submit" disabled={isSubmitting}>
          {isSubmitting ? 'Добавляем…' : 'Добавить'}
        </Button>
      </div>
    </form>
  )
}

function FormField({ label, error, children }: { label: string; error?: string; children: React.ReactNode }) {
  return (
    <div className="space-y-2">
      <Label className="text-caption uppercase text-text-secondary">{label}</Label>
      {children}
      {error && <p className="text-xs text-destructive">{error}</p>}
    </div>
  )
}

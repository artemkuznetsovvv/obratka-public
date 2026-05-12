import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { Pause, Play, Plus, RotateCcw, Trash2 } from 'lucide-react'
import { AppLayout } from '@/layouts/AppLayout'
import { Card } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Badge } from '@/components/ui/badge'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { adminProxiesApi, type CreateParserProxyRequest } from '@/api/admin'

const proxySchema = z.object({
  host: z.string().min(1, 'Укажите host'),
  port: z.coerce.number().int().min(1).max(65535),
  protocol: z.enum(['http', 'https', 'socks5']),
  username: z.string().optional(),
  password: z.string().optional(),
  notes: z.string().optional(),
})

export default function ProxiesPage() {
  const queryClient = useQueryClient()
  const [showAdd, setShowAdd] = useState(false)

  const { data, isLoading, isError, error } = useQuery({
    queryKey: ['admin', 'proxies'],
    queryFn: () => adminProxiesApi.list(),
  })

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['admin', 'proxies'] })

  const create = useMutation({
    mutationFn: (req: CreateParserProxyRequest) => adminProxiesApi.create(req),
    onSuccess: () => {
      invalidate()
      setShowAdd(false)
    },
  })
  const del = useMutation({ mutationFn: (id: number) => adminProxiesApi.delete(id), onSuccess: invalidate })
  const disable = useMutation({ mutationFn: (id: number) => adminProxiesApi.disable(id), onSuccess: invalidate })
  const enable = useMutation({ mutationFn: (id: number) => adminProxiesApi.enable(id), onSuccess: invalidate })
  const resetHealth = useMutation({ mutationFn: (id: number) => adminProxiesApi.resetHealth(id), onSuccess: invalidate })

  return (
    <AppLayout breadcrumbs={[{ label: 'Админ' }, { label: 'Прокси' }]}>
      <div className="mb-8 flex items-start justify-between">
        <div>
          <h1 className="text-h1 text-text-primary">Прокси Parser-Service</h1>
          <p className="text-body text-text-secondary mt-1">
            CRUD проксируется в Parser-Service. ID — числовой (`int`), назначается на стороне Parser-Service.
          </p>
        </div>
        <Button onClick={() => setShowAdd((v) => !v)} className="gap-2">
          <Plus size={16} />
          {showAdd ? 'Скрыть форму' : 'Добавить прокси'}
        </Button>
      </div>

      {showAdd && (
        <Card className="p-6 mb-6">
          <AddProxyForm
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
                <TableHead>Заметки</TableHead>
                <TableHead className="text-right">Действия</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.items.length === 0 && (
                <TableRow>
                  <TableCell colSpan={8} className="text-center text-text-secondary py-12">
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
                  <TableCell className="text-text-secondary max-w-xs truncate">{p.notes || '—'}</TableCell>
                  <TableCell className="text-right space-x-1 whitespace-nowrap">
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
    </AppLayout>
  )
}

interface AddProxyFormProps {
  onSubmit: (values: CreateParserProxyRequest) => void
  isSubmitting: boolean
  submitError: string | null
}

type ProxyFormValues = z.infer<typeof proxySchema>

function AddProxyForm({ onSubmit, isSubmitting, submitError }: AddProxyFormProps) {
  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<ProxyFormValues>({
    resolver: zodResolver(proxySchema),
    defaultValues: { protocol: 'http', port: 8080 },
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

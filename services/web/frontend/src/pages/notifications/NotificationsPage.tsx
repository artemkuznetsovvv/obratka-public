import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Bell,
  Building2,
  CheckCircle2,
  ExternalLink,
  Link2,
  Link2Off,
  Loader2,
  Send,
} from 'lucide-react'
import { AppLayout } from '@/layouts/AppLayout'
import { Card } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Switch } from '@/components/ui/switch'
import { describeApiError } from '@/api/errors'
import { notificationsApi } from '@/api/notifications'
import {
  monitoringsApi,
  MONITORING_STATUS_LABEL,
  type MonitoringStatus,
  type MonitoringListItem,
} from '@/api/monitorings'

export default function NotificationsPage() {
  return (
    <AppLayout breadcrumbs={[{ label: 'Уведомления' }]}>
      <div className="max-w-2xl mx-auto">
        <div className="mb-8 flex items-center gap-2">
          <Bell size={22} className="text-brand" />
          <div>
            <h1 className="text-h1 text-text-primary">Уведомления</h1>
            <p className="text-body text-text-secondary mt-1">
              Подключите Telegram и управляйте уведомлениями по мониторингам
            </p>
          </div>
        </div>

        <div className="space-y-6">
          <TelegramCard />
          <MonitoringsCard />
        </div>
      </div>
    </AppLayout>
  )
}

// ----- Telegram: привязка/отвязка -----
function TelegramCard() {
  const queryClient = useQueryClient()
  const statusQuery = useQuery({
    queryKey: ['notifications', 'telegram-status'],
    queryFn: notificationsApi.telegramStatus,
    // Поллим только когда привязка возможна, но ещё не сделана. Если бот не настроен — не поллим.
    refetchInterval: (q) =>
      q.state.data?.linked || q.state.data?.configured === false ? false : 5_000,
  })

  const linkM = useMutation({
    mutationFn: notificationsApi.telegramLink,
  })

  const unlinkM = useMutation({
    mutationFn: notificationsApi.telegramUnlink,
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ['notifications', 'telegram-status'] }),
  })

  const status = statusQuery.data

  return (
    <Card className="p-6">
      <div className="flex items-center gap-2 mb-4">
        <Send size={18} className="text-brand" />
        <h2 className="text-h3 text-text-primary">Telegram</h2>
      </div>

      {statusQuery.isLoading ? (
        <p className="text-sm text-text-secondary">Загрузка…</p>
      ) : !status?.configured ? (
        <p className="text-sm text-text-secondary">
          Бот уведомлений ещё не настроен администратором системы. Привязка станет доступна позже.
        </p>
      ) : status.linked ? (
        <div className="space-y-4">
          <p className="text-sm text-emerald-700 flex items-center gap-1.5">
            <CheckCircle2 size={15} /> Telegram привязан — уведомления приходят в бота.
          </p>
          {unlinkM.isError && (
            <p className="text-sm text-destructive">{describeApiError(unlinkM.error)}</p>
          )}
          <Button
            variant="outline"
            className="gap-2 text-destructive hover:bg-destructive/10"
            disabled={unlinkM.isPending}
            onClick={() => {
              if (window.confirm('Отвязать Telegram? Уведомления перестанут приходить.'))
                unlinkM.mutate()
            }}
          >
            {unlinkM.isPending ? (
              <Loader2 size={16} className="animate-spin" />
            ) : (
              <Link2Off size={16} />
            )}
            Отвязать Telegram
          </Button>
        </div>
      ) : (
        <div className="space-y-4">
          <p className="text-sm text-text-secondary">
            Нажмите кнопку — откроется бот{status.botUsername ? ` @${status.botUsername}` : ''}.
            Затем нажмите в нём <span className="font-medium">Start</span>, чтобы завершить привязку.
            Эта страница обновится автоматически.
          </p>
          {linkM.isError && (
            <p className="text-sm text-destructive">{describeApiError(linkM.error)}</p>
          )}
          {linkM.isSuccess && (
            <p className="text-sm text-text-secondary flex items-center gap-1.5">
              <ExternalLink size={14} />
              Если бот не открылся,{' '}
              <a
                href={linkM.data.deepLink}
                target="_blank"
                rel="noopener noreferrer"
                className="text-brand underline"
              >
                откройте ссылку вручную
              </a>
              .
            </p>
          )}
          <Button
            className="gap-2"
            disabled={linkM.isPending}
            onClick={() => {
              // Открываем вкладку синхронно (в обработчике клика), чтобы не попасть под попап-блокер,
              // и направляем её на deep-link, когда придёт ответ. Фолбэк — ссылка вручную ниже.
              const w = window.open('about:blank', '_blank')
              linkM.mutate(undefined, {
                onSuccess: (data) => {
                  if (w) {
                    w.opener = null
                    w.location.href = data.deepLink
                  } else {
                    window.open(data.deepLink, '_blank', 'noopener,noreferrer')
                  }
                },
                onError: () => w?.close(),
              })
            }}
          >
            {linkM.isPending ? <Loader2 size={16} className="animate-spin" /> : <Link2 size={16} />}
            Подключить Telegram
          </Button>
        </div>
      )}
    </Card>
  )
}

// ----- Подписки по мониторингам -----
function MonitoringsCard() {
  const queryClient = useQueryClient()
  const listQuery = useQuery({ queryKey: ['monitorings'], queryFn: monitoringsApi.list })

  const toggleM = useMutation({
    mutationFn: ({ id, enabled }: { id: string; enabled: boolean }) =>
      monitoringsApi.setNotifications(id, enabled),
    // Оптимистично переключаем сразу, откатываем при ошибке, сверяемся с сервером по завершении.
    onMutate: async ({ id, enabled }) => {
      await queryClient.cancelQueries({ queryKey: ['monitorings'] })
      const prev = queryClient.getQueryData<MonitoringListItem[]>(['monitorings'])
      queryClient.setQueryData<MonitoringListItem[]>(['monitorings'], (old) =>
        old?.map((x) => (x.id === id ? { ...x, notificationsEnabled: enabled } : x)),
      )
      return { prev }
    },
    onError: (_e, _vars, ctx) => {
      if (ctx?.prev) queryClient.setQueryData(['monitorings'], ctx.prev)
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['monitorings'] }),
  })

  return (
    <Card className="p-6">
      <div className="flex items-center gap-2 mb-1">
        <Bell size={18} className="text-brand" />
        <h2 className="text-h3 text-text-primary">Мониторинги</h2>
      </div>
      <p className="text-sm text-text-secondary mb-4">
        Включайте или выключайте уведомления для каждого мониторинга отдельно.
      </p>

      {toggleM.isError && (
        <p className="text-sm text-destructive mb-3">{describeApiError(toggleM.error)}</p>
      )}

      {listQuery.isLoading ? (
        <p className="text-sm text-text-secondary">Загрузка…</p>
      ) : listQuery.isError ? (
        <p className="text-sm text-destructive">{describeApiError(listQuery.error)}</p>
      ) : (listQuery.data?.length ?? 0) === 0 ? (
        <p className="text-sm text-text-secondary">
          Пока нет мониторингов. Включите мониторинг на дашборде завершённого анализа.
        </p>
      ) : (
        <div className="divide-y divide-border-subtle">
          {listQuery.data!.map((m) => {
            const busy = toggleM.isPending && toggleM.variables?.id === m.id
            return (
              <div key={m.id} className="flex items-center justify-between gap-4 py-3 first:pt-0 last:pb-0">
                <div className="min-w-0">
                  <div className="flex items-center gap-2">
                    <Building2 size={15} className="text-text-tertiary shrink-0" />
                    <span className="text-sm font-medium text-text-primary truncate">
                      {m.companyName}
                    </span>
                  </div>
                  <div className="mt-0.5 text-xs text-text-tertiary">
                    {STATUS_HINT[m.status]} · {m.branches.length}{' '}
                    {pluralize(m.branches.length, ['филиал', 'филиала', 'филиалов'])}
                  </div>
                </div>
                <div className="flex items-center gap-2 shrink-0">
                  {busy && <Loader2 size={14} className="animate-spin text-text-tertiary" />}
                  <Switch
                    checked={m.notificationsEnabled}
                    disabled={busy}
                    aria-label={`Уведомления для ${m.companyName}`}
                    onCheckedChange={(enabled) => toggleM.mutate({ id: m.id, enabled })}
                  />
                </div>
              </div>
            )
          })}
        </div>
      )}
    </Card>
  )
}

const STATUS_HINT: Record<MonitoringStatus, string> = MONITORING_STATUS_LABEL

function pluralize(n: number, forms: [string, string, string]): string {
  const mod10 = n % 10
  const mod100 = n % 100
  if (mod10 === 1 && mod100 !== 11) return forms[0]
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return forms[1]
  return forms[2]
}

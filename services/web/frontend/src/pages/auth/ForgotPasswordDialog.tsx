import { useEffect, useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { CheckCircle2, KeyRound, Loader2 } from 'lucide-react'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { authApi } from '@/api/auth'
import { describeApiError } from '@/api/errors'

// Сброс пароля без email-флоу: пользователь оставляет запрос, который падает в борду админки.
// Админ меняет пароль вручную и связывается с пользователем.
export function ForgotPasswordDialog({
  open,
  onOpenChange,
  defaultEmail,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  defaultEmail?: string
}) {
  const [email, setEmail] = useState('')
  const [message, setMessage] = useState('')

  const submitM = useMutation({
    mutationFn: () => authApi.passwordResetRequest(email.trim(), message.trim() || undefined),
  })

  useEffect(() => {
    if (!open) return
    setEmail(defaultEmail ?? '')
    setMessage('')
    submitM.reset()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open])

  const canSubmit = email.trim().length > 0 && !submitM.isPending

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <KeyRound size={18} className="text-brand" />
            Сброс пароля
          </DialogTitle>
          <DialogDescription>
            Оставьте запрос — администратор сменит пароль вручную и пришлёт новый на вашу почту.
          </DialogDescription>
        </DialogHeader>

        {submitM.isSuccess ? (
          <div className="space-y-4">
            <div className="flex items-start gap-3 rounded-lg bg-emerald-50 border border-emerald-200 px-4 py-3 text-sm text-emerald-800">
              <CheckCircle2 size={18} className="shrink-0 mt-0.5" />
              <span>
                Запрос отправлен. Администратор обработает его и пришлёт новый пароль на указанную почту.
              </span>
            </div>
            <DialogFooter>
              <Button onClick={() => onOpenChange(false)}>Закрыть</Button>
            </DialogFooter>
          </div>
        ) : (
          <div className="space-y-4">
            <div className="space-y-2">
              <Label htmlFor="forgot-email" className="text-caption uppercase text-text-secondary">
                Email аккаунта
              </Label>
              <Input
                id="forgot-email"
                type="email"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                placeholder="example@company.com"
                className="h-11"
                autoFocus
              />
            </div>
            <div className="space-y-2">
              <Label htmlFor="forgot-message" className="text-caption uppercase text-text-secondary">
                Комментарий (необязательно)
              </Label>
              <textarea
                id="forgot-message"
                value={message}
                onChange={(e) => setMessage(e.target.value)}
                rows={3}
                maxLength={2000}
                placeholder="Например, как с вами связаться"
                className="w-full p-3 rounded-lg border border-border-subtle bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring focus:ring-offset-2 resize-y"
              />
            </div>

            {submitM.isError && (
              <p className="text-sm text-destructive">{describeApiError(submitM.error)}</p>
            )}

            <DialogFooter>
              <Button variant="outline" onClick={() => onOpenChange(false)} disabled={submitM.isPending}>
                Отмена
              </Button>
              <Button onClick={() => submitM.mutate()} disabled={!canSubmit} className="gap-2">
                {submitM.isPending && <Loader2 size={14} className="animate-spin" />}
                Отправить запрос на сброс пароля
              </Button>
            </DialogFooter>
          </div>
        )}
      </DialogContent>
    </Dialog>
  )
}

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
import { adminUsersApi } from '@/api/admin'
import { describeApiError } from '@/api/errors'

// Ручная смена пароля пользователю админом (флоу «Забыли пароль» без email).
// Пароль показан как обычный текст — админу нужно скопировать и передать пользователю.
export function SetPasswordDialog({
  open,
  onOpenChange,
  userId,
  userEmail,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  userId: string
  userEmail: string
}) {
  const [password, setPassword] = useState('')

  const submitM = useMutation({
    mutationFn: () => adminUsersApi.setPassword(userId, password),
  })

  useEffect(() => {
    if (!open) return
    setPassword('')
    submitM.reset()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open])

  const canSubmit = password.trim().length >= 8 && !submitM.isPending

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <KeyRound size={18} className="text-brand" />
            Новый пароль
          </DialogTitle>
          <DialogDescription>
            Задать новый пароль для <span className="font-medium text-text-primary">{userEmail}</span>.
            Активные сессии сбросятся — пользователь войдёт с новым паролем.
          </DialogDescription>
        </DialogHeader>

        {submitM.isSuccess ? (
          <div className="space-y-4">
            <div className="flex items-start gap-3 rounded-lg bg-emerald-50 border border-emerald-200 px-4 py-3 text-sm text-emerald-800">
              <CheckCircle2 size={18} className="shrink-0 mt-0.5" />
              <span>Пароль обновлён. Передайте новый пароль пользователю.</span>
            </div>
            <DialogFooter>
              <Button onClick={() => onOpenChange(false)}>Закрыть</Button>
            </DialogFooter>
          </div>
        ) : (
          <div className="space-y-4">
            <div className="space-y-2">
              <Label htmlFor="set-pwd" className="text-caption uppercase text-text-secondary">
                Новый пароль
              </Label>
              <Input
                id="set-pwd"
                type="text"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                placeholder="Минимум 8 символов"
                className="h-11"
                autoFocus
              />
              <p className="text-xs text-text-tertiary">
                Минимум 8 символов, минимум одна цифра и строчная буква.
              </p>
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
                Сохранить пароль
              </Button>
            </DialogFooter>
          </div>
        )}
      </DialogContent>
    </Dialog>
  )
}

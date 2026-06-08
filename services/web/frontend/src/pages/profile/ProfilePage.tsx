import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import {
  CheckCircle2,
  Eye,
  EyeOff,
  KeyRound,
  Loader2,
  Lock,
  Mail,
  Save,
  User as UserIcon,
} from 'lucide-react'
import { AppLayout } from '@/layouts/AppLayout'
import { Card } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { authApi } from '@/api/auth'
import { describeApiError } from '@/api/errors'
import { useAuth } from '@/auth/AuthContext'

export default function ProfilePage() {
  const { user } = useAuth()

  return (
    <AppLayout breadcrumbs={[{ label: 'Профиль' }]}>
      <div className="max-w-2xl mx-auto">
        <div className="mb-8">
          <h1 className="text-h1 text-text-primary">Профиль</h1>
          <p className="text-body text-text-secondary mt-1">
            Личные данные и доступ к аккаунту
          </p>
        </div>

        {!user ? (
          <Card className="p-6 text-text-secondary text-sm">Загрузка…</Card>
        ) : (
          <div className="space-y-6">
            <ProfileForm />
            <PasswordForm />
          </div>
        )}
      </div>
    </AppLayout>
  )
}

// ----- ФИО + email -----
function ProfileForm() {
  const { user, updateUser } = useAuth()
  const [fullName, setFullName] = useState(user?.fullName ?? '')
  const [email, setEmail] = useState(user?.email ?? '')

  const save = useMutation({
    mutationFn: () => authApi.updateProfile(fullName.trim(), email.trim()),
    onSuccess: (updated) => updateUser(updated),
  })

  const dirty = fullName.trim() !== (user?.fullName ?? '') || email.trim() !== (user?.email ?? '')
  const canSave = dirty && fullName.trim().length > 0 && email.trim().length > 0 && !save.isPending

  return (
    <Card className="p-6">
      <div className="flex items-center gap-2 mb-4">
        <UserIcon size={18} className="text-brand" />
        <h2 className="text-h3 text-text-primary">Личные данные</h2>
      </div>

      <div className="space-y-4">
        <div className="space-y-2">
          <Label htmlFor="profile-name" className="text-caption uppercase text-text-secondary">
            ФИО
          </Label>
          <div className="relative">
            <UserIcon size={16} className="absolute left-3 top-1/2 -translate-y-1/2 text-text-tertiary" />
            <Input
              id="profile-name"
              value={fullName}
              onChange={(e) => setFullName(e.target.value)}
              placeholder="Иван Иванов"
              className="h-11 pl-9"
            />
          </div>
        </div>

        <div className="space-y-2">
          <Label htmlFor="profile-email" className="text-caption uppercase text-text-secondary">
            Email
          </Label>
          <div className="relative">
            <Mail size={16} className="absolute left-3 top-1/2 -translate-y-1/2 text-text-tertiary" />
            <Input
              id="profile-email"
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              placeholder="you@example.com"
              className="h-11 pl-9"
            />
          </div>
          <p className="text-xs text-text-tertiary">
            Email используется для входа. После смены входите с новым адресом.
          </p>
        </div>

        {save.isError && (
          <p className="text-sm text-destructive">{describeApiError(save.error)}</p>
        )}
        {save.isSuccess && !save.isPending && (
          <p className="text-sm text-emerald-700 flex items-center gap-1.5">
            <CheckCircle2 size={15} /> Данные сохранены.
          </p>
        )}

        <div className="flex justify-end">
          <Button onClick={() => save.mutate()} disabled={!canSave} className="gap-2">
            {save.isPending ? <Loader2 size={16} className="animate-spin" /> : <Save size={16} />}
            Сохранить
          </Button>
        </div>
      </div>
    </Card>
  )
}

// ----- Смена пароля -----
function PasswordForm() {
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [confirm, setConfirm] = useState('')
  const [localError, setLocalError] = useState<string | null>(null)

  const save = useMutation({
    mutationFn: () => authApi.changePassword(current, next),
    onSuccess: () => {
      setCurrent('')
      setNext('')
      setConfirm('')
    },
  })

  const submit = () => {
    setLocalError(null)
    if (next.length < 8) {
      setLocalError('Новый пароль — минимум 8 символов.')
      return
    }
    if (next !== confirm) {
      setLocalError('Пароли не совпадают.')
      return
    }
    save.mutate()
  }

  const canSave =
    current.length > 0 && next.length > 0 && confirm.length > 0 && !save.isPending

  return (
    <Card className="p-6">
      <div className="flex items-center gap-2 mb-4">
        <KeyRound size={18} className="text-brand" />
        <h2 className="text-h3 text-text-primary">Смена пароля</h2>
      </div>

      <div className="space-y-4">
        <PwdInput id="pwd-current" label="Текущий пароль" value={current} onChange={setCurrent} />
        <PwdInput id="pwd-new" label="Новый пароль" value={next} onChange={setNext} />
        <PwdInput id="pwd-confirm" label="Повторите новый пароль" value={confirm} onChange={setConfirm} />
        <p className="text-xs text-text-tertiary">
          Минимум 8 символов, минимум одна цифра и строчная буква.
        </p>

        {(localError || save.isError) && (
          <p className="text-sm text-destructive">
            {localError ?? describeApiError(save.error)}
          </p>
        )}
        {save.isSuccess && !save.isPending && (
          <p className="text-sm text-emerald-700 flex items-center gap-1.5">
            <CheckCircle2 size={15} /> Пароль обновлён.
          </p>
        )}

        <div className="flex justify-end">
          <Button onClick={submit} disabled={!canSave} className="gap-2">
            {save.isPending ? <Loader2 size={16} className="animate-spin" /> : <KeyRound size={16} />}
            Сменить пароль
          </Button>
        </div>
      </div>
    </Card>
  )
}

function PwdInput({
  id,
  label,
  value,
  onChange,
}: {
  id: string
  label: string
  value: string
  onChange: (v: string) => void
}) {
  const [show, setShow] = useState(false)
  return (
    <div className="space-y-2">
      <Label htmlFor={id} className="text-caption uppercase text-text-secondary">
        {label}
      </Label>
      <div className="relative">
        <Lock size={16} className="absolute left-3 top-1/2 -translate-y-1/2 text-text-tertiary" />
        <Input
          id={id}
          type={show ? 'text' : 'password'}
          value={value}
          onChange={(e) => onChange(e.target.value)}
          className="h-11 pl-9 pr-10"
          // Форма смены пароля, не вход: "new-password" гасит автозаполнение
          // сохранёнными кредами (в т.ч. в поле «Текущий пароль»), которое
          // браузеры игнорируют при autoComplete="off". Применяем ко всем трём.
          autoComplete="new-password"
        />
        <button
          type="button"
          onClick={() => setShow((v) => !v)}
          className="absolute right-3 top-1/2 -translate-y-1/2 text-text-tertiary hover:text-text-secondary"
          aria-label={show ? 'Скрыть пароль' : 'Показать пароль'}
          tabIndex={-1}
        >
          {show ? <EyeOff size={16} /> : <Eye size={16} />}
        </button>
      </div>
    </div>
  )
}

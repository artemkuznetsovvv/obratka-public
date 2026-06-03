import { forwardRef, useState } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { ArrowRight, Eye, EyeOff, Lock, Mail, User } from 'lucide-react'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Card } from '@/components/ui/card'
import { useAuth } from '@/auth/AuthContext'
import { cn } from '@/lib/utils'
import { ForgotPasswordDialog } from './ForgotPasswordDialog'

const loginSchema = z.object({
  email: z.string({ required_error: 'Введите email' }).min(1, 'Введите email').email('Введите корректный email'),
  password: z.string({ required_error: 'Введите пароль' }).min(1, 'Введите пароль'),
})

const registerSchema = z.object({
  email: z.string({ required_error: 'Введите email' }).min(1, 'Введите email').email('Введите корректный email'),
  password: z.string({ required_error: 'Введите пароль' }).min(8, 'Минимум 8 символов'),
  fullName: z.string({ required_error: 'Укажите имя' }).min(1, 'Укажите имя'),
})

type LoginValues = z.infer<typeof loginSchema>
type RegisterValues = z.infer<typeof registerSchema>

type LocationState = { from?: string }

export default function AuthPage() {
  const navigate = useNavigate()
  const location = useLocation()
  const fromState = (location.state as LocationState | null)?.from ?? '/'
  const [tab, setTab] = useState<'login' | 'register'>('login')

  return (
    <Card className="p-6">
      <Tabs value={tab} onValueChange={(v) => setTab(v as 'login' | 'register')}>
        <TabsList className="grid w-full grid-cols-2 h-auto rounded-none bg-transparent border-b border-border-subtle p-0">
          <UnderlineTab value="login">Вход</UnderlineTab>
          <UnderlineTab value="register">Регистрация</UnderlineTab>
        </TabsList>

        <TabsContent value="login" className="pt-6">
          <LoginForm onSuccess={() => navigate(fromState, { replace: true })} />
        </TabsContent>

        <TabsContent value="register" className="pt-6">
          <RegisterForm onSuccess={() => navigate('/', { replace: true })} />
        </TabsContent>
      </Tabs>
    </Card>
  )
}

function UnderlineTab({ value, children }: { value: string; children: React.ReactNode }) {
  return (
    <TabsTrigger
      value={value}
      className={cn(
        'rounded-none border-b-2 border-transparent bg-transparent py-3 text-h3 shadow-none',
        'data-[state=active]:border-brand data-[state=active]:bg-transparent data-[state=active]:text-text-primary data-[state=active]:shadow-none',
        'data-[state=inactive]:text-text-tertiary hover:text-text-secondary',
      )}
    >
      {children}
    </TabsTrigger>
  )
}

function LoginForm({ onSuccess }: { onSuccess: () => void }) {
  const { login } = useAuth()
  const [submitError, setSubmitError] = useState<string | null>(null)
  const [forgotOpen, setForgotOpen] = useState(false)
  const {
    register,
    handleSubmit,
    getValues,
    formState: { errors, isSubmitting },
  } = useForm<LoginValues>({
    resolver: zodResolver(loginSchema),
    defaultValues: { email: '', password: '' },
    mode: 'onTouched',
  })

  const onSubmit = handleSubmit(async (values) => {
    setSubmitError(null)
    try {
      await login(values)
      onSuccess()
    } catch (err) {
      setSubmitError(extractErrorMessage(err, 'Неверный email или пароль'))
    }
  })

  return (
    <form onSubmit={onSubmit} className="space-y-6">
      <FieldWithIcon
        id="login-email"
        label="Email адрес"
        type="email"
        autoComplete="email"
        placeholder="example@company.com"
        icon={<Mail size={18} />}
        error={errors.email?.message}
        {...register('email')}
      />

      <PasswordField
        id="login-password"
        label="Пароль"
        autoComplete="current-password"
        placeholder="••••••••"
        error={errors.password?.message}
        rightLabel={
          <button
            type="button"
            onClick={() => setForgotOpen(true)}
            className="text-caption uppercase text-brand hover:underline"
          >
            Забыли пароль?
          </button>
        }
        {...register('password')}
      />

      {submitError && <p className="text-sm text-destructive">{submitError}</p>}

      <Button type="submit" size="lg" className="w-full h-12" disabled={isSubmitting}>
        {isSubmitting ? 'Входим…' : 'Войти'}
        {!isSubmitting && <ArrowRight size={16} />}
      </Button>

      <ForgotPasswordDialog
        open={forgotOpen}
        onOpenChange={setForgotOpen}
        defaultEmail={getValues('email')}
      />
    </form>
  )
}

function RegisterForm({ onSuccess }: { onSuccess: () => void }) {
  const { register: registerUser } = useAuth()
  const [submitError, setSubmitError] = useState<string | null>(null)
  const {
    register,
    handleSubmit,
    formState: { errors, isSubmitting },
  } = useForm<RegisterValues>({
    resolver: zodResolver(registerSchema),
    defaultValues: { email: '', password: '', fullName: '' },
    mode: 'onTouched',
  })

  const onSubmit = handleSubmit(async (values) => {
    setSubmitError(null)
    try {
      await registerUser(values)
      onSuccess()
    } catch (err) {
      setSubmitError(extractErrorMessage(err, 'Не удалось создать аккаунт'))
    }
  })

  return (
    <form onSubmit={onSubmit} className="space-y-6">
      <FieldWithIcon
        id="reg-fullName"
        label="Имя и фамилия"
        type="text"
        autoComplete="name"
        placeholder="Иван Иванов"
        icon={<User size={18} />}
        error={errors.fullName?.message}
        {...register('fullName')}
      />
      <FieldWithIcon
        id="reg-email"
        label="Email адрес"
        type="email"
        autoComplete="email"
        placeholder="example@company.com"
        icon={<Mail size={18} />}
        error={errors.email?.message}
        {...register('email')}
      />
      <PasswordField
        id="reg-password"
        label="Пароль"
        autoComplete="new-password"
        placeholder="Минимум 8 символов"
        error={errors.password?.message}
        {...register('password')}
      />

      {submitError && <p className="text-sm text-destructive">{submitError}</p>}

      <Button type="submit" size="lg" className="w-full h-12" disabled={isSubmitting}>
        {isSubmitting ? 'Создаём…' : 'Создать аккаунт'}
        {!isSubmitting && <ArrowRight size={16} />}
      </Button>

      <p className="text-caption uppercase text-text-tertiary leading-relaxed">
        Регистрируясь, вы соглашаетесь с обработкой персональных данных согласно 152-ФЗ
      </p>
    </form>
  )
}

interface FieldWithIconProps extends React.InputHTMLAttributes<HTMLInputElement> {
  id: string
  label: string
  icon: React.ReactNode
  error?: string
}

const FieldWithIcon = forwardRef<HTMLInputElement, FieldWithIconProps>(
  ({ id, label, icon, error, className, ...inputProps }, ref) => (
    <div className="space-y-2">
      <Label htmlFor={id} className="text-caption uppercase text-text-secondary">{label}</Label>
      <div className="relative">
        <span className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-text-tertiary">
          {icon}
        </span>
        <Input id={id} ref={ref} className={cn('pl-10 h-12', className)} {...inputProps} />
      </div>
      {error && <p className="text-xs text-destructive">{error}</p>}
    </div>
  ),
)
FieldWithIcon.displayName = 'FieldWithIcon'

// Поле пароля с иконкой замка и кнопкой показать/скрыть (глаз). Используется и во входе,
// и в регистрации. rightLabel — опц. слот справа от лейбла (напр. ссылка «Забыли пароль?»).
interface PasswordFieldProps extends React.InputHTMLAttributes<HTMLInputElement> {
  id: string
  label: string
  error?: string
  rightLabel?: React.ReactNode
}

const PasswordField = forwardRef<HTMLInputElement, PasswordFieldProps>(
  ({ id, label, error, rightLabel, className, ...inputProps }, ref) => {
    const [show, setShow] = useState(false)
    return (
      <div className="space-y-2">
        <div className="flex items-center justify-between">
          <Label htmlFor={id} className="text-caption uppercase text-text-secondary">{label}</Label>
          {rightLabel}
        </div>
        <div className="relative">
          <span className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-text-tertiary">
            <Lock size={18} />
          </span>
          <Input
            id={id}
            ref={ref}
            className={cn('pl-10 pr-12 h-12', className)}
            {...inputProps}
            type={show ? 'text' : 'password'}
          />
          <button
            type="button"
            onClick={() => setShow((v) => !v)}
            className="absolute right-3 top-1/2 -translate-y-1/2 text-text-tertiary hover:text-text-secondary"
            aria-label={show ? 'Скрыть пароль' : 'Показать пароль'}
          >
            {show ? <EyeOff size={18} /> : <Eye size={18} />}
          </button>
        </div>
        {error && <p className="text-xs text-destructive">{error}</p>}
      </div>
    )
  },
)
PasswordField.displayName = 'PasswordField'

function extractErrorMessage(err: unknown, fallback: string): string {
  if (typeof err === 'object' && err !== null && 'response' in err) {
    const response = (err as { response?: { data?: { error?: string } } }).response
    if (response?.data?.error) return response.data.error
  }
  return fallback
}

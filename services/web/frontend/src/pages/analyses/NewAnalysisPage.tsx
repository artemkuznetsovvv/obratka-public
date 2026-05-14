import { useEffect, useState, type FormEvent } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { useMutation, useQuery } from '@tanstack/react-query'
import { ArrowRight, X } from 'lucide-react'
import { AppLayout } from '@/layouts/AppLayout'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Card } from '@/components/ui/card'
import { CityAutocomplete } from '@/components/ui/city-autocomplete'
import { companiesApi, type CreateCompanyRequest } from '@/api/companies'
import { AnalysisStepper } from './AnalysisStepper'

const OTHER = 'Другое'

const CATEGORIES: Array<{ value: string; subcategories: string[] }> = [
  {
    value: 'Общественное питание',
    subcategories: [
      'Кофейня',
      'Ресторан',
      'Кафе',
      'Фастфуд',
      'Пиццерия',
      'Суши-бар',
      'Бар',
      'Кондитерская',
      'Пекарня',
      'Столовая',
      'Доставка еды',
      'Кальянная',
      OTHER,
    ],
  },
  {
    value: 'Гостиничный бизнес',
    subcategories: [
      'Отель',
      'Хостел',
      'Апарт-отель',
      'Гостевой дом',
      'Санаторий',
      'База отдыха',
      'Глэмпинг',
      OTHER,
    ],
  },
  {
    value: 'Красота и здоровье',
    subcategories: [
      'Салон красоты',
      'Парикмахерская',
      'Барбершоп',
      'Маникюр и педикюр',
      'Косметология',
      'Массаж',
      'СПА',
      'Тату-салон',
      'Солярий',
      OTHER,
    ],
  },
  {
    value: 'Медицина',
    subcategories: [
      'Клиника',
      'Стоматология',
      'Медицинская лаборатория',
      'Аптека',
      'Оптика',
      'Ветеринарная клиника',
      'Реабилитационный центр',
      OTHER,
    ],
  },
  {
    value: 'Автомобильные услуги',
    subcategories: [
      'Автосервис',
      'Автомойка',
      'Шиномонтаж',
      'Автосалон',
      'Магазин запчастей',
      'Эвакуатор',
      'Тюнинг-ателье',
      OTHER,
    ],
  },
  {
    value: 'Розничная торговля',
    subcategories: [
      'Продуктовый магазин',
      'Одежда и обувь',
      'Электроника',
      'Мебель',
      'Книжный магазин',
      'Цветочный магазин',
      'Зоомагазин',
      'Хозтовары',
      'Ювелирный магазин',
      'Спорттовары',
      OTHER,
    ],
  },
  {
    value: 'Спорт и фитнес',
    subcategories: [
      'Фитнес-клуб',
      'Йога-студия',
      'Бассейн',
      'Танцевальная студия',
      'Единоборства',
      'Студия растяжки',
      'Тренажёрный зал',
      OTHER,
    ],
  },
  {
    value: 'Развлечения',
    subcategories: [
      'Кинотеатр',
      'Боулинг',
      'Бильярд',
      'Квест',
      'Караоке',
      'Игровая зона',
      'Концертный зал',
      'Театр',
      'Музей',
      'Парк аттракционов',
      OTHER,
    ],
  },
  {
    value: 'Образование',
    subcategories: [
      'Школа',
      'Детский сад',
      'Курсы',
      'Языковая школа',
      'Репетиторство',
      'Автошкола',
      'Школа искусств',
      OTHER,
    ],
  },
  {
    value: 'Бытовые услуги',
    subcategories: [
      'Химчистка',
      'Прачечная',
      'Ремонт обуви',
      'Ремонт техники',
      'Клининг',
      'Ателье',
      'Ремонт телефонов',
      OTHER,
    ],
  },
  {
    value: 'Туризм',
    subcategories: ['Турагентство', 'Туроператор', 'Экскурсии', OTHER],
  },
  {
    value: 'Онлайн-сервисы',
    subcategories: [
      'SaaS',
      'Маркетплейс',
      'Интернет-магазин',
      'Образовательная платформа',
      'Стриминговый сервис',
      'Финтех',
      'Сервис доставки',
      'Сервис бронирования',
      OTHER,
    ],
  },
  {
    value: OTHER,
    subcategories: [OTHER],
  },
]

export default function NewAnalysisPage() {
  const navigate = useNavigate()
  const [searchParams] = useSearchParams()
  const fromId = searchParams.get('from')

  const [name, setName] = useState('')
  const [category, setCategory] = useState('')
  const [subcategory, setSubcategory] = useState('')
  const [cities, setCities] = useState<string[]>([])
  const [description, setDescription] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [prefilled, setPrefilled] = useState(false)

  const subcategories = CATEGORIES.find((c) => c.value === category)?.subcategories ?? []

  const draftQuery = useQuery({
    queryKey: ['company', fromId],
    queryFn: () => companiesApi.get(fromId!),
    enabled: !!fromId,
  })

  useEffect(() => {
    if (!draftQuery.data || prefilled) return
    const c = draftQuery.data
    setName(c.name)
    setCategory(c.category ?? '')
    setSubcategory(c.subcategory ?? '')
    setCities(c.cities)
    setDescription(c.description ?? '')
    setPrefilled(true)
  }, [draftQuery.data, prefilled])

  const saveMutation = useMutation({
    mutationFn: (req: CreateCompanyRequest) =>
      fromId ? companiesApi.update(fromId, req) : companiesApi.create(req),
    onSuccess: (company) => {
      navigate(`/analyses/new/${company.id}/branches`)
    },
    onError: (err: unknown) => {
      const msg = err instanceof Error ? err.message : 'Не удалось сохранить компанию'
      setError(msg)
    },
  })

  const onSubmit = (e: FormEvent) => {
    e.preventDefault()
    setError(null)
    const trimmedName = name.trim()
    if (!trimmedName) {
      setError('Укажите название компании')
      return
    }
    if (cities.length === 0) {
      setError('Добавьте хотя бы один город')
      return
    }
    saveMutation.mutate({
      name: trimmedName,
      category: category || null,
      subcategory: subcategory || null,
      cities,
      description: description.trim() || null,
    })
  }

  return (
    <AppLayout breadcrumbs={[{ label: 'Главная', to: '/' }, { label: 'Новый анализ' }]}>
      <div className="max-w-4xl mx-auto">
        <div className="mb-10">
          <h1 className="text-h1 text-text-primary mb-3">Новый анализ</h1>
          <p className="text-body text-text-secondary max-w-2xl">
            Заполните данные компании, чтобы найти её карточки в источниках и подобрать релевантные
            рекомендации. Это поможет AI точнее интерпретировать отзывы ваших клиентов.
          </p>
        </div>

        <AnalysisStepper current={1} />

        <Card className="p-8 shadow-sm">
          <form className="space-y-6" onSubmit={onSubmit}>
            <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
              <div className="md:col-span-2">
                <label className="block text-h3 text-text-primary mb-2">Название компании</label>
                <Input
                  className="h-11"
                  placeholder="Напр: Coffee & Go или Ресторан 'Атмосфера'"
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                  maxLength={200}
                  autoFocus
                />
              </div>

              <div>
                <label className="block text-h3 text-text-primary mb-2">Категория бизнеса</label>
                <select
                  className="w-full h-11 px-3 rounded-lg border border-border-subtle bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring focus:ring-offset-2"
                  value={category}
                  onChange={(e) => {
                    setCategory(e.target.value)
                    setSubcategory('')
                  }}
                >
                  <option value="">Выберите категорию</option>
                  {CATEGORIES.map((c) => (
                    <option key={c.value} value={c.value}>
                      {c.value}
                    </option>
                  ))}
                </select>
              </div>

              <div>
                <label className="block text-h3 text-text-primary mb-2">Подкатегория бизнеса</label>
                <select
                  className="w-full h-11 px-3 rounded-lg border border-border-subtle bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring focus:ring-offset-2 disabled:opacity-50"
                  value={subcategory}
                  onChange={(e) => setSubcategory(e.target.value)}
                  disabled={subcategories.length === 0}
                >
                  <option value="">
                    {subcategories.length === 0 ? 'Сначала выберите категорию' : 'Выберите подкатегорию'}
                  </option>
                  {subcategories.map((s) => (
                    <option key={s} value={s}>
                      {s}
                    </option>
                  ))}
                </select>
              </div>

              <div className="md:col-span-2">
                <label className="block text-h3 text-text-primary mb-2">География присутствия</label>
                <CityAutocomplete
                  excluded={cities}
                  placeholder="Начните вводить название города (Москва, Санкт-Петербург, …)"
                  onSelect={(city) => {
                    if (!cities.some((c) => c.toLowerCase() === city.name.toLowerCase())) {
                      setCities([...cities, city.name])
                    }
                  }}
                />
                <p className="mt-2 text-xs text-text-tertiary">
                  Выбирайте города из подсказок — это гарантирует, что парсер найдёт их в источниках.
                </p>
                {cities.length > 0 && (
                  <div className="mt-3 flex flex-wrap gap-2">
                    {cities.map((city) => (
                      <span
                        key={city}
                        className="inline-flex items-center gap-1.5 px-3 py-1 bg-state-active-bg text-brand rounded-full text-sm font-medium border border-brand/20"
                      >
                        {city}
                        <button
                          type="button"
                          onClick={() => setCities(cities.filter((c) => c !== city))}
                          className="hover:text-brand-hover transition-colors"
                        >
                          <X size={14} />
                        </button>
                      </span>
                    ))}
                  </div>
                )}
              </div>

              <div className="md:col-span-2">
                <label className="block text-h3 text-text-primary mb-2">Дополнительный контекст</label>
                <textarea
                  className="w-full p-4 rounded-lg border border-border-subtle bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring focus:ring-offset-2 resize-y"
                  placeholder="Опишите особенности вашего бизнеса или ключевые преимущества, на которые стоит обратить внимание при анализе…"
                  rows={4}
                  value={description}
                  onChange={(e) => setDescription(e.target.value)}
                  maxLength={4000}
                />
                <p className="mt-2 text-xs text-text-tertiary">
                  Эти данные помогут AI лучше понимать специфику ваших отзывов.
                </p>
              </div>
            </div>

            {error && (
              <div className="rounded-lg border border-destructive/30 bg-destructive/5 px-4 py-3 text-sm text-destructive">
                {error}
              </div>
            )}

            <div className="pt-6 border-t border-border-subtle flex items-center justify-end gap-3">
              <Button type="button" variant="outline" onClick={() => navigate(-1)}>
                Отмена
              </Button>
              <Button type="submit" disabled={saveMutation.isPending} className="gap-2">
                {saveMutation.isPending
                  ? fromId
                    ? 'Сохраняем…'
                    : 'Создаём…'
                  : fromId
                    ? 'Продолжить поиск'
                    : 'Найти компанию'}
                {!saveMutation.isPending && <ArrowRight size={18} />}
              </Button>
            </div>
          </form>
        </Card>
      </div>
    </AppLayout>
  )
}

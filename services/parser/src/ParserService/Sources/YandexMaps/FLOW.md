# Flow сбора отзывов YandexMapsPlugin

## `FetchReviewsAsync(branch, period, ct)` — пошагово

```
Оркестратор вызывает plugin.FetchReviewsAsync(branch, period, ct)
│
│  branch.ExternalId = "1124715036" (businessId)
│  branch.ExternalUrl = "https://yandex.ru/maps/org/кофейня/1124715036/"
│  period = { From: 2024-01-01, To: 2024-06-01 }
│
├─ 1. RATE LIMITING
│     rateLimiter.WaitAsync(YandexMaps)
│     → рандомная пауза 2-5 сек (не перегружаем Яндекс)
│
├─ 2. ПРОКСИ
│     proxy = proxyRotator.GetProxyAsync(YandexMaps)
│     → ProxyInfo { Host, Port, User, Pass } или null (без прокси)
│
├─ 3. BROWSER CONTEXT
│     context = browserPool.AcquireAsync(new BrowserAcquireOptions(proxy))
│     → Playwright создаёт изолированный BrowserContext:
│       - свои cookies (чистые)
│       - свой proxy (если передан)
│       - UserAgent = Chrome 131, Locale = ru-RU, Timezone = Moscow
│
├─ 4. STEALTH
│     stealthConfigurator.ApplyStealthAsync(context)
│     → JS-инъекции в контекст ДО любой навигации:
│       - navigator.webdriver = undefined
│       - подмена canvas/WebGL fingerprint
│       - мок chrome.runtime, plugins, languages
│       - патч permissions API
│
├─ 5. СОЗДАНИЕ СЕССИИ (YandexSession.CreateAsync)
│     │
│     ├─ Открывает страницу организации в Playwright:
│     │   page.GotoAsync("https://yandex.ru/maps/org/кофейня/1124715036/")
│     │
│     ├─ Ждёт 1-3 сек (имитация человека)
│     │
│     ├─ Парсит HTML, ищет <script class="config-view">:
│     │   → извлекает csrfToken из JSON внутри тега
│     │
│     ├─ Читает cookies контекста:
│     │   → извлекает sessionId (cookie "Session_id" или "i")
│     │
│     └─ Возвращает YandexSession { CsrfToken, SessionId, Cookies, BrowserContext }
│
├─ 6. СБОР ОТЗЫВОВ (YandexReviewCollector.CollectAllReviewsAsync)
│     │
│     ├─ Проверяет: period.From задан?
│     │
│     │  ДА → ИНКРЕМЕНТАЛЬНЫЙ СБОР
│     │  │    Сортировка: by_time (новые первыми)
│     │  │    Страница 1 → 50 отзывов → маппинг в RawReview
│     │  │    Страница 2 → 50 отзывов → маппинг...
│     │  │    Страница 3 → встретили отзыв с датой < From
│     │  │                  → СТОП (дальше листать не нужно)
│     │  │    Итого: ~120 отзывов за секунды
│     │  │
│     │  НЕТ → ПОЛНЫЙ СБОР (dual sorting)
│     │       │
│     │       ├─ Проход 1: ranking = "by_time"
│     │       │   Страницы 1..12 (макс 600 отзывов)
│     │       │   Пауза 2-5 сек между страницами
│     │       │
│     │       ├─ Проход 2: ranking = "by_relevance_org"
│     │       │   Страницы 1..12 (ещё до 600 отзывов)
│     │       │   Пауза 2-5 сек между страницами
│     │       │
│     │       └─ Дедупликация по ExternalId (HashSet)
│     │           by_time:      480 отзывов
│     │           by_relevance: 510 отзывов
│     │           пересечение:  ~350
│     │           итого:        ~640 уникальных
│     │
│     │  КАЖДАЯ СТРАНИЦА — это:
│     │  │
│     │  ├─ YandexReviewApiClient.FetchReviewsPageAsync:
│     │  │   │
│     │  │   ├─ Формирует query params:
│     │  │   │   businessId=1124715036
│     │  │   │   csrfToken=abc123...
│     │  │   │   page=1
│     │  │   │   pageSize=50
│     │  │   │   ranking=by_time
│     │  │   │   sessionId=xyz789...
│     │  │   │
│     │  │   ├─ Djb2Hasher.ComputeS(params):
│     │  │   │   конкатенация значений → "1124715036abc123...150by_timexyz789..."
│     │  │   │   n=5381, для каждого char: n = (33*n) XOR char
│     │  │   │   → s = "2847593021"
│     │  │   │
│     │  │   ├─ Открывает новую page в том же BrowserContext
│     │  │   │   (cookies автоматически наследуются!)
│     │  │   │
│     │  │   ├─ GET https://yandex.ru/maps/api/business/fetchReviews?...&s=2847593021
│     │  │   │   Headers: Referer, Accept: application/json, X-Requested-With
│     │  │   │
│     │  │   └─ Десериализует JSON → YandexReviewsResponse
│     │  │       { reviews: [...], totalCount: 487, hasMore: true }
│     │  │
│     │  └─ Маппинг каждого YandexReviewDto → RawReview:
│     │      ExternalId  ← dto.ReviewId
│     │      Text        ← dto.Text
│     │      Date        ← FromUnixTimeSeconds(dto.UpdatedTime)
│     │      Stars       ← dto.Rating (1-5)
│     │      BranchId    ← branch.BranchId (Guid из оркестратора)
│     │
│     └─ return List<RawReview>
│
├─ 7. RETRY (при ошибках)
│     Attempt 1 → CSRF error → пересоздать сессию → retry
│     Attempt 2 → timeout → exponential backoff (4 сек) → retry
│     Attempt 3 → если снова ошибка → throw
│     Transient: HttpRequestException, TimeoutException, Playwright timeout/network
│
├─ 8. ВАЛИДАЦИЯ
│     reviews.Count < 5 → LogWarning (мало данных для анализа)
│
└─ finally:
      browserPool.ReleaseAsync(context)     → закрывает BrowserContext
      proxyRotator.ReleaseProxyAsync(proxy) → освобождает IP
```

## Ключевой принцип: один контекст = одна организация

```
Организация А                    Организация Б
┌──────────────────────┐        ┌──────────────────────┐
│ BrowserContext #1    │        │ BrowserContext #2    │
│ Proxy: 91.203.x.x   │        │ Proxy: 178.45.x.x   │
│ Cookies: сессия А    │        │ Cookies: сессия Б    │
│ CSRF: token_aaa      │        │ CSRF: token_bbb      │
│                      │        │                      │
│ Page 1 → 50 reviews  │        │ Page 1 → 50 reviews  │
│ Page 2 → 50 reviews  │        │ Page 2 → 50 reviews  │
│ ...                  │        │ ...                  │
│ IP НЕ меняется!      │        │ IP НЕ меняется!      │
└──────────────────────┘        └──────────────────────┘
         ▲                               ▲
         │          10-30 сек            │
         └───── пауза между оргами ──────┘
```

CSRF-токен привязан к IP сессии. Смена IP внутри пагинации = ошибка.
Поэтому прокси задаётся на уровне BrowserContext, а не отдельного запроса.

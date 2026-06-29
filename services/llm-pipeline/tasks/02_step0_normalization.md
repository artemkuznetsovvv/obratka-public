# Задача 02: Шаг 0 — Нормализация текста

## Цель

Привести сырые отзывы с площадок к стандартному виду перед LLM-обработкой. Шаг алгоритмический, без LLM. Стоимость = $0.

> v2 / tasks/12: batch-функция принимает `collector: ArtifactCollector | None`
> и при включённом отчёте пишет `Step0Artifact`. БД-кэш в этой итерации не
> подключается.

## Файлы

- `src/obratka/steps/step0_normalize.py`
- `src/obratka/utils/lang.py` — обёртка над langdetect

## Что делает

1. Удаление HTML-тегов, URL, телеграм/whatsapp-ссылок.
2. Чистка лишних пробелов, переводов строк, спецсимволов.
3. Нормализация регистра — всё в нижний регистр.
4. Исправление очевидного транслита (опционально, эвристика по общим словам).
5. Определение языка через `langdetect`.
6. Сохранение исходного текста рядом с нормализованным.

## Pydantic-схемы

```python
class RawReview(BaseModel):
    review_id: str
    author_id: str | None = None
    text: str
    stars: int | None = None
    date: datetime
    source: str               # "yandex" | "2gis" | "google"

class NormalizedReview(BaseModel):
    review_id: str
    author_id: str | None
    text_raw: str             # оригинал
    text_normalized: str      # очищенный
    text_hash: str            # sha256 от text_normalized — для кэша
    lang: str                 # "ru" | "en" | "kk" | ...
    lang_confidence: float
    stars: int | None
    date: datetime
    source: str
```

## Примеры

**Вход:**
```
<b>Otlichnoe 4 mesto!3</b> prihodite vse 👍 http://t.me/promo123
```
**Выход:**
```
otlichnoe 4 mesto!3 prihodite vse
```
(текст транслитом — это норма, переведём на шаге 0.5 если `lang != "ru"`)

**Вход:**
```
Очень    плохое    
обслуживание!!!  Подробности: https://2gis.ru/firm/12345
```
**Выход:**
```
очень плохое обслуживание!!! подробности:
```

## Регулярки (стартовый набор)

```python
HTML_TAG = re.compile(r"<[^>]+>")
URL = re.compile(r"https?://\S+|www\.\S+|t\.me/\S+")
WHITESPACE = re.compile(r"\s+")
EMOJI = re.compile(
    "[\U0001F600-\U0001F64F\U0001F300-\U0001F5FF\U0001F680-\U0001F6FF"
    "\U0001F1E0-\U0001F1FF\U00002600-\U000026FF\U00002700-\U000027BF]+",
    flags=re.UNICODE,
)
```

Эмоджи — **удаляем** (LLM их не читает полезно для тематики, токены тратятся зря).

## API

```python
def normalize_review(raw: RawReview) -> NormalizedReview: ...

def normalize_batch(raws: list[RawReview]) -> list[NormalizedReview]: ...
```

## Логирование

- На каждые 1000 обработанных отзывов — INFO с прогрессом.
- При обнаружении не-RU языка — DEBUG с `review_id`, `lang`, `lang_confidence`.
- Если `len(text_normalized) < 5` — WARNING (отзыв стал пустым после чистки), такие пропускаем дальше с пометкой `is_empty=True`.

## Кэш-хэш

`text_hash = sha256(text_normalized.encode("utf-8")).hexdigest()` — используется в БД как ключ кэша. См. `tasks/10_database_schema.md`.

## Критерии готовности

- [ ] Юнит-тесты на 10+ кейсов: HTML, ссылки, эмоджи, multi-line, пустой текст, чистый текст.
- [ ] Языковая детекция работает на русском, английском, казахском, узбекском, украинском.
- [ ] При `langdetect.LangDetectException` — fallback на `lang="unknown"`, `lang_confidence=0.0`.
- [ ] Скорость: 10К отзывов обрабатываются <2 секунд на ноутбучном CPU.

## Подсказки

- `langdetect` детерминистичен только при `langdetect.DetectorFactory.seed = 0` — установить в модуле один раз.
- Для очень коротких текстов (<10 символов) langdetect ненадёжен — для них ставим `lang="ru"` по умолчанию + DEBUG лог.

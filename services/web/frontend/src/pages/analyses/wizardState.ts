// Transit state for the new-analysis wizard.
//
// Двухслойная персистентность:
//  1. sessionStorage (быстрый кэш в пределах сессии) — основной путь чтения/записи
//     во время прохождения мастера. Очищается при закрытии вкладки.
//  2. Company.draftPeriodFrom/To/Sources (БД) — переживает закрытие вкладки/девайс,
//     заполняется на step 1 submit. Если пользак вернётся через неделю, поднимаем
//     отсюда. Источник истины для cross-session continuity.
//
// Чтение: effectiveWizardState(companyId, company) сначала смотрит sessionStorage,
// fallback'ом разворачивает Company.draft*. Запись: на step 1 submit пишем И в
// sessionStorage, И в Company (через CreateCompanyRequest).

export type AnalysisPeriod =
  | { kind: 'since-beginning' }
  | { kind: 'range'; from: string; to: string } // ISO yyyy-mm-dd

export interface WizardState {
  period: AnalysisPeriod
  sources: string[] // subset of ['2gis', 'yandex', 'google']
  // Optional — set after step 2 so step 3 (and back-nav to step 2) can restore picks.
  selectedBranchIds?: string[]
}

const KEY_PREFIX = 'obratka:analysis-wizard:'

export const DEFAULT_SOURCES = ['2gis', 'yandex', 'google']

export const defaultWizardState = (): WizardState => ({
  period: { kind: 'since-beginning' },
  sources: [...DEFAULT_SOURCES],
})

export function loadWizardState(companyId: string): WizardState | null {
  try {
    const raw = sessionStorage.getItem(KEY_PREFIX + companyId)
    if (!raw) return null
    const parsed = JSON.parse(raw) as WizardState
    if (!parsed || !parsed.period || !Array.isArray(parsed.sources)) return null
    return parsed
  } catch {
    return null
  }
}

export function saveWizardState(companyId: string, state: WizardState): void {
  try {
    sessionStorage.setItem(KEY_PREFIX + companyId, JSON.stringify(state))
  } catch {
    // sessionStorage quota / private mode — silently ignore, wizard still works in-memory.
  }
}

export function clearWizardState(companyId: string): void {
  try {
    sessionStorage.removeItem(KEY_PREFIX + companyId)
  } catch {
    // ignore
  }
}

// Чтение «эффективного» состояния: sessionStorage > Company.draft* > defaults.
// `company` опционально — если фронт ещё не загрузил CompanyDto, поведение как раньше.
interface CompanyDraftFields {
  draftPeriodFrom: string | null
  draftPeriodTo: string | null
  draftSources: string[] | null
  selectedBranchIds?: string[]
}

export function effectiveWizardState(
  companyId: string | null,
  company: CompanyDraftFields | null,
): WizardState {
  if (companyId) {
    const session = loadWizardState(companyId)
    if (session) return session
  }
  if (company) {
    const period: AnalysisPeriod =
      company.draftPeriodFrom && company.draftPeriodTo
        ? {
            kind: 'range',
            // backend хранит DateTimeOffset (ISO с time/tz). PeriodPicker и сам формат
            // wizardState ожидают yyyy-mm-dd — берём первые 10 символов.
            from: company.draftPeriodFrom.slice(0, 10),
            to: company.draftPeriodTo.slice(0, 10),
          }
        : { kind: 'since-beginning' }
    const sources =
      company.draftSources && company.draftSources.length > 0
        ? company.draftSources
        : [...DEFAULT_SOURCES]
    return { period, sources }
  }
  return defaultWizardState()
}

// Конвертация period → ISO для отправки в CreateCompanyRequest. «С самого начала» = null/null.
export function periodToDraftPayload(period: AnalysisPeriod): {
  draftPeriodFrom: string | null
  draftPeriodTo: string | null
} {
  if (period.kind !== 'range' || !period.from || !period.to) {
    return { draftPeriodFrom: null, draftPeriodTo: null }
  }
  return { draftPeriodFrom: period.from, draftPeriodTo: period.to }
}

export function formatPeriodSummary(period: AnalysisPeriod): string {
  if (period.kind === 'since-beginning') return 'С самого начала'
  return `${formatDate(period.from)} — ${formatDate(period.to)}`
}

function formatDate(iso: string): string {
  // iso = yyyy-mm-dd; render as dd.mm.yyyy without timezone shifts.
  const [y, m, d] = iso.split('-')
  if (!y || !m || !d) return iso
  return `${d}.${m}.${y}`
}

const SOURCE_LABELS: Record<string, string> = {
  '2gis': '2ГИС',
  yandex: 'Яндекс.Карты',
  google: 'Google Maps',
}

export function formatSourcesSummary(sources: string[]): string {
  return sources.map((s) => SOURCE_LABELS[s] ?? s).join(', ')
}

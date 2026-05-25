// Transit-only state for the new-analysis wizard. NOT a domain entity.
// Lives in sessionStorage keyed by companyId so user keeps period/sources
// across F5 and across step transitions, but tab close wipes it. Real
// persistence of these parameters happens on AnalysisJob in PG at launch.

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

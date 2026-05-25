import type {
  BranchSearchResponse,
  BranchSearchResultItem,
  SaveBranchGroupsRequest,
} from '@/api/companies'

// ---- Domain types (client-side, NOT what the API uses verbatim) ----

export type GroupKey = string // «g-1» из бэка или «custom-<uuid>» от ручного создания

export interface ClientProvider {
  branchId: string
  isEnabled: boolean
}

export interface ClientGroup {
  key: GroupKey
  name: string
  address: string
  city: string
  isSelected: boolean
  providers: ClientProvider[]
}

// Per-city editable layout. Меняется в реальном времени по мере того как
// юзер тыкает чекбоксы / привязывает unmatched / создаёт группы.
export interface CityLayout {
  // Карточки этого города (плоский справочник для рендера: name/address/rating/...)
  cardsById: Record<string, BranchSearchResultItem>
  groups: ClientGroup[]
  // branchId-ы карточек, не привязанные ни к какой группе и не помеченные «Игнорировать».
  unmatchedBranchIds: string[]
  // branchId-ы карточек, явно помеченных «Игнорировать» в unmatched-секции.
  ignoredBranchIds: string[]
}

export type CityState =
  | { status: 'pending' }
  | { status: 'searching' }
  | { status: 'done'; layout: CityLayout }
  | { status: 'error'; message: string }

export type CitiesState = Record<string, CityState>

// ---- Initialization from API response ----

export function layoutFromSearchResponse(response: BranchSearchResponse): CityLayout {
  const cardsById: Record<string, BranchSearchResultItem> = {}
  for (const sg of response.sources) for (const it of sg.items) cardsById[it.id] = it
  // На случай если provider вне response.sources (не должно быть, но защитимся):
  for (const lg of response.logicalGroups) for (const p of lg.providers) cardsById[p.id] = p
  for (const u of response.unmatched) cardsById[u.id] = u

  const groups: ClientGroup[] = response.logicalGroups.map((lg) => ({
    key: lg.groupKey,
    name: lg.canonicalName,
    address: lg.canonicalAddress,
    city: lg.city,
    isSelected: true, // ТЗ: по умолчанию main checkbox включён
    providers: lg.providers.map((p) => ({ branchId: p.id, isEnabled: true })),
  }))

  return {
    cardsById,
    groups,
    unmatchedBranchIds: response.unmatched.map((u) => u.id),
    ignoredBranchIds: [],
  }
}

// ---- Edit operations (pure, return new layout) ----

let customGroupCounter = 0
function nextCustomGroupKey(): GroupKey {
  customGroupCounter++
  return `custom-${customGroupCounter}-${Date.now()}`
}

export function setGroupSelected(layout: CityLayout, key: GroupKey, isSelected: boolean): CityLayout {
  return {
    ...layout,
    groups: layout.groups.map((g) => (g.key === key ? { ...g, isSelected } : g)),
  }
}

export function setProviderEnabled(
  layout: CityLayout,
  key: GroupKey,
  branchId: string,
  isEnabled: boolean,
): CityLayout {
  return {
    ...layout,
    groups: layout.groups.map((g) =>
      g.key === key
        ? {
            ...g,
            providers: g.providers.map((p) =>
              p.branchId === branchId ? { ...p, isEnabled } : p,
            ),
          }
        : g,
    ),
  }
}

// «Разгруппировать»: возвращает все карточки группы в unmatched и удаляет группу.
export function ungroup(layout: CityLayout, key: GroupKey): CityLayout {
  const group = layout.groups.find((g) => g.key === key)
  if (!group) return layout
  return {
    ...layout,
    groups: layout.groups.filter((g) => g.key !== key),
    unmatchedBranchIds: [...layout.unmatchedBranchIds, ...group.providers.map((p) => p.branchId)],
  }
}

// Из unmatched → в существующую группу как новый provider (isEnabled=true по умолчанию).
export function attachToGroup(layout: CityLayout, branchId: string, key: GroupKey): CityLayout {
  if (!layout.unmatchedBranchIds.includes(branchId)) return layout
  const target = layout.groups.find((g) => g.key === key)
  if (!target) return layout
  // Не добавляем дубль если карточка уже там (не должно случиться, но защитимся).
  if (target.providers.some((p) => p.branchId === branchId)) return layout
  return {
    ...layout,
    unmatchedBranchIds: layout.unmatchedBranchIds.filter((id) => id !== branchId),
    groups: layout.groups.map((g) =>
      g.key === key
        ? { ...g, providers: [...g.providers, { branchId, isEnabled: true }] }
        : g,
    ),
  }
}

// Из unmatched → создать новую группу из этой одной карточки.
export function createGroupFromUnmatched(layout: CityLayout, branchId: string): CityLayout {
  if (!layout.unmatchedBranchIds.includes(branchId)) return layout
  const card = layout.cardsById[branchId]
  if (!card) return layout
  const newGroup: ClientGroup = {
    key: nextCustomGroupKey(),
    name: card.name,
    address: card.address ?? '',
    city: '', // заполнится при сборке save-payload
    isSelected: true,
    providers: [{ branchId, isEnabled: true }],
  }
  return {
    ...layout,
    unmatchedBranchIds: layout.unmatchedBranchIds.filter((id) => id !== branchId),
    groups: [...layout.groups, newGroup],
  }
}

// Из unmatched → ignored.
export function ignoreUnmatched(layout: CityLayout, branchId: string): CityLayout {
  if (!layout.unmatchedBranchIds.includes(branchId)) return layout
  return {
    ...layout,
    unmatchedBranchIds: layout.unmatchedBranchIds.filter((id) => id !== branchId),
    ignoredBranchIds: [...layout.ignoredBranchIds, branchId],
  }
}

// Обратное: из ignored → обратно в unmatched (юзер передумал).
export function unignore(layout: CityLayout, branchId: string): CityLayout {
  if (!layout.ignoredBranchIds.includes(branchId)) return layout
  return {
    ...layout,
    ignoredBranchIds: layout.ignoredBranchIds.filter((id) => id !== branchId),
    unmatchedBranchIds: [...layout.unmatchedBranchIds, branchId],
  }
}

// ---- Aggregation: build save-payload across all cities ----

export function buildSavePayload(
  cityStates: CitiesState,
  cityOrder: string[],
): SaveBranchGroupsRequest {
  const groups: SaveBranchGroupsRequest['groups'] = []
  const ignoredBranchIds: string[] = []

  for (const city of cityOrder) {
    const state = cityStates[city]
    if (state?.status !== 'done') continue
    for (const g of state.layout.groups) {
      groups.push({
        name: g.name,
        address: g.address,
        // Если группа создана из unmatched, city у неё пустой — подставляем city текущей секции.
        city: g.city || city,
        isSelected: g.isSelected,
        providers: g.providers.map((p) => ({
          branchId: p.branchId,
          isEnabled: p.isEnabled,
        })),
      })
    }
    ignoredBranchIds.push(...state.layout.ignoredBranchIds)
  }

  return { groups, ignoredBranchIds }
}

// «Сколько провайдеров будет реально учитывать анализ» — для CTA-gate.
export function countActiveProviders(cityStates: CitiesState): number {
  let count = 0
  for (const city in cityStates) {
    const state = cityStates[city]
    if (state?.status !== 'done') continue
    for (const g of state.layout.groups) {
      if (!g.isSelected) continue
      for (const p of g.providers) if (p.isEnabled) count++
    }
  }
  return count
}

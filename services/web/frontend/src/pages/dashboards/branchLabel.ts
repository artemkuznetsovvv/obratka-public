// Достаём компактную «локацию» из адреса для подписи (таб, dropdown-опция).
// У сетевых брендов (Skuratov, Relax 24, Surf Coffee) у всех филиалов
// одинаковое name — различить можно только адресом.
//
//   «ТЦ KazanMall, …»        → «ТЦ KazanMall»  (якорь-ТЦ/ТРЦ/БЦ/ЖК/МФК)
//   «Улица Пушкина, 5, …»    → «Пушкина, 5»    (улица+дом, без префикса)
//   «ул. Профсоюзная, 34, …» → «Профсоюзная, 34»
//
// Если адреса нет — возвращаем fallback (обычно name) или 'Без адреса'.
export function extractBranchLabel(
  address: string | null | undefined,
  fallback: string | null | undefined,
): string {
  if (!address) return fallback ?? 'Без адреса'

  const segments = address.split(',').map((s) => s.trim()).filter(Boolean)
  if (segments.length === 0) return fallback ?? 'Без адреса'

  const first = segments[0]
  // ТЦ/ТРЦ/БЦ/ЖК/МФК — уникальный якорь, сам по себе достаточен.
  if (/^(ТЦ|ТРЦ|ТРК|БЦ|ЖК|МФК)\b/i.test(first)) {
    return first
  }

  // Иначе пробуем «улица + дом». Убираем префикс «улица/ул./проспект/пр./…».
  const streetWithoutPrefix = first.replace(
    /^(улица|ул\.?|проспект|пр\.?|пр-кт|переулок|пер\.?|шоссе|ш\.?|бульвар|б-р|проезд|тупик)\s+/i,
    '',
  )
  const houseLike = segments[1] && /^[0-9]/.test(segments[1]) ? segments[1] : null
  return houseLike ? `${streetWithoutPrefix}, ${houseLike}` : streetWithoutPrefix
}

import { MapPin } from 'lucide-react'
import { Card } from '@/components/ui/card'
import type { DashboardBranchDto } from '@/api/dashboards'
import { useDashboardFilters } from '../DashboardFiltersContext'
import { MetricCardPlaceholder } from './MetricCardPlaceholder'
import { MetricReviewCount } from '../cards/MetricReviewCount'
import { MetricAverageRating } from '../cards/MetricAverageRating'
import { MetricSentimentDistribution } from '../cards/MetricSentimentDistribution'
import { MetricFreshPulse } from '../cards/MetricFreshPulse'

// Секция одного филиала. По спеке:
//   - Базовый слой: метрики 1, 2, 3 (горизонтальный ряд)
//   - Расширенный слой: метрики 4, 5, 6, 7 (сетка)
//   - Опц. блок «Топ примеров»
//
// Сейчас все метрики — MetricCardPlaceholder. При реализации каждой метрики
// заменяется на соответствующий компонент.
export function BranchSection({ branch }: { branch: DashboardBranchDto }) {
  const filters = useDashboardFilters()
  // Если филиал явно снят из фильтра «филиал» — показываем пустое состояние.
  // Layout (видимость табов) при этом остаётся (см. решение по итерации 2).
  const isFilteredOut = !filters.branches.includes(branch.branchId)

  if (isFilteredOut) {
    return (
      <Card className="p-8 text-center bg-page-bg/30 border-dashed">
        <div className="text-sm text-text-secondary mb-1">
          Этот филиал исключён фильтром.
        </div>
        <div className="text-xs text-text-tertiary">
          Чтобы увидеть метрики — добавьте его обратно в фильтр «Филиал».
        </div>
      </Card>
    )
  }

  return (
    <div>
      <div className="mb-4">
        <h2 className="text-h2 text-text-primary">
          {branch.name ?? <span className="italic text-text-tertiary">Филиал удалён</span>}
        </h2>
        {branch.address && (
          <div className="text-xs text-text-tertiary mt-1 flex items-center gap-1">
            <MapPin size={11} />
            {branch.address}
          </div>
        )}
      </div>

      {/* Базовый слой: 1, 2, 3 */}
      <div className="grid grid-cols-1 sm:grid-cols-3 gap-3 mb-4">
        <MetricReviewCount branchId={branch.branchId} />
        <MetricAverageRating branchId={branch.branchId} />
        <MetricSentimentDistribution branchId={branch.branchId} />
      </div>

      {/* Расширенный слой: 4, 5, 6, 7 */}
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-3 mb-4">
        <MetricFreshPulse branchId={branch.branchId} />
        <MetricCardPlaceholder slug="М5" title="О чём говорят чаще всего" minHeight="14rem" />
        <MetricCardPlaceholder slug="М6" title="Сколько клиентов рекомендуют" minHeight="14rem" />
        <MetricCardPlaceholder slug="М7" title="Новые отзывы за период" hint="свой переключатель" minHeight="14rem" />
      </div>

      {/* Опц. блок «Топ примеров» (ТЗ 4.3) — пока placeholder, MVP включит позже */}
      <Card className="p-4 border-dashed border-border-subtle bg-page-bg/30">
        <div className="text-sm font-semibold text-text-primary mb-1">Топ примеров</div>
        <div className="text-xs text-text-tertiary">
          3–5 положительных и 3–5 отрицательных отзывов за выбранный период (опционально).
        </div>
      </Card>
    </div>
  )
}

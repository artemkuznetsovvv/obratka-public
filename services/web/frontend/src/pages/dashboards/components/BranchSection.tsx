import { MapPin } from 'lucide-react'
import { Card } from '@/components/ui/card'
import type { DashboardBranchDto } from '@/api/dashboards'
import { useDashboardFilters } from '../DashboardFiltersContext'
import { MetricReviewCount } from '../cards/MetricReviewCount'
import { MetricAverageRating } from '../cards/MetricAverageRating'
import { MetricSentimentDistribution } from '../cards/MetricSentimentDistribution'
import { MetricFreshPulse } from '../cards/MetricFreshPulse'
import { MetricTopTopics } from '../cards/MetricTopTopics'
import { MetricRecommendPercent } from '../cards/MetricRecommendPercent'
import { MetricRecentReviews } from '../cards/MetricRecentReviews'
import { MetricTopExamples } from '../cards/MetricTopExamples'

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

      {/* Расширенный слой: 4, 5, 6, 7. Сетка 2×2 — карточки шире, в М5
          вмещаются полные названия тем без обрезания. */}
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-3 mb-4">
        <MetricFreshPulse branchId={branch.branchId} />
        <MetricTopTopics branchId={branch.branchId} />
        <MetricRecommendPercent branchId={branch.branchId} />
        <MetricRecentReviews branchId={branch.branchId} />
      </div>

      {/* Блок «Топ примеров» (ТЗ 4.3): до 5 позитивных + до 5 негативных отзывов. */}
      <MetricTopExamples branchId={branch.branchId} />
    </div>
  )
}

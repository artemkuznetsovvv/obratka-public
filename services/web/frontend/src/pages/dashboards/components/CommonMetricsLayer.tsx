import { Building2 } from 'lucide-react'
import { MetricNetworkTotalReviews } from '../cards/MetricNetworkTotalReviews'
import { MetricNetworkAverageRating } from '../cards/MetricNetworkAverageRating'
import { MetricNetworkSentimentDistribution } from '../cards/MetricNetworkSentimentDistribution'

// Слой общих метрик по сети — отображается только когда в джобе 3+ филиалов.
// Содержит О1/О2/О3 в горизонтальном ряду.
export function CommonMetricsLayer() {
  return (
    <div className="mb-6">
      <div className="flex items-center gap-2 mb-3 text-h3 text-text-primary">
        <Building2 size={16} className="text-text-tertiary" />
        По сети
      </div>
      <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
        <MetricNetworkTotalReviews />
        <MetricNetworkAverageRating />
        <MetricNetworkSentimentDistribution />
      </div>
    </div>
  )
}

import { http } from './http'
import { parseContentDispositionFilename } from './download'

// PDF-отчёт по результатам анализа. Параметры зеркалят фильтры дашборда
// (как у metrics.ts). branchIds — выбранные на дашборде филиалы.
export interface ReportQuery {
  branchIds: string[]
  from: string | null
  to: string | null
  sources: string[]
  sentiments: string[]
  stars: number[]
}

export const reportsApi = {
  // Возвращает blob PDF + предложенное имя файла (из Content-Disposition).
  // Бэк сам трактует «все опции = не фильтровать», поэтому массивы шлём как есть.
  download: async (jobId: string, q: ReportQuery) => {
    const response = await http.get<Blob>(`/api/analyses/${jobId}/report.pdf`, {
      responseType: 'blob',
      params: {
        branchIds: q.branchIds.join(','),
        from: q.from ?? undefined,
        to: q.to ?? undefined,
        sources: q.sources.length > 0 ? q.sources.join(',') : undefined,
        sentiments: q.sentiments.length > 0 ? q.sentiments.join(',') : undefined,
        stars: q.stars.length > 0 ? q.stars.join(',') : undefined,
      },
    })
    const disposition = response.headers['content-disposition'] as string | undefined
    const fileName = parseContentDispositionFilename(disposition) ?? `Обратка_${jobId}.pdf`
    return { blob: response.data, fileName }
  },
}

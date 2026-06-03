namespace Obratka.Modules.Reports;

public interface IReportsModule
{
    // Синхронный рендер: QuestPDF.GeneratePdf() — CPU-bound, без I/O.
    // На вход — уже собранная модель (числа посчитаны в Web API из метрик-сервисов).
    byte[] Render(ReportDocumentModel model);
}

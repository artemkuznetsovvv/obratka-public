using QuestPDF.Fluent;

namespace Obratka.Modules.Reports;

internal sealed class ReportsModule : IReportsModule
{
    public byte[] Render(ReportDocumentModel model)
    {
        ReportPdfBootstrap.EnsureInitialized();
        return new ReportPdfDocument(model).GeneratePdf();
    }
}

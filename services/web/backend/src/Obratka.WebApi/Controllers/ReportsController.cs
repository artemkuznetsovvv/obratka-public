using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Obratka.Modules.Notifications;
using Obratka.Modules.Reports;
using Obratka.WebApi.Auth;
using Obratka.WebApi.Data;
using Obratka.WebApi.Integration.ProcessingGateway;
using Obratka.WebApi.Reports;

namespace Obratka.WebApi.Controllers;

[ApiController]
[Authorize]
[Route("api/analyses")]
public sealed class ReportsController(
    WebApiDbContext db,
    IProcessingGatewayClient gateway,
    UserManager<ApplicationUser> userManager,
    IReportsModule reports,
    ReportDataAssembler assembler,
    INotificationsModule notifications,
    ILogger<ReportsController> logger) : ControllerBase
{
    // PDF-отчёт по результатам анализа, по текущим параметрам и фильтрам дашборда.
    // Filters → query (как у метрик): branchIds (обяз., выбранные на дашборде), from/to,
    // sources, sentiments, stars. PDF стримится напрямую (без S3 — см. план §ADR-deviations).
    [HttpGet("{jobId:guid}/report.pdf")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Report(
        Guid jobId,
        [FromQuery] string? branchIds,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? sources,
        [FromQuery] string? sentiments,
        [FromQuery] string? stars,
        CancellationToken ct)
    {
        if (!assembler.IsAvailable)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Analytics не сконфигурирован",
                detail: "ConnectionStrings:ProcessingReadDb пустой — Analytics-модуль не подключён к processing_db, отчёт собрать нельзя.");
        }

        var branchList = ParseGuids(branchIds);
        if (branchList is null || branchList.Count == 0)
            return BadRequest(new { error = "Не выбрано ни одного филиала" });

        var ownerId = GetUserIdOrNull();
        if (ownerId is null) return Unauthorized();

        var job = await gateway.GetAnalysisAsync(jobId, ct);
        if (job is null) return NotFound();

        var company = await db.Companies.AsNoTracking()
            .Where(c => c.Id == job.CompanyId && c.OwnerUserId == ownerId)
            .Select(c => new { c.Name })
            .SingleOrDefaultAsync(ct);
        if (company is null) return NotFound();

        byte[] bytes;
        try
        {
            var model = await assembler.BuildAsync(
                job,
                company.Name,
                branchList,
                from,
                to,
                ParseCsv(sources),
                ParseCsv(sentiments),
                ParseStars(stars),
                ct);

            bytes = reports.Render(model);
        }
        catch (Exception ex)
        {
            var eventId = Guid.NewGuid().ToString("N");
            logger.LogError(ex, "PDF report generation failed for job {JobId} (event {EventId})", jobId, eventId);
            try
            {
                await notifications.SendAdminAlertAsync(new AdminAlert(
                    Stage: "Отчёт",
                    Reason: $"Сбой генерации PDF: {ex.Message}",
                    Severity: "critical",
                    EventId: eventId,
                    UserId: ownerId.Value,
                    CompanyId: job.CompanyId,
                    CompanyName: company.Name,
                    JobId: jobId), ct);
            }
            catch (Exception alertEx)
            {
                logger.LogWarning(alertEx, "Admin alert (report) send failed for job {JobId}", jobId);
            }
            return Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Не удалось сформировать отчёт",
                detail: "Произошла ошибка при генерации PDF. Администратор уведомлён, попробуйте позже.");
        }

        var fileName = $"Обратка_{SanitizeFileName(company.Name)}_{DateTimeOffset.UtcNow:yyyy-MM-dd}.pdf";
        return File(bytes, "application/pdf", fileName);
    }

    // Убираем символы, недопустимые в имени файла (Content-Disposition спокойно несёт кириллицу).
    private static string SanitizeFileName(string name)
    {
        var cleaned = new string(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray())
            .Trim();
        return string.IsNullOrEmpty(cleaned) ? "report" : cleaned;
    }

    private Guid? GetUserIdOrNull()
    {
        var id = userManager.GetUserId(User);
        return Guid.TryParse(id, out var g) ? g : null;
    }

    private static IReadOnlyList<Guid>? ParseGuids(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var items = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<Guid>(items.Length);
        foreach (var s in items)
            if (Guid.TryParse(s, out var g)) list.Add(g);
        return list.Count == 0 ? null : list;
    }

    private static IReadOnlyCollection<string>? ParseCsv(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var items = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return items.Length == 0 ? null : items;
    }

    private static IReadOnlyCollection<short>? ParseStars(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var items = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<short>(items.Length);
        foreach (var s in items)
            if (short.TryParse(s, out var n) && n is >= 1 and <= 5) list.Add(n);
        return list.Count == 0 ? null : list;
    }
}

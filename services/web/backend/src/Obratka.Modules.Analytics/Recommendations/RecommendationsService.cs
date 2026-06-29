using Microsoft.EntityFrameworkCore;
using Obratka.Modules.Analytics.Data;
using Obratka.Modules.Analytics.Data.Entities;

namespace Obratka.Modules.Analytics.Recommendations;

public interface IRecommendationsService
{
    Task<IReadOnlyList<AnalysisRecommendation>> ListByJobAsync(Guid jobId, CancellationToken ct);
}

internal sealed class RecommendationsService(ProcessingReadContext db) : IRecommendationsService
{
    public async Task<IReadOnlyList<AnalysisRecommendation>> ListByJobAsync(
        Guid jobId, CancellationToken ct)
    {
        return await db.AnalysisRecommendations
            .AsNoTracking()
            .Where(r => r.AnalysisJobId == jobId)
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Id)
            .ToListAsync(ct);
    }
}

using System.Data.Common;

namespace ProcessingGateway.Infrastructure.Database;

/// Фабрика DB-соединений для путей, использующих Dapper (bulk INSERT в `reviews` и
/// `review_llm_results` — Этапы 3 и 6). EF Core владеет схемой и single-row CRUD.
public interface IDbConnectionFactory
{
    /// Возвращает уже **открытое** соединение. Caller обязан Dispose-ить.
    Task<DbConnection> OpenAsync(CancellationToken ct = default);
}

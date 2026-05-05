using System.Data.Common;
using Npgsql;

namespace ProcessingGateway.Infrastructure.Database;

public sealed class NpgsqlConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public NpgsqlConnectionFactory(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("ProcessingDb")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:ProcessingDb is not configured");
    }

    public async Task<DbConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ProcessingGateway.Infrastructure.Database;

/// Используется только тулзой `dotnet ef migrations add/update` в дизайн-тайме.
/// В рантайме DbContext создаётся через DI из Program.cs.
/// Реальная БД здесь не нужна — миграции — это DDL-файлы.
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ProcessingDbContext>
{
    public ProcessingDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ProcessingDbContext>()
            .UseNpgsql("Host=localhost;Database=processing_design;Username=design;Password=design")
            .UseSnakeCaseNamingConvention()
            .Options;

        return new ProcessingDbContext(options);
    }
}

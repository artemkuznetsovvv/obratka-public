using Microsoft.EntityFrameworkCore;
using ParserService.Core.Models;

namespace ParserService.Core;

public class ParserDbContext : DbContext
{
    public DbSet<CollectionTask> CollectionTasks => Set<CollectionTask>();

    public ParserDbContext(DbContextOptions<ParserDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CollectionTask>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Source).HasConversion<string>();
            e.Property(t => t.Status).HasConversion<string>();
            e.Property(t => t.BranchesJson).HasColumnType("TEXT");
            e.HasIndex(t => t.JobId);
            e.HasIndex(t => t.Status);
        });
    }
}

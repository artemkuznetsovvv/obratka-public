namespace ParserService.Core.Models;

public class CollectionTask
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public Guid CompanyId { get; set; }
    public SourceType Source { get; set; }
    public CollectionTaskStatus Status { get; set; }
    public double Progress { get; set; }
    public int? ReviewCount { get; set; }
    public string? S3Url { get; set; }
    public string? Error { get; set; }
    public string BranchesJson { get; set; } = "[]";
    public DateTimeOffset? DateFrom { get; set; }
    public DateTimeOffset? DateTo { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

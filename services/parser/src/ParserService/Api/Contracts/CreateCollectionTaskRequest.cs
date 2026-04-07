namespace ParserService.Api.Contracts;

public record CreateCollectionTaskRequest(
    Guid JobId,
    Guid CompanyId,
    string Source,
    DateTimeOffset? DateFrom,
    DateTimeOffset? DateTo,
    List<BranchTargetDto> Branches
);

public record BranchTargetDto(Guid BranchId, string ExternalId, string ExternalUrl);

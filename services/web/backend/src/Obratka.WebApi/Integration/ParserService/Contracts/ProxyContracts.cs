namespace Obratka.WebApi.Integration.ParserService.Contracts;

public sealed record ParserProxyDto(
    int Id,
    string Host,
    int Port,
    string Protocol,
    string? Username,
    bool Enabled,
    int FailureCount,
    DateTimeOffset? CooldownUntil,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? ExpiresAt,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ParserProxyListResponse(int Total, IReadOnlyList<ParserProxyDto> Items);

public sealed record CreateParserProxyRequest(
    string Host,
    int Port,
    string Protocol,
    string? Username,
    string? Password,
    string? Notes,
    bool? Enabled,
    DateTimeOffset? ExpiresAt);

public sealed record ParserProxyIdRequest(int Id);

public sealed record SetParserProxyExpiresAtRequest(DateTimeOffset? ExpiresAt);

internal sealed record SetParserProxyExpiresAtPayload(int Id, DateTimeOffset? ExpiresAt);

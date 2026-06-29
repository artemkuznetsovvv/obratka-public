namespace ParserService.Api.Contracts;

public record CreateProxyRequest(
    string Host,
    int Port,
    string Protocol,
    string? Username,
    string? Password,
    string? Notes,
    bool? Enabled,
    DateTimeOffset? ExpiresAt);

public record ProxyIdRequest(int Id);

public record SetProxyExpiresAtRequest(int Id, DateTimeOffset? ExpiresAt);

public record ProxyDto(
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

public record ProxyListResponse(int Total, IReadOnlyList<ProxyDto> Items);

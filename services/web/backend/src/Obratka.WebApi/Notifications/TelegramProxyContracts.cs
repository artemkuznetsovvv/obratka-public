namespace Obratka.WebApi.Notifications;

// DTO-контракты админ-CRUD прокси Telegram (camelCase на проводе — Web API default).
// Зеркало Integration/ParserService/Contracts/ProxyContracts.cs. Password НИКОГДА не возвращаем.
public sealed record TelegramProxyDto(
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

public sealed record TelegramProxyListResponse(int Total, IReadOnlyList<TelegramProxyDto> Items);

public sealed record CreateTelegramProxyRequest(
    string Host,
    int Port,
    string Protocol,
    string? Username,
    string? Password,
    string? Notes,
    bool? Enabled,
    DateTimeOffset? ExpiresAt);

public sealed record SetTelegramProxyExpiresAtRequest(DateTimeOffset? ExpiresAt);

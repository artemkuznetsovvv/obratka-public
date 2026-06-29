namespace ProcessingGateway.Application.Ingestion;

/// Детерминированный ключ дедупликации для `reviews.composite_key` (UNIQUE).
/// Решение Этапа 0 №8 — простая схема, без хешей.
///
/// Фавор пути с external_id: source даёт стабильный id → ключ короткий и читаемый.
/// Fallback (когда external_id отсутствует): дата + первые 200 символов trim-нутого текста.
/// Длина гарантированно ≤ 1000 байт (VARCHAR(1000) в reviews.composite_key).
public static class CompositeKeyBuilder
{
    private const int FallbackTextPrefixLength = 200;
    public const int MaxLength = 1000;

    public static string Build(string source, Guid branchId, string? externalId, DateTimeOffset reviewDate, string text)
    {
        if (string.IsNullOrEmpty(source))
            throw new ArgumentException("source cannot be empty", nameof(source));

        if (!string.IsNullOrEmpty(externalId))
            return $"{source}:{branchId:N}:ext:{externalId}";

        var unix = reviewDate.ToUnixTimeSeconds();
        var trimmed = (text ?? string.Empty).Trim();
        var prefix = trimmed.Length > FallbackTextPrefixLength
            ? trimmed[..FallbackTextPrefixLength]
            : trimmed;

        return $"{source}:{branchId:N}:dt:{unix}:{prefix}";
    }
}

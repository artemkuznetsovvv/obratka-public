namespace Obratka.WebApi.Auth;

public interface IRefreshTokenStore
{
    Task<RefreshToken> IssueAsync(Guid userId, CancellationToken ct);
    Task<RefreshToken?> FindActiveAsync(string token, CancellationToken ct);
    Task RevokeAsync(string token, CancellationToken ct);
    Task RevokeAllForUserAsync(Guid userId, CancellationToken ct);
}

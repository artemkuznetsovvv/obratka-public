using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Obratka.WebApi.Data;

namespace Obratka.WebApi.Auth;

internal sealed class RefreshTokenStore(
    WebApiDbContext db,
    IJwtTokenService jwtTokenService,
    IOptions<JwtOptions> jwtOptions) : IRefreshTokenStore
{
    private readonly JwtOptions _options = jwtOptions.Value;

    public async Task<RefreshToken> IssueAsync(Guid userId, CancellationToken ct)
    {
        var entity = new RefreshToken
        {
            UserId = userId,
            Token = jwtTokenService.GenerateRefreshTokenValue(),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(_options.RefreshTokenExpiryDays),
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.RefreshTokens.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity;
    }

    public Task<RefreshToken?> FindActiveAsync(string token, CancellationToken ct) =>
        db.RefreshTokens
            .Where(x => x.Token == token && x.RevokedAt == null && x.ExpiresAt > DateTimeOffset.UtcNow)
            .FirstOrDefaultAsync(ct);

    public async Task RevokeAsync(string token, CancellationToken ct)
    {
        var entity = await db.RefreshTokens.FirstOrDefaultAsync(x => x.Token == token, ct);
        if (entity is null || entity.RevokedAt is not null) return;
        entity.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await db.RefreshTokens
            .Where(x => x.UserId == userId && x.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
    }
}

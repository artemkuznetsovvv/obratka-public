using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Obratka.WebApi.Data;

namespace Obratka.WebApi.Auth;

public sealed class DbInitializer(
    WebApiDbContext db,
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole<Guid>> roleManager,
    IConfiguration configuration,
    ILogger<DbInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken ct)
    {
        await db.Database.MigrateAsync(ct);

        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new IdentityRole<Guid>(role));
                logger.LogInformation("Role created: {Role}", role);
            }
        }

        var email = configuration["Seed:Admin:Email"];
        var password = configuration["Seed:Admin:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("Seed:Admin not configured; skipping admin seed.");
            return;
        }

        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not null)
        {
            if (!await userManager.IsInRoleAsync(existing, Roles.Admin))
                await userManager.AddToRoleAsync(existing, Roles.Admin);
            return;
        }

        var admin = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = configuration["Seed:Admin:FullName"] ?? "Administrator",
            CreatedAt = DateTimeOffset.UtcNow
        };

        var createResult = await userManager.CreateAsync(admin, password);
        if (!createResult.Succeeded)
        {
            var errors = string.Join("; ", createResult.Errors.Select(e => $"{e.Code}: {e.Description}"));
            throw new InvalidOperationException($"Failed to seed admin user: {errors}");
        }
        await userManager.AddToRoleAsync(admin, Roles.Admin);
        logger.LogInformation("Seeded admin user {Email}", email);
    }
}

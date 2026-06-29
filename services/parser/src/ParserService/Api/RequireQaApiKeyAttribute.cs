using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ParserService.Api;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class RequireQaApiKeyAttribute : Attribute, IAuthorizationFilter
{
    private const string HeaderName = "X-Api-Key";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var env = context.HttpContext.RequestServices.GetRequiredService<IHostEnvironment>();
        if (env.IsDevelopment()) return;

        var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var expected = config["Qa:ApiKey"];

        if (string.IsNullOrEmpty(expected))
        {
            context.Result = new ObjectResult(new { error = "QA endpoints disabled: Qa:ApiKey is not configured" })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };
            return;
        }

        if (!context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var provided)
            || provided.Count == 0
            || !FixedTimeEquals(provided.ToString(), expected))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid or missing X-Api-Key" });
        }
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        if (aBytes.Length != bBytes.Length) return false;
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}

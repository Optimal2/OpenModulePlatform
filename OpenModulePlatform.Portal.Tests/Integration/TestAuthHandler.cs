using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.Shared.Security;
using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace OpenModulePlatform.Portal.Tests.Integration;

/// <summary>
/// Test authentication handler that builds an OMP-authenticated principal from an
/// access token supplied by the SignalR client. The token must be a positive integer
/// OMP user id; the resulting identity carries the <see cref="OmpAuthDefaults.UserIdClaimType"/>
/// claim required by the Portal topbar hub.
/// </summary>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "OmpTestAuth";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userId = ResolveTestUserId();
        if (!userId.HasValue)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new(OmpAuthDefaults.UserIdClaimType, userId.Value.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, $"test-user-{userId.Value}")
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private int? ResolveTestUserId()
    {
        var accessToken = Request.Query["access_token"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(accessToken)
            && Request.Headers.TryGetValue("X-Test-User-Id", out var headerValue)
            && headerValue.Count > 0)
        {
            accessToken = headerValue[0];
        }

        if (string.IsNullOrWhiteSpace(accessToken)
            && Request.Headers.TryGetValue("Authorization", out var authHeaderValue)
            && authHeaderValue.Count > 0
            && !string.IsNullOrWhiteSpace(authHeaderValue[0]))
        {
            var authValue = authHeaderValue[0]!;
            const string bearerPrefix = "Bearer ";
            accessToken = authValue.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
                ? authValue[bearerPrefix.Length..]
                : authValue;
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        return int.TryParse(accessToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId) && userId > 0
            ? userId
            : null;
    }
}

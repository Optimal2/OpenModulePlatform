using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using System.Globalization;

namespace OpenModulePlatform.Web.Shared.Services;

/// <summary>
/// Projects the currently selected active role cookie into the authenticated user's claims.
/// </summary>
/// <remarks>
/// <para>
/// Blazor Server components primarily work with <see cref="AuthenticationState"/> and the
/// current <see cref="ClaimsPrincipal"/> rather than a fresh per-request <see cref="HttpContext"/>.
/// Adding the active role as a claim makes the chosen role available consistently in both
/// Razor Pages requests and interactive Blazor circuits.
/// </para>
/// </remarks>
public sealed class ActiveRoleClaimsTransformation : IClaimsTransformation
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ActiveRoleClaimsTransformation(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return Task.FromResult(principal);
        }

        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return Task.FromResult(principal);
        }

        var cookieValue = httpContext.Request.Cookies[ActiveRoleCookie.CookieName];
        if (string.IsNullOrWhiteSpace(cookieValue)
            || !int.TryParse(cookieValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var roleId))
        {
            return Task.FromResult(RemoveActiveRoleClaims(principal));
        }

        var normalizedRoleId = roleId.ToString(CultureInfo.InvariantCulture);
        var existingClaims = principal.FindAll(ActiveRoleCookie.ClaimType).ToArray();
        if (existingClaims.Length == 1 && string.Equals(existingClaims[0].Value, normalizedRoleId, StringComparison.Ordinal))
        {
            return Task.FromResult(principal);
        }

        var clone = principal.Clone();
        if (clone.Identity is ClaimsIdentity identity)
        {
            foreach (var claim in identity.FindAll(ActiveRoleCookie.ClaimType).ToArray())
            {
                identity.RemoveClaim(claim);
            }

            identity.AddClaim(new Claim(ActiveRoleCookie.ClaimType, normalizedRoleId));
        }

        return Task.FromResult(clone);
    }

    private static ClaimsPrincipal RemoveActiveRoleClaims(ClaimsPrincipal principal)
    {
        if (!principal.HasClaim(claim => claim.Type == ActiveRoleCookie.ClaimType))
        {
            return principal;
        }

        var clone = principal.Clone();
        if (clone.Identity is ClaimsIdentity identity)
        {
            foreach (var claim in identity.FindAll(ActiveRoleCookie.ClaimType).ToArray())
            {
                identity.RemoveClaim(claim);
            }
        }

        return clone;
    }
}

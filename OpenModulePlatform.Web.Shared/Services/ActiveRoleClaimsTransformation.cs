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

        var cookieValue = _httpContextAccessor.HttpContext?.Request.Cookies[ActiveRoleCookie.CookieName];
        if (string.IsNullOrWhiteSpace(cookieValue))
        {
            return Task.FromResult(principal);
        }

        if (!int.TryParse(cookieValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var roleId))
        {
            return Task.FromResult(principal);
        }

        var existingClaim = principal.FindFirst(ActiveRoleCookie.ClaimType);
        if (existingClaim is not null && string.Equals(existingClaim.Value, cookieValue, StringComparison.Ordinal))
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

            identity.AddClaim(new Claim(ActiveRoleCookie.ClaimType, roleId.ToString(CultureInfo.InvariantCulture)));
        }

        return Task.FromResult(clone);
    }
}

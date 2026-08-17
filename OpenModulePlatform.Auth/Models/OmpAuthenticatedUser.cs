// File: OpenModulePlatform.Auth/Models/OmpAuthenticatedUser.cs
using OpenModulePlatform.Web.Shared.Security;
using System.Security.Claims;

namespace OpenModulePlatform.Auth.Models;

public sealed class OmpAuthenticatedUser
{
    public int? UserId { get; init; }
    public int? ProviderId { get; init; }
    public string DisplayName { get; init; } = "";
    public string Provider { get; init; } = "";
    public string ProviderUserKey { get; init; } = "";
    public IReadOnlyList<(string PrincipalType, string Principal)> RolePrincipals { get; init; } = [];

    /// <summary>
    /// The account's security stamp at sign-in time (R7-F10). Stamped into the
    /// cookie so the session validation hook can detect a later rotation --
    /// account disabled or password changed -- and end the session.
    /// </summary>
    public Guid? SecurityStamp { get; init; }

    public ClaimsPrincipal ToClaimsPrincipal()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, DisplayName),
            new(OmpAuthDefaults.ProviderClaimType, Provider),
            new(OmpAuthDefaults.ProviderUserKeyClaimType, ProviderUserKey)
        };

        if (UserId is int userId)
        {
            claims.Add(new Claim(OmpAuthDefaults.UserIdClaimType, userId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (SecurityStamp is Guid securityStamp)
        {
            claims.Add(new Claim(OmpAuthDefaults.SecurityStampClaimType, securityStamp.ToString()));
        }

        claims.AddRange(RolePrincipals
            .Where(principal =>
                !string.IsNullOrWhiteSpace(principal.PrincipalType) &&
                !string.IsNullOrWhiteSpace(principal.Principal))
            .Select(principal => new Claim(
                OmpAuthDefaults.PrincipalClaimType,
                principal.PrincipalType + "|" + principal.Principal)));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, OmpAuthDefaults.AuthenticationScheme));
    }
}

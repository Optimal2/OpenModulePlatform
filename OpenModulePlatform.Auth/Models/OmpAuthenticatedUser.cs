// File: OpenModulePlatform.Auth/Models/OmpAuthenticatedUser.cs
using OpenModulePlatform.Web.Shared.Security;
using System.Security.Claims;

namespace OpenModulePlatform.Auth.Models;

public sealed class OmpAuthenticatedUser
{
    public int? UserId { get; init; }
    public string DisplayName { get; init; } = "";
    public string Provider { get; init; } = "";
    public string ProviderUserKey { get; init; } = "";
    public IReadOnlyList<(string PrincipalType, string Principal)> RolePrincipals { get; init; } = [];

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

        foreach (var principal in RolePrincipals)
        {
            if (!string.IsNullOrWhiteSpace(principal.PrincipalType) &&
                !string.IsNullOrWhiteSpace(principal.Principal))
            {
                claims.Add(new Claim(
                    OmpAuthDefaults.PrincipalClaimType,
                    principal.PrincipalType + "|" + principal.Principal));
            }
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, OmpAuthDefaults.AuthenticationScheme));
    }
}

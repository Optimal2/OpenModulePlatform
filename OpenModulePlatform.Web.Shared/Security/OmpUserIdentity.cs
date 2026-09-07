// File: OpenModulePlatform.Web.Shared/Security/OmpUserIdentity.cs
using System.Globalization;
using System.Security.Claims;

namespace OpenModulePlatform.Web.Shared.Security;

/// <summary>
/// Reads the OMP user identity of record from a signed-in principal. The user id is
/// the integer key of omp.users, carried in the <see cref="OmpAuthDefaults.UserIdClaimType"/>
/// claim by every OMP sign-in path. This is the one helper modules should use when
/// they need "which OMP user" rather than a display name.
/// </summary>
public static class OmpUserIdentity
{
    /// <summary>The omp.users.user_id of the principal, or null when not signed in through OMP.</summary>
    public static int? TryGetOmpUserId(ClaimsPrincipal? user)
    {
        var claimValue = user?.FindFirstValue(OmpAuthDefaults.UserIdClaimType);
        return int.TryParse(claimValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId)
            && userId > 0
            ? userId
            : null;
    }

    /// <summary>
    /// A display name for the principal: the identity name when present, otherwise
    /// the first name-like claim, otherwise null. A snapshot for logs and labels,
    /// never an identity of record.
    /// </summary>
    public static string? TryGetDisplayName(ClaimsPrincipal? user)
    {
        if (user is null)
        {
            return null;
        }

        var name = user.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        name = user.FindFirstValue("name") ?? user.FindFirstValue(ClaimTypes.Name) ?? user.FindFirstValue("preferred_username");
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}

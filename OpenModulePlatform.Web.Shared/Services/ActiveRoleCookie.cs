using Microsoft.AspNetCore.Http;

namespace OpenModulePlatform.Web.Shared.Services;

/// <summary>
/// Shared cookie settings for the currently selected OMP role.
/// </summary>
public static class ActiveRoleCookie
{
    public const string CookieName = "omp_active_role";
    public const string ClaimType = "omp_active_role";

    public static void Clear(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Cookies.Delete(
            CookieName,
            new CookieOptions
            {
                Path = "/",
                // Must match the Secure flag the cookie was set with, which now
                // follows the connection so plain-HTTP deployments work; a
                // mismatched deletion cookie would not clear the role (R4-E5).
                Secure = response.HttpContext.Request.IsHttps
            });
    }
}

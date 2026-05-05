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
                Secure = true
            });
    }
}

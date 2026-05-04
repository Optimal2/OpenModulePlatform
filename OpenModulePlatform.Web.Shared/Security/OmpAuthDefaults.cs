// File: OpenModulePlatform.Web.Shared/Security/OmpAuthDefaults.cs
namespace OpenModulePlatform.Web.Shared.Security;

/// <summary>
/// Shared authentication constants for OMP web applications.
/// </summary>
public static class OmpAuthDefaults
{
    public const string AuthenticationScheme = "OmpAuth";
    public const string CookieName = ".OpenModulePlatform.Auth";

    public const string UserIdClaimType = "omp:user_id";
    public const string ProviderClaimType = "omp:provider";
    public const string ProviderUserKeyClaimType = "omp:provider_user_key";
    public const string PrincipalClaimType = "omp:principal";
}

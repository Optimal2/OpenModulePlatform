// File: OpenModulePlatform.Web.Shared/Security/OmpRbacDefaults.cs
namespace OpenModulePlatform.Web.Shared.Security;

/// <summary>
/// Shared RBAC constants for built-in OMP role-principal mappings.
/// </summary>
public static class OmpRbacDefaults
{
    public const string SystemPrincipalType = "OMPSystem";
    public const string EveryonePrincipal = "Everyone";
    public const string AuthenticatedUsersPrincipal = "AuthenticatedUsers";

    public const string EveryoneRoleName = "Everyone";
    public const string AuthenticatedUsersRoleName = "AuthenticatedUsers";

    public const string ConfigurationCategory = "rbac";
    public const string AuthenticatedUsersWindowsDomainsSetting = "authenticatedUsersWindowsDomains";
}

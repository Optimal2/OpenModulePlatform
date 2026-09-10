// File: OpenModulePlatform.Portal/Security/OmpPortalPermissions.cs
using OpenModulePlatform.Web.Shared.Navigation;

namespace OpenModulePlatform.Portal.Security;

/// <summary>
/// Well-known permission names used by the shared OMP Portal.
/// </summary>
public static class OmpPortalPermissions
{
    public const string View = "OMP.Portal.View";

    public const string Admin = PortalAdminNavigation.AdminPermission;

    /// <summary>Read access to the user log alone; PortalAdmins get it too, so an admin never loses the page.</summary>
    public const string UserLogView = PortalAdminNavigation.UserLogPermission;
}

namespace OpenModulePlatform.Web.Shared.Navigation;

/// <summary>
/// Shared definition for the portal admin navigation structure used by both the portal navbar and
/// the shared module top bar admin menu. It is also the one place that knows which permission
/// opens which admin page, so the menus, the return-URL check and the favourites filter agree.
/// </summary>
public static class PortalAdminNavigation
{
    /// <summary>Opens every admin page and the whole admin menu.</summary>
    public const string AdminPermission = "OMP.Portal.Admin";

    /// <summary>
    /// Opens the user log (the audit trail) on its own. Meant for auditors and
    /// reviewers who must not get the rest of the admin area with it.
    /// </summary>
    public const string UserLogPermission = "OMP.Portal.UserLog.View";

    private sealed record Entry(string Section, string TextKey, string RelativePath, string? Permission = null);

    // Order is menu order. An entry without a permission is admin-only.
    private static readonly IReadOnlyList<Entry> Entries =
    [
        new("System", "Installation", "/admin/instancetemplateedit?id=1"),
        new("System", "Import/export", "/admin/modulepackageimport"),
        new("System", "Artifacts", "/admin/artifacts"),
        new("System", "Maintenance", "/admin/maintenance"),
        new("System", "User log", "/admin/activitylog", UserLogPermission),
        new("System", "Operations", "/admin/hostdeployments"),
        new("System", "Resource monitor", "/admin/hostresources"),
        new("Administration", "Navigation", "/admin/portalentries"),
        new("Administration", "iFrame URLs", "/admin/iframeurls"),
        new("Administration", "Dashboard widgets", "/admin/dashboardwidgets"),
        new("Administration", "Banners", "/admin/banners"),
        new("Administration", "Config settings", "/admin/configsettings"),
        new("Administration", "Security", "/admin/security"),
        new("Administration", "Users", "/admin/users")
    ];

    /// <summary>Every section and item, for a Portal administrator.</summary>
    public static IReadOnlyList<PortalAdminMenuSection> CreateSections(Func<string, string> hrefFactory)
        => CreateSections(hrefFactory, _ => true);

    /// <summary>
    /// The sections and items the given permissions open: everything for a Portal
    /// administrator, otherwise only the items whose own permission is held. Sections
    /// left without items are dropped, so the result is empty for a plain user.
    /// </summary>
    public static IReadOnlyList<PortalAdminMenuSection> CreateSections(
        Func<string, string> hrefFactory,
        IReadOnlySet<string> permissions)
        => permissions.Contains(AdminPermission)
            ? CreateSections(hrefFactory)
            : CreateSections(hrefFactory, entry => Opens(entry, permissions));

    /// <summary>
    /// True when the permissions open the admin page at <paramref name="relativePath"/>
    /// (portal-relative, such as <c>/admin/activitylog?Range=7d</c>; query and case are
    /// ignored). Unknown admin paths stay admin-only.
    /// </summary>
    public static bool CanAccess(string? relativePath, IReadOnlySet<string> permissions)
    {
        if (permissions.Contains(AdminPermission))
        {
            return true;
        }

        var path = NormalizePath(relativePath);
        return Entries.Any(entry =>
            Opens(entry, permissions)
            && string.Equals(NormalizePath(entry.RelativePath), path, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Opens(Entry entry, IReadOnlySet<string> permissions)
        => entry.Permission is not null && permissions.Contains(entry.Permission);

    private static IReadOnlyList<PortalAdminMenuSection> CreateSections(
        Func<string, string> hrefFactory,
        Func<Entry, bool> include)
        => Entries
            .Where(include)
            .GroupBy(entry => entry.Section)
            .Select(section => new PortalAdminMenuSection(
                section.Key,
                section
                    .Select(entry => new PortalAdminMenuItem(entry.TextKey, hrefFactory(entry.RelativePath), Permission: entry.Permission))
                    .ToArray()))
            .ToArray();

    private static string NormalizePath(string? path)
    {
        var value = (path ?? string.Empty).Trim();
        var cut = value.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            value = value[..cut];
        }

        value = value.TrimEnd('/');
        return value.StartsWith('/') ? value : $"/{value}";
    }
}

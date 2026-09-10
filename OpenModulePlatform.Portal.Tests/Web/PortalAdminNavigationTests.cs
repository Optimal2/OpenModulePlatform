// File: OpenModulePlatform.Portal.Tests/Web/PortalAdminNavigationTests.cs
using OpenModulePlatform.Web.Shared.Navigation;

namespace OpenModulePlatform.Portal.Tests.Web;

/// <summary>
/// The admin menu and the admin-path gates share one table of pages and the
/// permission that opens each. A Portal administrator gets everything; the
/// user log permission opens the user log alone; a plain user gets nothing.
/// The SQL guards pin the seed, so a fresh or upgraded install has the row
/// the page checks for.
/// </summary>
public sealed class PortalAdminNavigationTests
{
    private static readonly Func<string, string> Href = relativePath => $"/portal{relativePath}";

    [Fact]
    public void Admin_SeesEverySectionAndItem()
    {
        var sections = PortalAdminNavigation.CreateSections(Href, Set(PortalAdminNavigation.AdminPermission));

        Assert.Equal(["System", "Administration"], sections.Select(section => section.TextKey));
        Assert.Equal(
            PortalAdminNavigation.CreateSections(Href).SelectMany(section => section.Items),
            sections.SelectMany(section => section.Items));
        Assert.Contains(sections.SelectMany(section => section.Items), item => item.TextKey == "User log" && item.Href == "/portal/admin/activitylog");
    }

    [Fact]
    public void UserLogPermission_OpensOnlyTheUserLog()
    {
        var sections = PortalAdminNavigation.CreateSections(Href, Set(PortalAdminNavigation.UserLogPermission));

        var section = Assert.Single(sections);
        Assert.Equal("System", section.TextKey);
        var item = Assert.Single(section.Items);
        Assert.Equal("User log", item.TextKey);
        Assert.Equal("/portal/admin/activitylog", item.Href);
        Assert.Equal(PortalAdminNavigation.UserLogPermission, item.Permission);
    }

    [Fact]
    public void PlainUser_GetsNoAdminMenu()
    {
        Assert.Empty(PortalAdminNavigation.CreateSections(Href, Set()));
        Assert.Empty(PortalAdminNavigation.CreateSections(Href, Set("OMP.Portal.View")));
    }

    [Theory]
    [InlineData("/admin/activitylog")]
    [InlineData("/admin/activitylog?Range=7d&Take=500")]
    [InlineData("/Admin/ActivityLog/")]
    public void UserLogPermission_OpensTheUserLogPath(string path)
    {
        Assert.True(PortalAdminNavigation.CanAccess(path, Set(PortalAdminNavigation.UserLogPermission)));
    }

    [Theory]
    [InlineData("/admin")]
    [InlineData("/admin/users")]
    [InlineData("/admin/activitylog/anything")]
    [InlineData("/admin/security/role?roleId=1")]
    public void UserLogPermission_OpensNoOtherAdminPath(string path)
    {
        Assert.False(PortalAdminNavigation.CanAccess(path, Set(PortalAdminNavigation.UserLogPermission)));
    }

    [Fact]
    public void Admin_OpensEveryAdminPath()
    {
        Assert.True(PortalAdminNavigation.CanAccess("/admin", Set(PortalAdminNavigation.AdminPermission)));
        Assert.True(PortalAdminNavigation.CanAccess("/admin/not-in-the-menu", Set(PortalAdminNavigation.AdminPermission)));
    }

    [Fact]
    public void PlainUser_OpensNoAdminPath()
    {
        Assert.False(PortalAdminNavigation.CanAccess("/admin/activitylog", Set()));
        Assert.False(PortalAdminNavigation.CanAccess("/admin/activitylog", Set("OMP.Portal.View")));
    }

    [Fact]
    public void UserLogPermission_IsSeededAndProbed()
    {
        var initialize = ReadRepositoryTextFile("OpenModulePlatform.Portal", "sql", "2-initialize-omp-portal.sql");
        var validate = ReadRepositoryTextFile("OpenModulePlatform.Portal", "sql", "0-validate-omp-portal.sql");
        var definition = ReadRepositoryTextFile("OpenModulePlatform.Portal", "omp_portal.module-definition.json");

        Assert.Contains($"N'{PortalAdminNavigation.UserLogPermission}'", initialize);
        Assert.Contains("VALUES(@PortalAdminsRoleId, @PortalUserLogPermissionId)", initialize);
        Assert.Contains($"WHERE Name = N'{PortalAdminNavigation.UserLogPermission}'", validate);
        Assert.Contains($"\"name\": \"{PortalAdminNavigation.UserLogPermission}\"", definition);
        Assert.Contains($"\"permissionName\": \"{PortalAdminNavigation.UserLogPermission}\"", definition);
    }

    private static IReadOnlySet<string> Set(params string[] permissions)
        => new HashSet<string>(permissions, StringComparer.Ordinal);

    private static string ReadRepositoryTextFile(params string[] relativePathSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "OpenModulePlatform.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException("Could not locate OpenModulePlatform repository root.");
        }

        return File.ReadAllText(Path.Join([directory.FullName, .. relativePathSegments]));
    }
}

namespace OpenModulePlatform.Web.Shared.Navigation;

/// <summary>
/// View model for the compact portal shortcut bar shown above module navigation.
/// </summary>
public sealed class PortalTopBarModel
{
    public static PortalTopBarModel Hidden { get; } = new()
    {
        IsVisible = false,
        Links = Array.Empty<PortalTopBarLink>(),
        ModuleLinks = Array.Empty<PortalTopBarLink>(),
        PortalAdminLinks = Array.Empty<PortalTopBarLink>(),
        PortalAdminSections = Array.Empty<PortalAdminMenuSection>(),
        LanguageOptions = Array.Empty<PortalTopBarCultureOption>(),
        OverflowToggleTextKey = "More",
        CollapsedToggleTextKey = "Modules",
        PortalAdminToggleTextKey = "Admin",
        LanguageToggleTextKey = "Language",
        PreferredCulture = "en-US",
        EffectiveCulture = "en-US",
        PreferredCultureDisplayText = "English",
        EffectiveCultureDisplayText = "English",
        AvailableRoles = Array.Empty<OpenModulePlatform.Web.Shared.Services.UserRoleOption>()
    };

    public bool IsVisible { get; init; }

    /// <summary>
    /// Combined link collection kept for backwards compatibility.
    /// </summary>
    public IReadOnlyList<PortalTopBarLink> Links { get; init; } = Array.Empty<PortalTopBarLink>();

    public PortalTopBarLink? PortalLink { get; init; }

    public IReadOnlyList<PortalTopBarLink> ModuleLinks { get; init; } = Array.Empty<PortalTopBarLink>();

    public bool IsPortalAdmin { get; init; }

    public IReadOnlyList<PortalTopBarLink> PortalAdminLinks { get; init; } = Array.Empty<PortalTopBarLink>();

    public IReadOnlyList<PortalAdminMenuSection> PortalAdminSections { get; init; } = Array.Empty<PortalAdminMenuSection>();

    public IReadOnlyList<PortalTopBarCultureOption> LanguageOptions { get; init; } = Array.Empty<PortalTopBarCultureOption>();

    public string PreferredCulture { get; init; } = "en-US";

    public string EffectiveCulture { get; init; } = "en-US";

    public string PreferredCultureDisplayText { get; init; } = "English";

    public string EffectiveCultureDisplayText { get; init; } = "English";

    public bool IsCultureFallback { get; init; }

    public string? CurrentUserName { get; init; }

    public IReadOnlyList<OpenModulePlatform.Web.Shared.Services.UserRoleOption> AvailableRoles { get; init; } = Array.Empty<OpenModulePlatform.Web.Shared.Services.UserRoleOption>();

    public int? ActiveRoleId { get; init; }

    public string? ActiveRoleName { get; init; }

    public string OverflowToggleTextKey { get; init; } = "More";

    public string CollapsedToggleTextKey { get; init; } = "Modules";

    public string PortalAdminToggleTextKey { get; init; } = "Admin";

    public string LanguageToggleTextKey { get; init; } = "Language";
}

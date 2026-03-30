namespace OpenModulePlatform.Web.Shared.Navigation;

/// <summary>
/// Represents a selectable culture option in the shared portal top bar.
/// </summary>
public sealed record PortalTopBarCultureOption(string Culture, string TextKey, bool IsCurrent);

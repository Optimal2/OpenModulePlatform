using System.Resources;
using OpenModulePlatform.Installer.Core;

namespace OpenModulePlatform.Installer.Ui;

/// <summary>
/// UI text access. The neutral resource is English; Swedish ships in
/// InstallerStrings.sv.resx, and the resource manager picks from the OS UI
/// culture automatically (CurrentUICulture).
/// </summary>
internal static class UiText
{
    private static readonly ResourceManager Resources = new(
        "OpenModulePlatform.Installer.Resources.InstallerStrings",
        typeof(UiText).Assembly);

    public static string Get(string key)
        => Resources.GetString(key) ?? key;

    public static string Format(string key, params object[] args)
        => string.Format(Get(key), args);

    /// <summary>Localized title for a prerequisite check line.</summary>
    public static string CheckTitle(PrerequisiteCheckId id)
        => Get("Check_" + id);

    /// <summary>Localized label for a confirmation-card row.</summary>
    public static string CardLabel(string englishLabel)
        => englishLabel switch
        {
            "Profile" => Get("LabelProfile"),
            "Computer" => Get("LabelComputer"),
            "Environment" => Get("LabelEnvironment"),
            "SQL Server" => Get("LabelSqlServer"),
            "Database" => Get("LabelDatabase"),
            "Modules" => Get("LabelModules"),
            "Artifacts" => Get("LabelArtifacts"),
            "Install root" => Get("LabelInstallRoot"),
            _ => englishLabel
        };
}

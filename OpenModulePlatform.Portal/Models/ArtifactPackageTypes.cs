// File: OpenModulePlatform.Portal/Models/ArtifactPackageTypes.cs
namespace OpenModulePlatform.Portal.Models;

/// <summary>
/// Shared package-type labels used by artifact admin pages.
/// </summary>
public static class ArtifactPackageTypes
{
    public static IReadOnlyList<OptionItem> CreateOptions(Func<string, string> localize)
        =>
        [
            Opt("web-app", localize("Web app")),
            Opt("service-app", localize("Service app")),
            Opt("host-agent", localize("HostAgent")),
            Opt("worker", localize("Worker")),
            Opt("worker-plugin", localize("Worker plugin")),
            Opt("channel-type", localize("Channel type")),
            Opt("folder", localize("Folder")),
            Opt("zip", localize("Zip")),
            Opt("nupkg", localize("NuGet package"))
        ];

    private static OptionItem Opt(string value, string label)
        => new() { Value = value, Label = label };
}

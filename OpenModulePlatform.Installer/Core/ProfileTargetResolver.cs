using OpenModulePlatform.Installation;

namespace OpenModulePlatform.Installer.Core;

/// <summary>
/// Resolves the public portal URL of the matched profile from its IIS binding
/// settings, and the artifact runtime major the hosting bundle must cover.
/// </summary>
public static class ProfileTargetResolver
{
    /// <summary>
    /// The portal URL to probe after installation: binding protocol, host header
    /// (localhost when the binding has no host header) and port (omitted when it
    /// is the protocol default).
    /// </summary>
    public static string ResolvePortalUrl(BootstrapConfig config)
    {
        var hostAgent = config.HostAgent;
        var protocol = string.IsNullOrWhiteSpace(hostAgent.IisBindingProtocol)
            ? "http"
            : hostAgent.IisBindingProtocol.Trim().ToLowerInvariant();
        var host = string.IsNullOrWhiteSpace(hostAgent.IisBindingHostHeader)
            ? "localhost"
            : hostAgent.IisBindingHostHeader.Trim();
        var isDefaultPort = (protocol == "http" && hostAgent.IisBindingPort == 80)
            || (protocol == "https" && hostAgent.IisBindingPort == 443);
        return isDefaultPort
            ? $"{protocol}://{host}/"
            : $"{protocol}://{host}:{hostAgent.IisBindingPort}/";
    }

    /// <summary>
    /// True when the profile deploys the OMP authentication application: the
    /// HostAgent enables IIS Windows authentication for that app, so the
    /// Windows Authentication role service is a prerequisite.
    /// An artifact counts when its target (or, lacking a target, its source)
    /// ends in a folder named <c>auth</c>.
    /// </summary>
    public static bool ProfileUsesWindowsAuthentication(BootstrapConfig config)
        => config.Artifacts.Any(artifact =>
            artifact.Enabled && ArtifactEndsInFolder(artifact, "auth"));

    /// <summary>
    /// True when any selected artifact is a Blazor Server app (folder name
    /// contains "blazor"): Blazor Server needs the IIS WebSocket Protocol
    /// feature for its SignalR circuits.
    /// </summary>
    public static bool ProfileUsesBlazor(BootstrapConfig config)
        => config.Artifacts.Any(artifact =>
            artifact.Enabled && ArtifactFolderName(artifact).Contains("blazor", StringComparison.OrdinalIgnoreCase));

    private static bool ArtifactEndsInFolder(ArtifactPayloadOptions artifact, string folderName)
        => ArtifactFolderName(artifact).Equals(folderName, StringComparison.OrdinalIgnoreCase);

    private static string ArtifactFolderName(ArtifactPayloadOptions artifact)
    {
        var path = !string.IsNullOrWhiteSpace(artifact.Target) ? artifact.Target : artifact.Source;
        var trimmed = (path ?? string.Empty).Trim().TrimEnd('/', '\\');
        var name = Path.GetFileName(trimmed);
        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            name = Path.GetFileNameWithoutExtension(name);
        }

        return name;
    }
}

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
    /// An artifact counts when any folder of its target (or, lacking a target,
    /// its source) names the auth app: hand-written profiles point the target
    /// at the deployed folder (<c>...\WebApps\auth</c>, the IIS path the
    /// HostAgent matches with <c>IsOmpAuthenticationAppPath</c>), while synced
    /// package targets are versioned artifact-store paths such as
    /// <c>omp-auth/web/{version}/payload/OpenModulePlatform.Auth.zip</c>.
    /// </summary>
    public static bool ProfileUsesWindowsAuthentication(BootstrapConfig config)
        => config.Artifacts.Any(artifact =>
            artifact.Enabled && ArtifactMentionsAuthApp(artifact));

    private static bool ArtifactMentionsAuthApp(ArtifactPayloadOptions artifact)
    {
        foreach (var path in new[] { artifact.Target, artifact.Source })
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var trimmed = path.Trim().TrimEnd('/', '\\');
            var name = Path.GetFileName(trimmed);
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                name = Path.GetFileNameWithoutExtension(name);
            }

            if (IsAuthAppName(name))
            {
                return true;
            }

            var directory = Path.GetDirectoryName(trimmed);
            while (!string.IsNullOrEmpty(directory))
            {
                if (IsAuthAppName(Path.GetFileName(directory)))
                {
                    return true;
                }

                var parent = Path.GetDirectoryName(directory);
                if (parent is null || parent.Equals(directory, StringComparison.Ordinal))
                {
                    break;
                }

                directory = parent;
            }
        }

        return false;
    }

    /// <summary>
    /// A folder or zip-base name denotes the auth app when it is exactly
    /// <c>auth</c>/<c>omp-auth</c> or ends with a <c>.auth</c>/<c>-auth</c>/
    /// <c>_auth</c> separator suffix (for example <c>OpenModulePlatform.Auth</c>).
    /// </summary>
    private static bool IsAuthAppName(string name)
        => name.Equals("auth", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".auth", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-auth", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_auth", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when any selected artifact is a Blazor Server app (folder name
    /// contains "blazor"): Blazor Server needs the IIS WebSocket Protocol
    /// feature for its SignalR circuits.
    /// </summary>
    public static bool ProfileUsesBlazor(BootstrapConfig config)
        => config.Artifacts.Any(artifact =>
            artifact.Enabled && ArtifactFolderName(artifact).Contains("blazor", StringComparison.OrdinalIgnoreCase));

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

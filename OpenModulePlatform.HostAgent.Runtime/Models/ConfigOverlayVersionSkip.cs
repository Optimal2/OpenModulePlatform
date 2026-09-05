namespace OpenModulePlatform.HostAgent.Runtime.Models;

/// <summary>
/// An enabled config overlay that matched every selector for a deployment except
/// its <c>artifactVersion</c> minimum (ADR 0006): the overlay did not apply and
/// the artifact's own configuration was used instead.
/// </summary>
public sealed record ConfigOverlayVersionSkip(
    string OverlayKey,
    string OverlayVersion,
    string MinimumArtifactVersion)
{
    public string ToDiagnosticWarning(string artifactVersion)
        => $"Config overlay '{OverlayKey}' (overlay version {OverlayVersion}) requires artifact version " +
           $"{MinimumArtifactVersion} or later, but this deployment resolves artifact version {artifactVersion}. " +
           "The overlay did not apply; the artifact's own configuration was used.";
}

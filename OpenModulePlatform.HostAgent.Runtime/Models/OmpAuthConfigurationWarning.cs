using System.Text.Json;

namespace OpenModulePlatform.HostAgent.Runtime.Models;

/// <summary>
/// Structured OmpAuth configuration warning produced by the HostAgent.
/// <see cref="Message" /> is the human-readable English text used for HostAgent logs, while
/// <see cref="ToStoredString" /> returns the single-line JSON representation persisted in
/// omp.HostAppDeploymentStates.LastWarning. The Portal maps the JSON code + parameters to
/// localized text at render time; unknown codes fall back to the raw stored line.
/// Keep the code values stable: they are a contract with the Portal render layer.
/// </summary>
public sealed record OmpAuthConfigurationWarning(
    string Code,
    IReadOnlyList<string> Parameters,
    string Message)
{
    public const string CookieNameUnexpectedValueCode = "OmpAuth.CookieName.UnexpectedValue";
    public const string ApplicationNameUnexpectedValueCode = "OmpAuth.ApplicationName.UnexpectedValue";
    public const string DataProtectionKeyPathMismatchCode = "OmpAuth.DataProtectionKeyPath.Mismatch";
    public const string DataProtectionKeyPathNotUncPathCode = "OmpAuth.DataProtectionKeyPath.NotUncPath";

    public string ToStoredString()
        => JsonSerializer.Serialize(new { code = Code, @params = Parameters });
}

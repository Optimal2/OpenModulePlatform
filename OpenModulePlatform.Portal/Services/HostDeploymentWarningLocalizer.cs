using System.Text.Json;
using Microsoft.Extensions.Localization;

namespace OpenModulePlatform.Portal.Services;

/// <summary>
/// Maps structured HostAgent diagnostic warning lines stored in
/// omp.HostAppDeploymentStates.LastWarning to localized display text.
/// The HostAgent is a background service without user culture, so it stores each OmpAuth
/// warning as a single JSON line: {"code":"&lt;code&gt;","params":["..."]}.
/// Lines that are not structured JSON, or carry an unknown code, are rendered as-is so
/// free-text warnings (for example required-config-section warnings) keep working.
/// The code values are a contract with OpenModulePlatform.HostAgent.Runtime
/// (see OmpAuthConfigurationWarning); keep them in sync.
/// </summary>
public static class HostDeploymentWarningLocalizer
{
    public static string? Localize(string? storedWarning, IStringLocalizer localizer)
    {
        if (string.IsNullOrWhiteSpace(storedWarning))
        {
            return storedWarning;
        }

        var lines = storedWarning.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = LocalizeLine(lines[i], localizer);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string LocalizeLine(string line, IStringLocalizer localizer)
    {
        if (!line.StartsWith('{'))
        {
            return line;
        }

        string? code;
        IReadOnlyList<string> parameters;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("code", out var codeElement)
                || codeElement.ValueKind != JsonValueKind.String)
            {
                return line;
            }

            code = codeElement.GetString();
            parameters = root.TryGetProperty("params", out var paramsElement)
                && paramsElement.ValueKind == JsonValueKind.Array
                    ? paramsElement
                        .EnumerateArray()
                        .Where(element => element.ValueKind == JsonValueKind.String)
                        .Select(element => element.GetString()!)
                        .ToArray()
                    : [];
        }
        catch (JsonException)
        {
            return line;
        }

        return code switch
        {
            "OmpAuth.CookieName.UnexpectedValue" when parameters.Count == 2
                => localizer[
                    "OmpAuth:CookieName is '{0}' but the expected OMP default is '{1}'. " +
                    "Shared auth cookies may break if this value differs across OMP web apps.",
                    parameters[0], parameters[1]].Value,

            "OmpAuth.ApplicationName.UnexpectedValue" when parameters.Count == 2
                => localizer[
                    "OmpAuth:ApplicationName is '{0}' but the expected OMP default is '{1}'. " +
                    "Shared auth cookies may break if this value differs across OMP web apps.",
                    parameters[0], parameters[1]].Value,

            "OmpAuth.DataProtectionKeyPath.Mismatch" when parameters.Count == 2
                => localizer[
                    "OmpAuth:DataProtectionKeyPath is '{0}' but the HostAgent expects '{1}'. " +
                    "Data protection keys must be shared across OMP web apps for auth-cookie compatibility.",
                    parameters[0], parameters[1]].Value,

            "OmpAuth.DataProtectionKeyPath.NotUncPath" when parameters.Count == 1
                => localizer[
                    "OmpAuth:DataProtectionKeyPath '{0}' is not a UNC path. " +
                    "A local key path will break auth-cookie sharing in load-balanced scenarios.",
                    parameters[0]].Value,

            _ => line
        };
    }
}

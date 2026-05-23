using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace OpenModulePlatform.Artifacts;

/// <summary>
/// Normalizes portable module definition packages before they are stored in OMP.
/// </summary>
public static class ModuleDefinitionPackageNormalizer
{
    private const int MaxExternalSqlFileBytes = 1024 * 1024 * 5;

    /// <summary>
    /// Reads external SQL files referenced from <c>sqlScripts[].path</c> and
    /// stores their text in <c>inlineSql</c>. The source package can therefore
    /// keep reviewable .sql files while the imported definition remains
    /// self-contained for Portal and HostAgent repair execution.
    /// </summary>
    public static JsonNode NormalizeExternalSqlFiles(
        JsonNode root,
        string definitionPath,
        string packageRoot)
    {
        if (root is not JsonObject rootObject)
        {
            throw new InvalidOperationException("The module definition JSON must be an object.");
        }

        if (rootObject["sqlScripts"] is not JsonArray scripts)
        {
            return rootObject;
        }

        var definitionDirectory = Path.GetDirectoryName(Path.GetFullPath(definitionPath))
            ?? Path.GetFullPath(packageRoot);
        var fullPackageRoot = Path.GetFullPath(packageRoot);

        foreach (var script in scripts.OfType<JsonObject>())
        {
            if (HasSqlContent(script))
            {
                continue;
            }

            var relativePath = GetString(script, "path") ?? GetString(script, "source");
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            var sqlPath = ResolveSqlPath(relativePath, definitionDirectory, fullPackageRoot);
            var fileInfo = new FileInfo(sqlPath);
            if (fileInfo.Length > MaxExternalSqlFileBytes)
            {
                throw new InvalidOperationException(
                    $"Module definition SQL file '{relativePath}' exceeds the limit of {MaxExternalSqlFileBytes} bytes.");
            }

            var sqlText = File.ReadAllText(sqlPath, Encoding.UTF8);
            var actualSha256 = ComputeSha256(sqlText);
            var declaredSha256 = GetString(script, "sha256");
            if (!string.IsNullOrWhiteSpace(declaredSha256)
                && !string.Equals(declaredSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Module definition SQL file '{relativePath}' does not match its declared SHA-256.");
            }

            script["inlineSql"] = sqlText;
            script["sha256"] = actualSha256;
            script.Remove("content");
            script.Remove("contentEncoding");
        }

        return rootObject;
    }

    private static bool HasSqlContent(JsonObject script)
        => !string.IsNullOrWhiteSpace(GetString(script, "inlineSql"))
            || !string.IsNullOrWhiteSpace(GetString(script, "content"));

    private static string ResolveSqlPath(
        string relativePath,
        string definitionDirectory,
        string packageRoot)
    {
        var normalized = NormalizePackagePath(relativePath);
        var definitionRelativePath = ResolveUnderRoot(definitionDirectory, normalized);
        if (File.Exists(definitionRelativePath)
            && IsUnderRoot(definitionRelativePath, packageRoot))
        {
            return definitionRelativePath;
        }

        var packageRelativePath = ResolveUnderRoot(packageRoot, normalized);
        if (File.Exists(packageRelativePath))
        {
            return packageRelativePath;
        }

        throw new FileNotFoundException(
            $"Module definition SQL file '{relativePath}' was not found inside the package.",
            relativePath);
    }

    private static string NormalizePackagePath(string value)
    {
        var normalized = value.Trim().Replace('\\', '/').Trim('/');
        if (normalized.Length == 0
            || normalized.Contains(':', StringComparison.Ordinal)
            || normalized.IndexOf('\0') >= 0
            || normalized.StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Module definition SQL paths must be relative package paths.");
        }

        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0
            || segments.Any(segment => segment is "." or "..")
            || segments.Any(segment => segment.IndexOfAny(invalidFileNameChars) >= 0))
        {
            throw new InvalidOperationException("Module definition SQL paths must stay inside the package.");
        }

        return string.Join('/', segments);
    }

    private static string ResolveUnderRoot(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderRoot(fullPath, fullRoot))
        {
            throw new InvalidOperationException("Module definition SQL path escapes the package root.");
        }

        return fullPath;
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root);
        var normalizedRoot = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(normalizedRoot, comparison);
    }

    private static string? GetString(JsonObject obj, string propertyName)
    {
        // JsonObject lookups are case-sensitive; iterate explicitly so package documents can use case-insensitive field names.
        foreach (var property in obj)
        {
            if (property.Key.Equals(propertyName, StringComparison.OrdinalIgnoreCase)
                && property.Value is JsonValue value
                && value.TryGetValue<string>(out var text))
            {
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
        }

        return null;
    }

    private static string ComputeSha256(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

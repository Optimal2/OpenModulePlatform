using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

internal static class ArtifactConfigurationFileWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static bool AreApplied(
        string targetRoot,
        IReadOnlyList<ArtifactConfigurationFileDescriptor> files,
        IReadOnlyDictionary<string, string> variables)
    {
        if (files.Count == 0)
        {
            return true;
        }

        foreach (var file in files)
        {
            var path = ResolveTargetPath(targetRoot, file);
            var expectedContent = Render(file.FileContent, variables);
            if (!File.Exists(path) || !FileContentEquals(path, expectedContent))
            {
                return false;
            }
        }

        return true;
    }

    public static async Task ApplyAsync(
        string targetRoot,
        IReadOnlyList<ArtifactConfigurationFileDescriptor> files,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken cancellationToken)
    {
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = ResolveTargetPath(targetRoot, file);
            var fileContent = Render(file.FileContent, variables);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            if (File.Exists(path) && FileContentEquals(path, fileContent))
            {
                continue;
            }

            await File.WriteAllTextAsync(path, fileContent, Utf8NoBom, cancellationToken);
        }
    }

    public static IReadOnlyDictionary<string, string> CreateVariables(
        WebAppDeploymentDescriptor deployment,
        string ompConnectionString,
        HostAgentSettings settings)
        => CreateVariables(
            deployment.HostId,
            deployment.HostKey,
            deployment.AppInstanceId,
            deployment.AppInstanceKey,
            deployment.ArtifactId,
            deployment.Version,
            deployment.TargetName,
            ompConnectionString,
            settings);

    public static IReadOnlyDictionary<string, string> CreateVariables(
        ServiceAppDeploymentDescriptor deployment,
        string ompConnectionString,
        HostAgentSettings settings)
        => CreateVariables(
            deployment.HostId,
            deployment.HostKey,
            deployment.AppInstanceId,
            deployment.AppInstanceKey,
            deployment.ArtifactId,
            deployment.Version,
            deployment.TargetName,
            ompConnectionString,
            settings);

    private static string ResolveTargetPath(
        string targetRoot,
        ArtifactConfigurationFileDescriptor file)
    {
        if (string.IsNullOrWhiteSpace(file.RelativePath))
        {
            throw new InvalidOperationException(
                $"Artifact configuration file '{file.ArtifactConfigurationFileId}' has no relative path.");
        }

        var relativePath = file.RelativePath.Trim().Replace('/', Path.DirectorySeparatorChar);
        return DeploymentPath.CombineUnderRoot(
            targetRoot,
            relativePath,
            $"Artifact configuration file '{file.ArtifactConfigurationFileId}' RelativePath");
    }

    private static IReadOnlyDictionary<string, string> CreateVariables(
        Guid hostId,
        string hostKey,
        Guid appInstanceId,
        string appInstanceKey,
        int artifactId,
        string artifactVersion,
        string? targetName,
        string ompConnectionString,
        HostAgentSettings settings)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Omp.HostId"] = hostId.ToString("D"),
            ["Omp.HostKey"] = hostKey,
            ["Omp.AppInstanceId"] = appInstanceId.ToString("D"),
            ["Omp.AppInstanceKey"] = appInstanceKey,
            ["Omp.ArtifactId"] = artifactId.ToString(CultureInfo.InvariantCulture),
            ["Omp.ArtifactVersion"] = artifactVersion,
            ["Omp.TargetName"] = targetName ?? string.Empty,
            ["Omp.ConnectionStrings.OmpDb"] = ompConnectionString,
            ["Omp.HostAgent.CentralArtifactRoot"] = settings.CentralArtifactRoot,
            ["Omp.HostAgent.LocalArtifactCacheRoot"] = settings.LocalArtifactCacheRoot,
            ["Omp.HostAgent.WebAppsRoot"] = settings.WebAppsRoot,
            ["Omp.HostAgent.PortalPhysicalPath"] = settings.PortalPhysicalPath,
            ["Omp.HostAgent.ServicesRoot"] = settings.ServicesRoot,
            ["Omp.HostAgent.WebAppDataProtectionKeyPath"] = settings.WebAppDataProtectionKeyPath
        };

        foreach (var item in variables.ToArray())
        {
            variables["Omp.Json." + item.Key[4..]] = JsonStringContent(item.Value);
        }

        return variables;
    }

    private static string Render(string content, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(content) || !content.Contains("{{Omp.", StringComparison.Ordinal))
        {
            return content;
        }

        var rendered = content;
        foreach (var variable in variables)
        {
            rendered = rendered.Replace(
                "{{" + variable.Key + "}}",
                variable.Value,
                StringComparison.Ordinal);
        }

        return rendered;
    }

    private static string JsonStringContent(string value)
    {
        var serialized = JsonSerializer.Serialize(value);
        return serialized.Length < 2 ? string.Empty : serialized[1..^1];
    }

    private static bool FileContentEquals(string path, string expectedContent)
    {
        var expectedBytes = Utf8NoBom.GetBytes(expectedContent);
        var actualBytes = File.ReadAllBytes(path);
        return actualBytes.AsSpan().SequenceEqual(expectedBytes);
    }
}

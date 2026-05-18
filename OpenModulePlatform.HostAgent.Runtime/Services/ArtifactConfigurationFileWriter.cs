using System.Text;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

internal static class ArtifactConfigurationFileWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static bool AreApplied(
        string targetRoot,
        IReadOnlyList<ArtifactConfigurationFileDescriptor> files)
    {
        if (files.Count == 0)
        {
            return true;
        }

        foreach (var file in files)
        {
            var path = ResolveTargetPath(targetRoot, file);
            if (!File.Exists(path) || !FileContentEquals(path, file.FileContent))
            {
                return false;
            }
        }

        return true;
    }

    public static async Task ApplyAsync(
        string targetRoot,
        IReadOnlyList<ArtifactConfigurationFileDescriptor> files,
        CancellationToken cancellationToken)
    {
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = ResolveTargetPath(targetRoot, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            if (File.Exists(path) && FileContentEquals(path, file.FileContent))
            {
                continue;
            }

            await File.WriteAllTextAsync(path, file.FileContent, Utf8NoBom, cancellationToken);
        }
    }

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

    private static bool FileContentEquals(string path, string expectedContent)
    {
        var expectedBytes = Utf8NoBom.GetBytes(expectedContent);
        var actualBytes = File.ReadAllBytes(path);
        return actualBytes.AsSpan().SequenceEqual(expectedBytes);
    }
}

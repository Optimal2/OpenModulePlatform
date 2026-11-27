using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.Installation.Tests;

public sealed class ArtifactDirectoryHashTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "omp-installation-hash-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InstallationDirectoryHash_SkipsLinkedDirectoriesLikeHostAgent()
    {
        Directory.CreateDirectory(_root);
        var outside = Path.Join(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Join(outside, "secret.txt"), "not part of the artifact");
        var artifact = Path.Join(_root, "artifact");
        Directory.CreateDirectory(artifact);
        File.WriteAllText(Path.Join(artifact, "real.txt"), "payload");
        Assert.True(TryCreateJunction(Path.Join(artifact, "linked"), outside), "The test requires permission to create a directory link.");

        var expected = await ArtifactHash.ComputeSha256Async(artifact, CancellationToken.None);

        Assert.Equal(expected, InstallationEngine.ComputeDirectorySha256(artifact));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            // Remove links first and without recursion, so cleanup never walks into a link target.
            foreach (var link in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories)
                         .Where(path => new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
                         .ToList())
            {
                Directory.Delete(link);
            }

            Directory.Delete(_root, recursive: true);
        }
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return Directory.Exists(linkPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // A symbolic link needs the create-symbolic-link privilege (admin or developer mode).
            // A directory junction does not, and it is the link kind the hash must skip on Windows.
            return OperatingSystem.IsWindows() && TryCreateWindowsJunction(linkPath, targetPath);
        }
    }

    private static bool TryCreateWindowsJunction(string linkPath, string targetPath)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("mklink");
            startInfo.ArgumentList.Add("/J");
            startInfo.ArgumentList.Add(linkPath);
            startInfo.ArgumentList.Add(targetPath);
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0 && Directory.Exists(linkPath);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}

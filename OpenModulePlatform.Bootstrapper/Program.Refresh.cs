using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace OpenModulePlatform.Bootstrapper;

internal static partial class Program
{
    private const int InstallerRefreshParentWaitSeconds = 120;
    private const int InstallerRefreshPathSafetyMargin = 240;
    private const int InstallerRefreshExpectedDeepSuffixLength = 190;

    private static async Task<int> RunInstallerPackageRefreshAsync(CliOptions cli)
    {
        var logPath = Path.Combine(
            Path.GetTempPath(),
            "omp-installer-refresh-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".log");

        await using var log = new StreamWriter(logPath, append: false, Encoding.UTF8);
        Console.SetOut(log);
        Console.SetError(log);

        try
        {
            if (string.IsNullOrWhiteSpace(cli.ConfigPath))
            {
                throw new InvalidOperationException("--config is required for installer package refresh.");
            }

            var configPath = Path.GetFullPath(cli.ConfigPath);
            var payloadRoot = ResolvePayloadRoot(cli, configPath);
            WaitForParentProcess(cli.ParentProcessId);

            var config = await ReadJsonAsync<BootstrapConfig>(configPath);
            var sourceRoot = ResolvePrimaryDeveloperSourceRoot(config, payloadRoot, configPath);
            var packageConfigPath = ResolveDeveloperPackageConfigPath(config, sourceRoot);
            var packageOutputRoot = ResolveSafeInstallerRefreshOutputRoot(config, sourceRoot, payloadRoot);

            Console.WriteLine("OpenModulePlatform installer package refresh");
            Console.WriteLine($"Config:         {configPath}");
            Console.WriteLine($"Package root:   {payloadRoot}");
            Console.WriteLine($"Source root:    {sourceRoot}");
            Console.WriteLine($"Package config: {packageConfigPath}");
            Console.WriteLine($"Output root:    {packageOutputRoot}");
            Console.WriteLine();

            foreach (var root in ResolveDeveloperSourceRoots(config, payloadRoot, configPath))
            {
                var embedScript = Path.Combine(root, "scripts", "dev", "embed-module-definition-sql.ps1");
                if (!File.Exists(embedScript))
                {
                    continue;
                }

                Console.WriteLine($"> Refresh embedded SQL in module definitions: {root}");
                RunProcess(
                    "powershell",
                    [
                        "-NoProfile",
                        "-File",
                        embedScript,
                        "-RepositoryRoot",
                        root
                    ],
                    workingDirectory: root);
            }

            Console.WriteLine("> Build HostAgent-first package from source");
            RunProcess(
                "powershell",
                [
                    "-NoProfile",
                    "-File",
                    Path.Combine(sourceRoot, "scripts", "deployment", "package-hostagent-first.ps1"),
                    "-ConfigPath",
                    packageConfigPath,
                    "-OutputRoot",
                    packageOutputRoot,
                    "-SkipZip"
                ],
                workingDirectory: sourceRoot);

            var generatedPackageRoot = Directory
                .EnumerateDirectories(packageOutputRoot, "OpenModulePlatformHostAgentFirst-*", SearchOption.TopDirectoryOnly)
                .Select(path => new DirectoryInfo(path))
                .OrderByDescending(directory => directory.LastWriteTimeUtc)
                .FirstOrDefault()
                ?.FullName
                ?? throw new InvalidOperationException($"No generated HostAgent-first package was found below {packageOutputRoot}.");

            Console.WriteLine($"Generated package: {generatedPackageRoot}");
            await MergeCurrentBootstrapConfigAsync(config, generatedPackageRoot);
            ReplaceDirectory(generatedPackageRoot, payloadRoot);

            var destinationLogPath = Path.Combine(payloadRoot, "installer-refresh.log");
            await log.FlushAsync();
            File.Copy(logPath, destinationLogPath, overwrite: true);

            if (cli.RestartGui)
            {
                StartInstallerGui(payloadRoot);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Installer package refresh failed.");
            Console.Error.WriteLine(ex);
            await log.FlushAsync();

            if (OperatingSystem.IsWindows() && Environment.UserInteractive)
            {
                MessageBox.Show(
                    $"Installer package refresh failed. Details were written to:{Environment.NewLine}{logPath}{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                    "OpenModulePlatform installer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            return 1;
        }
    }

    private static void WaitForParentProcess(int parentProcessId)
    {
        if (parentProcessId <= 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(parentProcessId);
            process.WaitForExit(TimeSpan.FromSeconds(InstallerRefreshParentWaitSeconds));
        }
        catch (ArgumentException)
        {
            // The parent already exited, which is the desired state before replacing its package.
        }
        catch (InvalidOperationException)
        {
            // The parent process handle is no longer usable; continue and let file replacement prove safety.
        }
    }

    private static async Task MergeCurrentBootstrapConfigAsync(BootstrapConfig currentConfig, string generatedPackageRoot)
    {
        var generatedConfigPath = Path.Combine(generatedPackageRoot, "bootstrap.local.sample.json");
        var generatedConfig = await ReadJsonAsync<BootstrapConfig>(generatedConfigPath);
        currentConfig.Artifacts = generatedConfig.Artifacts;
        currentConfig.Sql.ArtifactVersionOverrides = generatedConfig.Sql.ArtifactVersionOverrides;
        currentConfig.Sql.ArtifactVersionVariableOverrides = generatedConfig.Sql.ArtifactVersionVariableOverrides;

        var json = JsonSerializer.Serialize(currentConfig, JsonOptions);
        await File.WriteAllTextAsync(generatedConfigPath, json + Environment.NewLine, Encoding.UTF8);
    }

    private static IReadOnlyList<string> ResolveDeveloperSourceRoots(
        BootstrapConfig config,
        string payloadRoot,
        string configPath)
    {
        var configuredRoots = ParseDeveloperSourceRoots(config.DeveloperSource.SourceRoot)
            .Select(Path.GetFullPath)
            .Where(root => File.Exists(Path.Combine(root, "omp-components.json")))
            .ToList();

        if (configuredRoots.Any(IsOpenModulePlatformSourceRoot))
        {
            return configuredRoots;
        }

        foreach (var start in GetDeveloperSourceSearchStarts(payloadRoot, configPath))
        {
            foreach (var candidate in EnumerateSelfAndParents(start))
            {
                if (IsOpenModulePlatformSourceRoot(candidate))
                {
                    configuredRoots.Insert(0, candidate);
                    return configuredRoots;
                }
            }
        }

        throw new DirectoryNotFoundException("Developer source roots must include an OpenModulePlatform source repository.");
    }

    private static string ResolvePrimaryDeveloperSourceRoot(
        BootstrapConfig config,
        string payloadRoot,
        string configPath)
    {
        foreach (var sourceRoot in ResolveDeveloperSourceRoots(config, payloadRoot, configPath))
        {
            if (IsOpenModulePlatformSourceRoot(sourceRoot))
            {
                return sourceRoot;
            }
        }

        throw new DirectoryNotFoundException("Developer source roots must include an OpenModulePlatform source repository.");
    }

    private static string ResolveDeveloperPackageConfigPath(BootstrapConfig config, string sourceRoot)
    {
        if (!string.IsNullOrWhiteSpace(config.DeveloperSource.PackageConfigPath))
        {
            var configured = Path.GetFullPath(config.DeveloperSource.PackageConfigPath);
            if (!File.Exists(configured))
            {
                throw new FileNotFoundException("Configured package config was not found.", configured);
            }

            return configured;
        }

        var local = Path.Combine(sourceRoot, "scripts", "deployment", "omp-suite.local.psd1");
        if (File.Exists(local))
        {
            return local;
        }

        var sample = Path.Combine(sourceRoot, "scripts", "deployment", "omp-suite.config.sample.psd1");
        if (File.Exists(sample))
        {
            return sample;
        }

        throw new FileNotFoundException("No package config was found below the OpenModulePlatform source repository.");
    }

    private static string ResolveSafeInstallerRefreshOutputRoot(
        BootstrapConfig config,
        string sourceRoot,
        string payloadRoot)
    {
        var configured = string.IsNullOrWhiteSpace(config.DeveloperSource.PackageOutputRoot)
            ? Path.Combine(ResolveShortInstallerRefreshBaseRoot(config, sourceRoot), "PackageRefresh")
            : Path.GetFullPath(config.DeveloperSource.PackageOutputRoot);
        if (!PathOverlaps(configured, payloadRoot) && !RisksInstallerRefreshPathLimit(configured))
        {
            return configured;
        }

        return Path.Combine(
            ResolveShortInstallerRefreshBaseRoot(config, sourceRoot),
            "PackageRefresh",
            DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
    }

    private static string ResolveShortInstallerRefreshBaseRoot(BootstrapConfig config, string sourceRoot)
    {
        if (!string.IsNullOrWhiteSpace(config.ArtifactStoreRoot))
        {
            var artifactStoreParent = Directory.GetParent(Path.GetFullPath(config.ArtifactStoreRoot))?.FullName;
            if (!string.IsNullOrWhiteSpace(artifactStoreParent))
            {
                return artifactStoreParent;
            }
        }

        var sourceDrive = Path.GetPathRoot(Path.GetFullPath(sourceRoot));
        return Path.Combine(
            string.IsNullOrWhiteSpace(sourceDrive) ? Path.GetPathRoot(Path.GetTempPath()) ?? Path.GetTempPath() : sourceDrive,
            "OMP");
    }

    private static bool RisksInstallerRefreshPathLimit(string path)
        => Path.GetFullPath(path).Length + InstallerRefreshExpectedDeepSuffixLength > InstallerRefreshPathSafetyMargin;

    private static IEnumerable<string> ParseDeveloperSourceRoots(string value)
        => value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static item => !string.IsNullOrWhiteSpace(item));

    private static IEnumerable<string> GetDeveloperSourceSearchStarts(string payloadRoot, string configPath)
    {
        yield return payloadRoot;
        yield return Path.GetDirectoryName(configPath) ?? Environment.CurrentDirectory;
        yield return AppContext.BaseDirectory;
        yield return Environment.CurrentDirectory;
    }

    private static IEnumerable<string> EnumerateSelfAndParents(string path)
    {
        var directory = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new DirectoryInfo(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
        while (directory is not null)
        {
            yield return directory.FullName;
            directory = directory.Parent;
        }
    }

    private static bool IsOpenModulePlatformSourceRoot(string path)
        => File.Exists(Path.Combine(path, "omp-components.json"))
            && File.Exists(Path.Combine(path, "OpenModulePlatform.slnx"))
            && File.Exists(Path.Combine(path, "scripts", "deployment", "package-hostagent-first.ps1"));

    private static void ReplaceDirectory(string source, string destination)
    {
        if (PathOverlaps(source, destination))
        {
            throw new InvalidOperationException("Generated package root must not overlap the destination package root.");
        }

        var backup = destination.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + ".backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        DeleteDirectoryIfExists(backup);

        if (Directory.Exists(destination))
        {
            Directory.Move(destination, backup);
        }

        try
        {
            CopyDirectoryRecursive(source, destination);
            DeleteDirectoryBestEffort(backup);
            DeleteDirectoryBestEffort(source);
        }
        catch
        {
            DeleteDirectoryIfExists(destination);
            if (Directory.Exists(backup))
            {
                Directory.Move(backup, destination);
            }

            throw;
        }
    }

    private static void CopyDirectoryRecursive(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? destination);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        Directory.Delete(path, recursive: true);
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            DeleteDirectoryIfExists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Could not delete temporary directory '{path}': {ex.Message}");
        }
    }

    private static void StartInstallerGui(string packageRoot)
    {
        var executable = Path.Combine(packageRoot, "OpenModulePlatform.Bootstrapper.exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("Updated installer executable was not found.", executable);
        }

        Process.Start(new ProcessStartInfo(executable)
        {
            WorkingDirectory = packageRoot,
            UseShellExecute = true
        });
    }

    private static bool PathOverlaps(string left, string right)
        => IsSameOrParentPath(left, right) || IsSameOrParentPath(right, left);

    private static bool IsSameOrParentPath(string parentPath, string childPath)
    {
        var parent = WithTrailingDirectorySeparator(Path.GetFullPath(parentPath));
        var child = WithTrailingDirectorySeparator(Path.GetFullPath(childPath));
        return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static string WithTrailingDirectorySeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
}

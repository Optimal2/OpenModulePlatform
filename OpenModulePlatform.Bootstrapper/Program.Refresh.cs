using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace OpenModulePlatform.Bootstrapper;

internal static partial class Program
{
    private const int InstallerRefreshParentWaitSeconds = 120;
    private const int InstallerRefreshPathSafetyMargin = 240;
    private const int InstallerRefreshExpectedDeepSuffixLength = 190;

    private static async Task<int> RunInstallerPackageRefreshAsync(CliOptions cli)
    {
        var logPath = Path.Join(
            Path.GetTempPath(),
            "omp-installer-refresh-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".log");

        if (OperatingSystem.IsWindows() && Environment.UserInteractive && cli.RestartGui)
        {
            return RunInstallerPackageRefreshWithProgress(cli, logPath);
        }

        await using var log = new StreamWriter(logPath, append: false, Encoding.UTF8);
        Console.SetOut(log);
        Console.SetError(log);

        try
        {
            await RunInstallerPackageRefreshCoreAsync(cli, logPath);
            return 0;
        }
        catch (JsonException ex)
        {
            // Detached refresh process boundary: write full diagnostics to the log and return a failure code to the launcher.
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
        catch (SystemException ex)
        {
            // Detached refresh process boundary: write full diagnostics to the log and return a failure code to the launcher.
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

    private static int RunInstallerPackageRefreshWithProgress(CliOptions cli, string logPath)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(defaultValue: false);

        using var form = new InstallerRefreshProgressForm(logPath);
        form.Shown += async (_, _) =>
        {
            var exitCode = 1;
            await using var log = new StreamWriter(logPath, append: false, Encoding.UTF8);
            var writer = new InstallerRefreshProgressWriter(log, form.AppendLogLine);
            Console.SetOut(writer);
            Console.SetError(writer);

            try
            {
                form.SetStatus("Building updated installer package...");
                await RunInstallerPackageRefreshCoreAsync(cli, logPath);
                exitCode = 0;
                form.SetStatus("Updated installer package created. Starting installer...");
            }
            catch (JsonException ex)
            {
                // Progress UI boundary: keep the background refresh failure visible while preserving the detailed log file.
                Console.Error.WriteLine("Installer package refresh failed.");
                Console.Error.WriteLine(ex);
                form.SetStatus("Installer package refresh failed.");
                MessageBox.Show(
                    form,
                    $"Installer package refresh failed. Details were written to:{Environment.NewLine}{logPath}{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                    "OpenModulePlatform installer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch (SystemException ex)
            {
                // Progress UI boundary: keep the background refresh failure visible while preserving the detailed log file.
                Console.Error.WriteLine("Installer package refresh failed.");
                Console.Error.WriteLine(ex);
                form.SetStatus("Installer package refresh failed.");
                MessageBox.Show(
                    form,
                    $"Installer package refresh failed. Details were written to:{Environment.NewLine}{logPath}{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                    "OpenModulePlatform installer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                await writer.FlushAsync();
                form.ExitCode = exitCode;
                form.BeginInvoke(new Action(form.Close));
            }
        };

        Application.Run(form);
        return form.ExitCode;
    }

    private static async Task RunInstallerPackageRefreshCoreAsync(CliOptions cli, string logPath)
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
        Console.WriteLine($"Log file:       {logPath}");
        Console.WriteLine();

        foreach (var root in ResolveDeveloperSourceRoots(config, payloadRoot, configPath))
        {
            var embedScript = Path.Join(root, "scripts", "dev", "embed-module-definition-sql.ps1");
            if (!File.Exists(embedScript))
            {
                continue;
            }

            Console.WriteLine($"> Refresh embedded SQL in module definitions: {root}");
            RunProcessStreaming(
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
        RunProcessStreaming(
            "powershell",
            [
                "-NoProfile",
                "-File",
                Path.Join(sourceRoot, "scripts", "deployment", "package-hostagent-first.ps1"),
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
        await MergeCurrentBootstrapConfigAsync(config, configPath, payloadRoot, generatedPackageRoot);
        MergeCurrentPackageData(payloadRoot, generatedPackageRoot, configPath);
        ReplaceDirectory(generatedPackageRoot, payloadRoot);

        var destinationLogPath = Path.Join(payloadRoot, "installer-refresh.log");
        Console.Out.Flush();
        File.Copy(logPath, destinationLogPath, overwrite: true);

        if (cli.RestartGui)
        {
            StartInstallerGui(payloadRoot);
        }
    }

    private static void RunProcessStreaming(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(fileName)
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            info.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Could not start process: {fileName}");
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                Console.WriteLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                Console.Error.WriteLine(args.Data);
            }
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} failed with exit code {process.ExitCode}. See the installer refresh log for details.");
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

    private static async Task MergeCurrentBootstrapConfigAsync(
        BootstrapConfig currentConfig,
        string currentConfigPath,
        string currentPackageRoot,
        string generatedPackageRoot)
    {
        var generatedTemplateConfigPath = ResolveGeneratedTemplateConfigPath(generatedPackageRoot);
        var generatedConfig = await ReadJsonAsync<BootstrapConfig>(generatedTemplateConfigPath);
        var currentConfigRoot = Path.Join(currentPackageRoot, "configs");
        var configsToMerge = Directory.Exists(currentConfigRoot)
            ? Directory.EnumerateFiles(currentConfigRoot, "*.json", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [currentConfigPath];

        var wroteAnyConfig = false;
        foreach (var configPath in configsToMerge)
        {
            var profileConfig = string.Equals(
                Path.GetFullPath(configPath),
                Path.GetFullPath(currentConfigPath),
                StringComparison.OrdinalIgnoreCase)
                    ? currentConfig
                    : await ReadJsonAsync<BootstrapConfig>(configPath);

            ApplyGeneratedPayloadMetadata(profileConfig, generatedConfig);
            var generatedConfigPath = ResolveGeneratedCurrentConfigPath(
                configPath,
                currentPackageRoot,
                generatedPackageRoot,
                generatedTemplateConfigPath);
            var json = JsonSerializer.Serialize(profileConfig, JsonOptions);
            Directory.CreateDirectory(Path.GetDirectoryName(generatedConfigPath)!);
            await File.WriteAllTextAsync(generatedConfigPath, json + Environment.NewLine, Encoding.UTF8);
            wroteAnyConfig = true;
        }

        var generatedSampleProfile = Path.Join(generatedPackageRoot, "configs", "bootstrap.local.sample.json");
        if (wroteAnyConfig
            && !configsToMerge.Any(path =>
                Path.GetFileName(path).Equals("bootstrap.local.sample.json", StringComparison.OrdinalIgnoreCase))
            && File.Exists(generatedSampleProfile))
        {
            File.Delete(generatedSampleProfile);
        }
    }

    private static void ApplyGeneratedPayloadMetadata(BootstrapConfig profileConfig, BootstrapConfig generatedConfig)
    {
        profileConfig.Artifacts = generatedConfig.Artifacts;
        profileConfig.Sql.ArtifactVersionOverrides = generatedConfig.Sql.ArtifactVersionOverrides;
        profileConfig.Sql.ArtifactVersionVariableOverrides = generatedConfig.Sql.ArtifactVersionVariableOverrides;

        var generatedHostAgentSettings = generatedConfig.HostAgent.AppSettings?["HostAgent"] as JsonObject;
        var profileHostAgentSettings = profileConfig.HostAgent.AppSettings?["HostAgent"] as JsonObject;
        if (generatedHostAgentSettings is null || profileHostAgentSettings is null)
        {
            return;
        }

        if (generatedHostAgentSettings.TryGetPropertyValue("Version", out var generatedVersion)
            && generatedVersion is not null)
        {
            profileHostAgentSettings["Version"] = generatedVersion.DeepClone();
        }
    }

    private static void MergeCurrentPackageData(
        string currentPackageRoot,
        string generatedPackageRoot,
        string currentConfigPath)
    {
        PreserveCurrentPackageDirectory(
            currentPackageRoot,
            generatedPackageRoot,
            "payload");
        PreserveCurrentPackageDirectory(
            currentPackageRoot,
            generatedPackageRoot,
            "sql");
        PreserveCurrentPackageDirectory(
            currentPackageRoot,
            generatedPackageRoot,
            Path.Join("data", "global", "artifacts"));
        MergeCurrentGlobalConfigObjectLibrary(
            currentPackageRoot,
            generatedPackageRoot,
            "host-configs");
        MergeCurrentGlobalConfigObjectLibrary(
            currentPackageRoot,
            generatedPackageRoot,
            "config-overlays");

        var generatedHostsRoot = Path.Join(generatedPackageRoot, "data", "hosts");
        var generatedSampleHostRoot = Path.Join(generatedHostsRoot, "bootstrap.local.sample");
        var activeConfigKey = Path.GetFileNameWithoutExtension(currentConfigPath);

        CopyCurrentHostData(currentPackageRoot, generatedPackageRoot, activeConfigKey);

        if (!string.IsNullOrWhiteSpace(activeConfigKey)
            && Directory.Exists(generatedSampleHostRoot)
            && !activeConfigKey.Equals("bootstrap.local.sample", StringComparison.OrdinalIgnoreCase))
        {
            CopyDirectoryRecursive(
                generatedSampleHostRoot,
                Path.Join(generatedHostsRoot, activeConfigKey));
        }
    }

    private static void PreserveCurrentPackageDirectory(
        string currentPackageRoot,
        string generatedPackageRoot,
        string relativeDirectory)
    {
        var currentRoot = Path.Join(currentPackageRoot, relativeDirectory);
        if (!Directory.Exists(currentRoot))
        {
            return;
        }

        CopyDirectoryRecursive(
            currentRoot,
            Path.Join(generatedPackageRoot, relativeDirectory),
            overwriteExistingFiles: false);
    }

    private static void MergeCurrentGlobalConfigObjectLibrary(
        string currentPackageRoot,
        string generatedPackageRoot,
        string libraryName)
    {
        var currentLibraryRoot = Path.Join(currentPackageRoot, "data", "global", libraryName);
        if (!Directory.Exists(currentLibraryRoot))
        {
            return;
        }

        CopyDirectoryRecursive(
            currentLibraryRoot,
            Path.Join(generatedPackageRoot, "data", "global", libraryName));
    }

    private static void CopyCurrentHostData(
        string currentPackageRoot,
        string generatedPackageRoot,
        string activeConfigKey)
    {
        var currentHostsRoot = Path.Join(currentPackageRoot, "data", "hosts");
        if (!Directory.Exists(currentHostsRoot))
        {
            return;
        }

        foreach (var currentHostRoot in Directory.EnumerateDirectories(currentHostsRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var configKey = Path.GetFileName(currentHostRoot);
            if (configKey.Equals("bootstrap.local.sample", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(activeConfigKey)
                    && configKey.Equals(activeConfigKey, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            CopyDirectoryRecursive(
                currentHostRoot,
                Path.Join(generatedPackageRoot, "data", "hosts", configKey));
        }
    }

    private static string ResolveGeneratedTemplateConfigPath(string generatedPackageRoot)
    {
        var candidates = new[]
        {
            Path.Join(generatedPackageRoot, "configs", "bootstrap.local.sample.json"),
            Path.Join(generatedPackageRoot, "bootstrap.local.sample.json")
        };

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Generated bootstrap config was not found.", candidates[0]);
    }

    private static string ResolveGeneratedCurrentConfigPath(
        string currentConfigPath,
        string currentPackageRoot,
        string generatedPackageRoot,
        string fallbackGeneratedConfigPath)
    {
        var currentFullPath = Path.GetFullPath(currentConfigPath);
        var currentRoot = Path.GetFullPath(currentPackageRoot);
        if (IsSameOrChildPath(currentRoot, currentFullPath))
        {
            var relativePath = Path.GetRelativePath(currentRoot, currentFullPath);
            return Path.GetFullPath(Path.Join(generatedPackageRoot, relativePath));
        }

        return fallbackGeneratedConfigPath;
    }

    private static IReadOnlyList<string> ResolveDeveloperSourceRoots(
        BootstrapConfig config,
        string payloadRoot,
        string configPath)
    {
        var configuredRoots = ParseDeveloperSourceRoots(config.DeveloperSource.SourceRoot)
            .Select(Path.GetFullPath)
            .Where(root => File.Exists(Path.Join(root, "omp-components.json")))
            .ToList();

        if (configuredRoots.Any(IsOpenModulePlatformSourceRoot))
        {
            return configuredRoots;
        }

        var discoveredRoot = GetDeveloperSourceSearchStarts(payloadRoot, configPath)
            .SelectMany(EnumerateSelfAndParents)
            .FirstOrDefault(IsOpenModulePlatformSourceRoot);
        if (discoveredRoot is not null)
        {
            configuredRoots.Insert(0, discoveredRoot);
            return configuredRoots;
        }

        throw new DirectoryNotFoundException("Developer source roots must include an OpenModulePlatform source repository.");
    }

    private static string ResolvePrimaryDeveloperSourceRoot(
        BootstrapConfig config,
        string payloadRoot,
        string configPath)
    {
        var primaryRoot = ResolveDeveloperSourceRoots(config, payloadRoot, configPath)
            .FirstOrDefault(IsOpenModulePlatformSourceRoot);
        if (primaryRoot is not null)
        {
            return primaryRoot;
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

        var local = Path.Join(sourceRoot, "scripts", "deployment", "omp-suite.local.psd1");
        if (File.Exists(local))
        {
            return local;
        }

        var sample = Path.Join(sourceRoot, "scripts", "deployment", "omp-suite.config.sample.psd1");
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
            ? Path.Join(ResolveShortInstallerRefreshBaseRoot(config, sourceRoot), "PackageRefresh")
            : Path.GetFullPath(config.DeveloperSource.PackageOutputRoot);
        if (!PathOverlaps(configured, payloadRoot) && !RisksInstallerRefreshPathLimit(configured))
        {
            return configured;
        }

        return Path.Join(
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
        return Path.Join(
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
    {
        // The child segments are fixed repository-relative names; rooted values here would be source bugs, not user input.
        return File.Exists(Path.Join(path, "omp-components.json"))
            && File.Exists(Path.Join(path, "OpenModulePlatform.slnx"))
            && File.Exists(Path.Join(path, "scripts", "deployment", "package-hostagent-first.ps1"));
    }

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

    private static void CopyDirectoryRecursive(
        string source,
        string destination,
        bool overwriteExistingFiles = true)
    {
        Directory.CreateDirectory(destination);
        foreach (var relativeDirectory in Directory
            .EnumerateDirectories(source, "*", SearchOption.AllDirectories)
            .Select(directory => Path.GetRelativePath(source, directory)))
        {
            Directory.CreateDirectory(Path.Join(destination, relativeDirectory));
        }

        foreach (var relativeFile in Directory
            .EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(source, file)))
        {
            var target = Path.Join(destination, relativeFile);
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? destination);
            if (!overwriteExistingFiles && File.Exists(target))
            {
                continue;
            }

            File.Copy(Path.Join(source, relativeFile), target, overwrite: overwriteExistingFiles);
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
        var executable = Path.Join(packageRoot, "OpenModulePlatform.Bootstrapper.exe");
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

    private sealed class InstallerRefreshProgressForm : Form
    {
        private readonly Label _statusLabel = new()
        {
            AutoSize = true,
            Text = "Preparing installer refresh..."
        };

        private readonly TextBox _logBox = new()
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false
        };

        private readonly ProgressBar _progressBar = new()
        {
            Dock = DockStyle.Top,
            Height = 18,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30
        };

        public InstallerRefreshProgressForm(string logPath)
        {
            ExitCode = 1;
            Text = "OpenModulePlatform installer refresh";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(760, 520);
            Size = new Size(920, 640);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(12)
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            root.Controls.Add(new Label
            {
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Text = "Creating updated installer package"
            }, 0, 0);

            var statusPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0, 10, 0, 8)
            };
            statusPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            statusPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            statusPanel.Controls.Add(_statusLabel, 0, 0);
            statusPanel.Controls.Add(_progressBar, 0, 1);
            root.Controls.Add(statusPanel, 0, 1);

            root.Controls.Add(_logBox, 0, 2);
            root.Controls.Add(new Label
            {
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Text = $"Log file: {logPath}"
            }, 0, 3);

            Controls.Add(root);
        }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int ExitCode { get; set; }

        public void SetStatus(string text)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SetStatus(text)));
                return;
            }

            _statusLabel.Text = text;
        }

        public void AppendLogLine(string line)
        {
            if (IsDisposed)
            {
                return;
            }

            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => AppendLogLine(line)));
                return;
            }

            _logBox.AppendText(line + Environment.NewLine);
        }
    }

    private sealed class InstallerRefreshProgressWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly Action<string> _appendLine;
        private readonly StringBuilder _line = new();
        private readonly object _gate = new();

        public InstallerRefreshProgressWriter(TextWriter inner, Action<string> appendLine)
        {
            _inner = inner;
            _appendLine = appendLine;
        }

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            lock (_gate)
            {
                _inner.Write(value);
                if (value == '\r')
                {
                    return;
                }

                if (value == '\n')
                {
                    FlushBufferedLine();
                    return;
                }

                _line.Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            foreach (var character in value)
            {
                Write(character);
            }
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            Write(Environment.NewLine);
        }

        public override void Flush()
        {
            lock (_gate)
            {
                _inner.Flush();
            }
        }

        public override async Task FlushAsync()
        {
            string? pendingLine = null;
            lock (_gate)
            {
                if (_line.Length > 0)
                {
                    pendingLine = _line.ToString();
                    _line.Clear();
                }
            }

            if (pendingLine is not null)
            {
                _appendLine(pendingLine);
            }

            await _inner.FlushAsync();
        }

        private void FlushBufferedLine()
        {
            var text = _line.ToString();
            _line.Clear();
            _appendLine(text);
        }
    }
}

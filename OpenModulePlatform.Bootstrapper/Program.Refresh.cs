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
    private const int InstallerRefreshPackageLockWaitSeconds = 120;
    private const int InstallerRefreshPathSafetyMargin = 240;
    private const int InstallerRefreshExpectedDeepSuffixLength = 190;
    private const int DeveloperSourceGitPullTimeoutSeconds = 120;
    private const int DeveloperSourcePullLockTimeoutSeconds = 180;
    private const string DeveloperSourcePullMutexName = @"Local\OpenModulePlatform.Bootstrapper.DeveloperSourcePull";

    private static async Task<int> RunInstallerPackageRefreshAsync(CliOptions cli)
    {
        if (TryRestartRefreshFromRunnerCopy(cli, out var runnerExitCode))
        {
            return runnerExitCode;
        }

        var logPath = string.IsNullOrWhiteSpace(cli.LogFilePath)
            ? Path.Join(
                Path.GetTempPath(),
                "omp-installer-refresh-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".log")
            : Path.GetFullPath(cli.LogFilePath);

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
            Console.WriteLine("Installer package refresh completed.");
            return 0;
        }
        catch (JsonException ex)
        {
            // Detached refresh process boundary: write full diagnostics to the log and return a failure code to the launcher.
            Console.Error.WriteLine("Installer package refresh failed.");
            Console.Error.WriteLine(ex);
            await log.FlushAsync();
            ShowRefreshFailureDialogForGuiFlow(cli, logPath, ex);
            return 1;
        }
        catch (SystemException ex)
        {
            // Detached refresh process boundary: write full diagnostics to the log and return a failure code to the launcher.
            Console.Error.WriteLine("Installer package refresh failed.");
            Console.Error.WriteLine(ex);
            await log.FlushAsync();
            ShowRefreshFailureDialogForGuiFlow(cli, logPath, ex);
            return 1;
        }
    }

    private static void ShowRefreshFailureDialogForGuiFlow(CliOptions cli, string logPath, Exception ex)
    {
        // Only the GUI-initiated flow (--restart-gui) may block on a dialog;
        // a scripted refresh must fail with an exit code, never hang on UI.
        if (!cli.RestartGui || !OperatingSystem.IsWindows() || !Environment.UserInteractive)
        {
            return;
        }

        MessageBox.Show(
            $"Installer package refresh failed. Details were written to:{Environment.NewLine}{logPath}{Environment.NewLine}{Environment.NewLine}{ex.Message}",
            "OpenModulePlatform installer",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private static bool TryRestartRefreshFromRunnerCopy(CliOptions cli, out int exitCode)
    {
        exitCode = 1;
        var currentExecutable = Environment.ProcessPath;
        if (currentExecutable is null || string.IsNullOrWhiteSpace(cli.ConfigPath))
        {
            return false;
        }

        var configPath = Path.GetFullPath(cli.ConfigPath);
        var payloadRoot = ResolvePayloadRoot(cli, configPath);
        if (!IsSameOrParentPath(payloadRoot, currentExecutable))
        {
            return false;
        }

        // The refresh replaces the payload root; a process running from inside
        // it keeps the directory locked, so the replace step would fail with
        // access denied. Hand the work to a copy in the temp directory (the
        // same pattern the GUI uses) and let this process exit so the copy can
        // swap the package. The runner inherits --log-file so callers can
        // follow a deterministic log; without one, a path is generated here
        // and printed before this process exits.
        CleanupStaleRunnerDirectories();

        var runnerRoot = Path.Join(
            Path.GetTempPath(),
            "omp-installer-refresh-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runnerRoot);
        CopyInstallerRunnerFiles(currentExecutable, runnerRoot);

        var logPath = string.IsNullOrWhiteSpace(cli.LogFilePath)
            ? Path.Join(
                Path.GetTempPath(),
                "omp-installer-refresh-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".log")
            : Path.GetFullPath(cli.LogFilePath);

        var startInfo = new ProcessStartInfo(Path.Join(runnerRoot, Path.GetFileName(currentExecutable)))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = runnerRoot,
            ArgumentList =
            {
                "--refresh-installer-package",
                "--config",
                configPath,
                "--payload-root",
                payloadRoot,
                "--parent-process-id",
                Environment.ProcessId.ToString(),
                "--log-file",
                logPath
            }
        };

        if (cli.RestartGui)
        {
            startInfo.ArgumentList.Add("--restart-gui");
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the installer package refresh runner process.");

        Console.WriteLine("Installer package refresh continues in a detached runner process.");
        Console.WriteLine($"RunnerPid: {process.Id}");
        Console.WriteLine($"LogFile:   {logPath}");
        Console.WriteLine("The runner waits for this process to exit before replacing the package.");
        exitCode = 0;
        return true;
    }

    internal static void CopyInstallerRunnerFiles(string currentExecutable, string runnerRoot)
    {
        var executableDirectory = Path.GetDirectoryName(currentExecutable)
            ?? throw new InvalidOperationException("Could not resolve the running installer directory.");
        var baseName = Path.GetFileNameWithoutExtension(currentExecutable);
        var hasFrameworkDependentFiles =
            File.Exists(Path.Join(executableDirectory, baseName + ".deps.json"))
            || File.Exists(Path.Join(executableDirectory, baseName + ".runtimeconfig.json"));

        if (!hasFrameworkDependentFiles)
        {
            File.Copy(currentExecutable, Path.Join(runnerRoot, Path.GetFileName(currentExecutable)), overwrite: true);
            return;
        }

        // Copy only the Bootstrapper's own runtime files (top-level .exe/.dll/
        // .json next to it), NOT the whole package tree. The executable lives
        // at the package root, so a recursive copy dragged data\global\
        // artifacts, sql and tools - potentially gigabytes - into %TEMP% on
        // every refresh, and nothing ever cleaned it (R3-G1).
        foreach (var file in Directory.EnumerateFiles(executableDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            File.Copy(file, Path.Join(runnerRoot, Path.GetFileName(file)), overwrite: true);
        }
    }

    // Best-effort removal of runner copies left by earlier refreshes; the
    // runner process cannot delete its own directory while running, so the next
    // refresh cleans up the previous ones (R3-G1).
    private static void CleanupStaleRunnerDirectories()
    {
        try
        {
            var tempRoot = Path.GetTempPath();
            foreach (var directory in Directory.EnumerateDirectories(tempRoot, "omp-installer-refresh-runner-*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still in use by a running runner, or locked; skip it.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp not enumerable; nothing to clean.
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
            catch (Exception ex)
            {
                // R11-B2. Progress UI boundary: keep the background refresh failure visible
                // while preserving the detailed log file. This handler is subscribed to
                // Shown as an async lambda, so it is an async void method: an exception
                // outside the filter does not merely skip this dialog, it takes the whole
                // installer down while a package refresh is half-written. Same reasoning as
                // the GUI action boundary -- the widest filter is the correct one here.
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

        // R5-G2: check for package locks BEFORE the multi-minute rebuild. The
        // blocker check used to run only after the build, at the file-swap, so
        // every attempt paid the full build cost then failed on a locked file.
        // This fails fast (naming the blocking process/PID) - and nudges an idle
        // GUI to close (R5-G4) - so the build only starts once the swap can win.
        WaitForPackageProcessesToExit(payloadRoot);

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

        var sourceRoots = ResolveDeveloperSourceRoots(config, payloadRoot, configPath);
        Console.WriteLine("Source repository updates:");
        PullDeveloperSourceRepositories(
            sourceRoots,
            line => Console.WriteLine(line),
            throwOnFailure: true);
        Console.WriteLine();

        foreach (var root in sourceRoots)
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
        WaitForPackageProcessesToExit(payloadRoot);
        ReplaceDirectory(generatedPackageRoot, payloadRoot);

        var destinationLogPath = Path.Join(payloadRoot, "installer-refresh.log");
        Console.Out.Flush();
        File.Copy(logPath, destinationLogPath, overwrite: true);

        if (cli.RestartGui)
        {
            StartInstallerGui(payloadRoot);
        }
    }

    // Builds the PSModulePath handed to Windows PowerShell 5.1 children: the
    // machine-scope path (so 5.1 finds its own Microsoft.PowerShell.* modules
    // instead of pwsh 7's incompatible ones), with the current user's Windows
    // PowerShell module folder prepended. Replacing the whole path with only the
    // machine scope dropped user-scope modules (Install-Module -Scope CurrentUser),
    // breaking sibling-repo hooks that import them (R4-G9).
    internal static string BuildWindowsPowerShellModulePath()
    {
        var machineModulePath = Environment.GetEnvironmentVariable(
            "PSModulePath",
            EnvironmentVariableTarget.Machine) ?? string.Empty;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var userScope = string.IsNullOrWhiteSpace(userProfile)
            ? string.Empty
            : Path.Combine(userProfile, "WindowsPowerShell", "Modules");

        if (string.IsNullOrWhiteSpace(userScope))
        {
            return machineModulePath;
        }

        return string.IsNullOrWhiteSpace(machineModulePath)
            ? userScope
            : $"{userScope};{machineModulePath}";
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

        if (string.Equals(fileName, "powershell", StringComparison.OrdinalIgnoreCase))
        {
            // When the refresh is launched from a pwsh 7 session (the CLI
            // wrapper), the inherited PSModulePath puts pwsh's module folders
            // first and Windows PowerShell 5.1 then fails to load its own
            // Microsoft.PowerShell.* modules ("running scripts is disabled").
            // Give 5.1 children the machine-scope path instead of the
            // launcher's, and skip the policy check outright: these are our
            // own repo scripts, not downloaded content.
            var modulePath = BuildWindowsPowerShellModulePath();
            if (!string.IsNullOrWhiteSpace(modulePath))
            {
                info.Environment["PSModulePath"] = modulePath;
            }

            info.ArgumentList.Add("-ExecutionPolicy");
            info.ArgumentList.Add("Bypass");
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
        // Sample templates are never operative configs: merging one would
        // round-trip it through BootstrapConfig (stamping the build machine's
        // hostAgent identity into it) and its presence in the merge set blocks
        // the sample cleanup below, so the installer later sees two configs
        // matching the same machine.
        var configsToMerge = Directory.Exists(currentConfigRoot)
            ? Directory.EnumerateFiles(currentConfigRoot, "*.json", SearchOption.TopDirectoryOnly)
                .Where(static path => !Path.GetFileName(path).EndsWith(".sample.json", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [currentConfigPath];

        // The operative config may be a host profile outside the package
        // (Universal hosts layout); once the package has its own configs
        // folder the enumeration above would silently drop the profile from
        // the merge, so its artifact versions and SQL variable overrides
        // would never be refreshed.
        if (!configsToMerge.Any(path => string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(currentConfigPath),
                StringComparison.OrdinalIgnoreCase)))
        {
            configsToMerge = [.. configsToMerge, currentConfigPath];
        }

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
            var json = JsonSerializer.Serialize(profileConfig, JsonOptions);
            var fullConfigPath = Path.GetFullPath(configPath);

            // A host-profile config outside the package root is the operative
            // config for later installer runs. Without writing the merge back,
            // the profile keeps stale artifact versions and SQL variable
            // overrides forever - and those variables feed the module
            // definition seeds, which then resurrect and re-default
            // long-retired versions on every apply. The profile is
            // hand-maintained, so only the generated fields are merged into
            // the original JSON - a full BootstrapConfig round-trip would
            // drop properties the model does not carry (schema, profile
            // metadata). It gets no copy inside the generated package, where
            // the fallback path is the sample template.
            if (!IsSameOrParentPath(currentPackageRoot, fullConfigPath))
            {
                await WriteGeneratedPayloadMetadataIntoJsonFileAsync(fullConfigPath, generatedConfig);
                Console.WriteLine($"Updated host profile config with generated artifact versions: {fullConfigPath}");
                wroteAnyConfig = true;
                continue;
            }

            var generatedConfigPath = ResolveGeneratedCurrentConfigPath(
                configPath,
                currentPackageRoot,
                generatedPackageRoot,
                generatedTemplateConfigPath);
            Directory.CreateDirectory(Path.GetDirectoryName(generatedConfigPath)!);
            // R5S-G2/R5S-G3: this config is written INTO the redistributable
            // installer package (and is later copied into its .backup-* snapshots
            // during the swap). Strip GUI-entered service/SQL/app-pool passwords
            // so plaintext production secrets never leave the build machine; the
            // target operator supplies them at install time and they are
            // DPAPI-protected on the target (the same model as the HostAgent
            // account credential). A host-profile config kept OUTSIDE the package
            // takes the metadata-only branch above and retains its local secrets.
            var redistributableJson = RedactRedistributablePackageConfigSecrets(json);
            await AtomicJsonFile.WriteAsync(generatedConfigPath, redistributableJson + Environment.NewLine, Encoding.UTF8);
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

    // R5S-G2/R5S-G3: secret bootstrap-config fields (camelCase, matching the Web
    // JSON naming the installer serializes with) that must never be persisted
    // into a redistributable package.
    private static readonly string[] RedistributableHostAgentSecretProperties =
    [
        "serviceAccountPassword",
        "serviceAppPassword",
        "iisAppPoolPassword"
    ];

    private static string RedactRedistributablePackageConfigSecrets(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root)
        {
            return json;
        }

        if (root["sql"] is JsonObject sql && sql.ContainsKey("password"))
        {
            sql["password"] = string.Empty;
        }

        if (root["hostAgent"] is JsonObject hostAgent)
        {
            foreach (var property in RedistributableHostAgentSecretProperties.Where(hostAgent.ContainsKey))
            {
                hostAgent[property] = string.Empty;
            }
        }

        return root.ToJsonString(JsonOptions);
    }

    private static async Task WriteSyncedArtifactTargetsIntoConfigAsync(string configPath, BootstrapConfig config)
    {
        // Persist ONLY the artifacts array into the original JSON document, so every
        // hand-maintained property survives (same merge approach as
        // WriteGeneratedPayloadMetadataIntoJsonFileAsync below). Rationale: the
        // --refresh-and-stage-package fast path normalized artifact targets in memory
        // only, while --check-developer-source-status re-reads the tracked config —
        // so the same UPDATE rows survived every successful build+import until the
        // heavyweight --refresh-installer-package happened to rewrite the file
        // (operator-verified 2026-08-31: 17 stale rows across two green runs).
        var originalNode = JsonNode.Parse(
                await File.ReadAllTextAsync(configPath),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
            as JsonObject
            ?? throw new InvalidOperationException($"Host profile config is not a JSON object: {configPath}");

        originalNode["artifacts"] = JsonSerializer.SerializeToNode(config.Artifacts, JsonOptions);
        var json = originalNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        // UTF8Encoding(false): plain Encoding.UTF8 EMITS a BOM, which the original
        // hand-maintained configs do not carry - the first rewrite silently added
        // EF BB BF (measured 2026-08-31 on linus_hemma/bootstrap.json). BOM-free
        // matches how the files are authored; System.Text.Json reads both.
        await AtomicJsonFile.WriteAsync(configPath, json + Environment.NewLine, new UTF8Encoding(false));
    }

    private static async Task WriteGeneratedPayloadMetadataIntoJsonFileAsync(string configPath, BootstrapConfig generatedConfig)
    {
        // Same field set as ApplyGeneratedPayloadMetadata, but merged into the
        // original JSON document so hand-maintained properties survive.
        var originalNode = JsonNode.Parse(
                await File.ReadAllTextAsync(configPath),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
            as JsonObject
            ?? throw new InvalidOperationException($"Host profile config is not a JSON object: {configPath}");

        originalNode["artifacts"] = JsonSerializer.SerializeToNode(generatedConfig.Artifacts, JsonOptions);
        if (originalNode["sql"] is not JsonObject sqlNode)
        {
            sqlNode = new JsonObject();
            originalNode["sql"] = sqlNode;
        }

        sqlNode["artifactVersionOverrides"] = JsonSerializer.SerializeToNode(generatedConfig.Sql.ArtifactVersionOverrides, JsonOptions);
        sqlNode["artifactVersionVariableOverrides"] = JsonSerializer.SerializeToNode(generatedConfig.Sql.ArtifactVersionVariableOverrides, JsonOptions);

        if (generatedConfig.HostAgent.AppSettings?["HostAgent"] is JsonObject generatedHostAgentSettings
            && generatedHostAgentSettings.TryGetPropertyValue("Version", out var generatedVersion)
            && generatedVersion is not null
            && originalNode["hostAgent"]?["appSettings"]?["HostAgent"] is JsonObject profileHostAgentSettings)
        {
            profileHostAgentSettings["Version"] = generatedVersion.DeepClone();
        }

        var json = originalNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        // UTF8Encoding(false): plain Encoding.UTF8 EMITS a BOM, which the original
        // hand-maintained configs do not carry - the first rewrite silently added
        // EF BB BF (measured 2026-08-31 on linus_hemma/bootstrap.json). BOM-free
        // matches how the files are authored; System.Text.Json reads both.
        await AtomicJsonFile.WriteAsync(configPath, json + Environment.NewLine, new UTF8Encoding(false));
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
        MergeCurrentGlobalConfigObjectLibrary(
            currentPackageRoot,
            generatedPackageRoot,
            "widgets");
        MergeCurrentGlobalConfigObjectLibrary(
            currentPackageRoot,
            generatedPackageRoot,
            "widget-data");

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

        var local = Path.Join(sourceRoot, "scripts", "deployment", "hostagent-first.local.psd1");
        if (File.Exists(local))
        {
            return local;
        }

        var sample = Path.Join(sourceRoot, "scripts", "deployment", "hostagent-first.config.sample.psd1");
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

    private static int PullDeveloperSourceRepositories(
        IReadOnlyList<string> sourceRoots,
        Action<string> writeLine,
        Action<string>? progress = null,
        bool throwOnFailure = false)
    {
        var warnings = 0;
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var pullLock = TryAcquireDeveloperSourcePullLock(writeLine, progress, throwOnFailure);
        if (pullLock is null)
        {
            return 1;
        }

        foreach (var sourceRoot in sourceRoots.Select(Path.GetFullPath).Where(emitted.Add))
        {
            var displayName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceRoot));
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = sourceRoot;
            }

            if (!IsGitWorkTreeRoot(sourceRoot))
            {
                writeLine($"  SKIP    {displayName}: source root is not a Git worktree ({sourceRoot}).");
                continue;
            }

            progress?.Invoke($"Updating source repository {displayName}...");
            ProcessResult result;
            try
            {
                result = UpdateDeveloperSourceRepository(sourceRoot);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                result = new ProcessResult(-1, string.Empty, "Could not start git.exe: " + ex.Message);
            }

            var output = NormalizeProcessOutput(result.StdOut, result.StdErr);
            if (result.ExitCode == 0)
            {
                writeLine($"  OK      {displayName}: {SummarizeProcessOutput(output, "already up to date")}");
                continue;
            }

            var message = $"  WARN    {displayName}: git fast-forward update failed with exit code {result.ExitCode}: {SummarizeProcessOutput(output, "no output")}";
            if (throwOnFailure)
            {
                throw new InvalidOperationException(
                    $"Could not update source repository '{sourceRoot}' before refreshing installer objects. Resolve the Git state manually and run refresh again. {NormalizeWhitespace(output)}");
            }

            writeLine(message);
            warnings++;
        }

        return warnings;
    }

    private static ProcessResult UpdateDeveloperSourceRepository(string sourceRoot)
    {
        var branchResult = RunProcess(
            "git",
            ["-C", sourceRoot, "branch", "--show-current"],
            throwOnFailure: false,
            workingDirectory: sourceRoot,
            timeout: TimeSpan.FromSeconds(DeveloperSourceGitPullTimeoutSeconds));
        if (branchResult.ExitCode != 0)
        {
            return branchResult;
        }

        var branchName = GetFirstProcessOutputLine(branchResult);
        if (string.IsNullOrWhiteSpace(branchName))
        {
            return new ProcessResult(1, string.Empty, "Repository is in detached HEAD state; cannot determine a single branch to fast-forward.");
        }

        var remoteName = GetSingleGitConfigValue(sourceRoot, $"branch.{branchName}.remote");
        var mergeRefs = GetGitConfigValues(sourceRoot, $"branch.{branchName}.merge");
        if (string.IsNullOrWhiteSpace(remoteName) || mergeRefs.Count != 1)
        {
            return new ProcessResult(
                1,
                string.Empty,
                $"Branch '{branchName}' must have exactly one upstream remote and merge ref configured for installer refresh.");
        }

        const string headsPrefix = "refs/heads/";
        var mergeRef = mergeRefs[0];
        if (!mergeRef.StartsWith(headsPrefix, StringComparison.Ordinal))
        {
            return new ProcessResult(1, string.Empty, $"Branch '{branchName}' upstream merge ref '{mergeRef}' is not a branch ref.");
        }

        var upstreamBranchName = mergeRef[headsPrefix.Length..];
        var upstreamTrackingRef = $"refs/remotes/{remoteName}/{upstreamBranchName}";
        var fetchResult = RunProcess(
            "git",
            ["-C", sourceRoot, "fetch", "--prune", remoteName, $"+{mergeRef}:{upstreamTrackingRef}"],
            throwOnFailure: false,
            workingDirectory: sourceRoot,
            timeout: TimeSpan.FromSeconds(DeveloperSourceGitPullTimeoutSeconds));
        if (fetchResult.ExitCode != 0)
        {
            return fetchResult;
        }

        var mergeResult = RunProcess(
            "git",
            ["-C", sourceRoot, "merge", "--ff-only", upstreamTrackingRef],
            throwOnFailure: false,
            workingDirectory: sourceRoot,
            timeout: TimeSpan.FromSeconds(DeveloperSourceGitPullTimeoutSeconds));

        return new ProcessResult(
            mergeResult.ExitCode,
            NormalizeProcessOutput(fetchResult.StdOut, mergeResult.StdOut),
            NormalizeProcessOutput(fetchResult.StdErr, mergeResult.StdErr));
    }

    private static string GetSingleGitConfigValue(string sourceRoot, string key)
    {
        var values = GetGitConfigValues(sourceRoot, key);
        return values.Count == 1 ? values[0] : string.Empty;
    }

    private static IReadOnlyList<string> GetGitConfigValues(string sourceRoot, string key)
    {
        var result = RunProcess(
            "git",
            ["-C", sourceRoot, "config", "--get-all", key],
            throwOnFailure: false,
            workingDirectory: sourceRoot,
            timeout: TimeSpan.FromSeconds(DeveloperSourceGitPullTimeoutSeconds));
        if (result.ExitCode != 0)
        {
            return [];
        }

        return result.StdOut
            .Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
    }

    private static string GetFirstProcessOutputLine(ProcessResult result)
        => NormalizeProcessOutput(result.StdOut, result.StdErr)
            .Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;

    private static IDisposable? TryAcquireDeveloperSourcePullLock(
        Action<string> writeLine,
        Action<string>? progress,
        bool throwOnFailure)
    {
        progress?.Invoke("Waiting for source repository update lock...");
        // The returned handle owns and later disposes the mutex; a local using would release the lock
        // before the protected repository update has completed.
        var handle = new MutexReleaseHandle(new Mutex(initiallyOwned: false, DeveloperSourcePullMutexName));
        try
        {
            if (handle.WaitOne(TimeSpan.FromSeconds(DeveloperSourcePullLockTimeoutSeconds)))
            {
                return handle;
            }

            handle.Dispose();
            var message = $"Another OpenModulePlatform installer source repository update is already running. Try again when that operation has completed. Waited {DeveloperSourcePullLockTimeoutSeconds} seconds.";
            if (throwOnFailure)
            {
                throw new TimeoutException(message);
            }

            writeLine("  WARN    " + message);
            return null;
        }
        catch (AbandonedMutexException)
        {
            handle.MarkAcquired();
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private sealed class MutexReleaseHandle(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;
        private bool _disposed;
        private bool _ownsMutex;

        public bool WaitOne(TimeSpan timeout)
        {
            var mutexToWait = _mutex ?? throw new ObjectDisposedException(nameof(MutexReleaseHandle));
            _ownsMutex = mutexToWait.WaitOne(timeout);
            return _ownsMutex;
        }

        public void MarkAcquired()
            => _ownsMutex = true;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var mutexToDispose = _mutex;
            _mutex = null;
            if (mutexToDispose is null)
            {
                return;
            }

            using (mutexToDispose)
            {
                if (_ownsMutex)
                {
                    mutexToDispose.ReleaseMutex();
                }
            }
        }
    }

    private static bool IsGitWorkTreeRoot(string sourceRoot)
        => Directory.Exists(sourceRoot)
           && (Directory.Exists(Path.Join(sourceRoot, ".git"))
               || File.Exists(Path.Join(sourceRoot, ".git")));

    private static string NormalizeProcessOutput(string stdout, string stderr)
        => string.Join(
            Environment.NewLine,
            new[] { stdout, stderr }
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim()));

    private static string SummarizeProcessOutput(string output, string fallback)
    {
        var firstLine = output
            .Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLine) ? fallback : firstLine;
    }

    private static string NormalizeWhitespace(string value)
        => string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

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

    private static void WaitForPackageProcessesToExit(string packageRoot)
    {
        // An installer GUI (or any other process) started from the package
        // keeps the directory locked, so the swap below would fail with a raw
        // IOException that names no culprit. Wait a bounded time and then fail
        // with the offending processes named, so the operator knows to close
        // the installer window instead of guessing.
        var deadline = DateTime.UtcNow.AddSeconds(InstallerRefreshPackageLockWaitSeconds);
        while (true)
        {
            var blockers = Process.GetProcesses()
                .Where(process =>
                {
                    try
                    {
                        return process.Id != Environment.ProcessId
                            && process.MainModule?.FileName is { } fileName
                            && IsSameOrParentPath(packageRoot, fileName);
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        return false;
                    }
                })
                .ToArray();
            if (blockers.Length == 0)
            {
                return;
            }

            var blockerNames = string.Join(", ", blockers.Select(static process => $"{process.ProcessName} (PID {process.Id})"));
            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException(
                    "The installer package cannot be replaced while these processes run from it: " + blockerNames
                    + ". Close the installer window (or stop the processes) and run the refresh again.");
            }

            // R5-G4 (root cause of the deploy-lock incident): the manual installer
            // GUI launched from the package root keeps the directory locked for
            // its whole idle lifetime, blocking the file-swap. Politely ask any
            // blocker that owns a window (the idle GUI) to close so it releases
            // the lock. Console/service blockers own no window and are unaffected;
            // this is best-effort and the bounded wait/throw above still guards.
            foreach (var blocker in blockers)
            {
                TryCloseIdleGuiBlocker(blocker);
            }

            Console.WriteLine($"Waiting for processes running from the package to exit: {blockerNames}");
            Thread.Sleep(TimeSpan.FromSeconds(5));
        }
    }

    // R5-G4: best-effort WM_CLOSE to an idle GUI holding the package lock.
    // Process.CloseMainWindow posts WM_CLOSE only when the process owns a main
    // window, so a console or service blocker is left untouched.
    private static void TryCloseIdleGuiBlocker(Process blocker)
    {
        try
        {
            blocker.Refresh();
            if (blocker.HasExited || blocker.MainWindowHandle == IntPtr.Zero)
            {
                return;
            }

            blocker.CloseMainWindow();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // The process exited or its window is no longer accessible; the swap
            // proceeds once the lock clears, or the wait/throw reports the blocker.
        }
    }

    /// <summary>
    /// Swaps a freshly generated package root into place, keeping a rollback
    /// copy until the new destination has been PROVEN complete.
    ///
    /// The order here is the point. Until 2026-09-01 the stale-backup sweep ran
    /// first and the fresh backup was deleted the moment CopyDirectoryRecursive
    /// returned, so a copy that finished without throwing but left an incomplete
    /// destination took the last rollback copy with it. Verification now happens
    /// before anything is deleted, and the sweep happens after -- so at every
    /// moment where the destination is unproven, at least one backup exists.
    /// </summary>
    /// <param name="verifyDestination">
    /// Test seam. Returns true when the destination is a faithful copy of the
    /// source. Defaults to the deterministic file-set-and-size comparison in
    /// <see cref="DestinationMatchesSource"/>.
    /// </param>
    internal static void ReplaceDirectory(
        string source,
        string destination,
        Func<string, bool>? verifyDestination = null)
    {
        if (PathOverlaps(source, destination))
        {
            throw new InvalidOperationException("Generated package root must not overlap the destination package root.");
        }

        var destinationTrimmed = destination.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var backup = destinationTrimmed + ".backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        DeleteDirectoryIfExists(backup);

        var hadDestination = Directory.Exists(destination);
        if (hadDestination)
        {
            MoveDirectoryWithRetry(destination, backup);
        }

        try
        {
            CopyDirectoryRecursive(source, destination);

            // Verify BEFORE deleting anything. A copy that returned without
            // throwing is not the same thing as a complete package: a file
            // locked mid-copy or a disk that filled up can leave a destination
            // that looks plausible and boots into a half-old install.
            var verified = verifyDestination is null
                ? DestinationMatchesSource(source, destination)
                : verifyDestination(destination);

            if (!verified)
            {
                throw new InvalidOperationException(
                    "The replaced package root failed verification against the generated package; " +
                    "rolling back to the previous root.");
            }
        }
        catch
        {
            // The rollback must be as resilient as the forward move: a raw
            // Directory.Move/Delete here could itself fail on the same transient
            // AV/sync lock and leave the install root missing with the original
            // stranded under the backup name (R3-G4, R4-G1). Retry the delete so
            // the restore below can proceed.
            DeleteDirectoryWithRetry(destination);
            if (Directory.Exists(backup))
            {
                MoveDirectoryWithRetry(backup, destination);
            }

            throw;
        }

        // Only now is the destination trusted. Sweep the fresh backup and any
        // stale timestamped ones from earlier runs: each run used a new
        // timestamp and only cleaned its own name, so a backup that survived a
        // locked delete accumulated multi-GB copies beside the package root
        // until an operator noticed (R4-G11).
        DeleteDirectoryBestEffort(backup);
        CleanupStaleBackups(destinationTrimmed);
        DeleteDirectoryBestEffort(source);
    }

    /// <summary>
    /// Deterministic comparison of a copied package root against its source:
    /// same set of relative paths, same byte length per file.
    ///
    /// Deliberately not a hash comparison. The failure this guards against is a
    /// truncated or missing file, which length and set membership catch, and a
    /// package root is large enough that hashing every file would add minutes to
    /// every refresh for no additional coverage of that failure.
    /// </summary>
    internal static bool DestinationMatchesSource(string source, string destination)
    {
        if (!Directory.Exists(source) || !Directory.Exists(destination))
        {
            return false;
        }

        var expected = RelativeFileSizes(source);
        var actual = RelativeFileSizes(destination);

        if (expected.Count != actual.Count)
        {
            return false;
        }

        foreach (var entry in expected)
        {
            if (!actual.TryGetValue(entry.Key, out var size) || size != entry.Value)
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, long> RelativeFileSizes(string root)
    {
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            sizes[relative] = new FileInfo(file).Length;
        }

        return sizes;
    }

    private static void CleanupStaleBackups(string destinationTrimmed)
    {
        var parent = Path.GetDirectoryName(destinationTrimmed);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            return;
        }

        var prefix = Path.GetFileName(destinationTrimmed) + ".backup-";
        foreach (var directory in Directory.EnumerateDirectories(parent, prefix + "*"))
        {
            DeleteDirectoryBestEffort(directory);
        }
    }

    private static void MoveDirectoryWithRetry(string source, string destination)
    {
        // Antivirus scanners and file-sync engines take transient handles on
        // freshly written package files; a single failed rename should not
        // abort a refresh that already built a complete package.
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (IOException ex) when (attempt < maxAttempts)
            {
                Console.WriteLine($"Package directory is locked ({ex.Message.TrimEnd('.')}); retrying in 3 seconds (attempt {attempt}/{maxAttempts}).");
                Thread.Sleep(TimeSpan.FromSeconds(3));
            }
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

    private static void DeleteDirectoryWithRetry(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        // Same transient AV/sync lock class the moves retry against: a single
        // raw Delete in the rollback path could throw and prevent the restore,
        // leaving the install root missing (R4-G1).
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < maxAttempts)
            {
                Thread.Sleep(200 * attempt);
            }
        }
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

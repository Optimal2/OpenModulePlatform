using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.Installation;

public static partial class InstallationEngine
{
    internal static IEnumerable<string> EnumerateWindowsServiceNames()
    {
        var result = RunProcess(GetScPath(), ["query", "state=", "all"], throwOnFailure: false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"sc.exe failed with exit code {result.ExitCode} while listing Windows services: {result.StdOut}{result.StdErr}");
        }

        return result.StdOut
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(static rawLine => rawLine.Trim())
            .Where(static line => line.StartsWith("SERVICE_NAME:", StringComparison.OrdinalIgnoreCase))
            .Select(static line => line["SERVICE_NAME:".Length..].Trim())
            .Where(static serviceName => !string.IsNullOrWhiteSpace(serviceName));
    }

    internal static string GetWindowsServiceExecutablePath(string serviceName)
    {
        var result = RunProcess(GetScPath(), ["qc", serviceName], throwOnFailure: false);
        if (result.ExitCode != 0)
        {
            return string.Empty;
        }

        var line = result.StdOut
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(static rawLine => rawLine.Trim())
            .FirstOrDefault(static line => line.StartsWith("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase));
        if (line is not null)
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0)
            {
                return string.Empty;
            }

            return ExtractExecutablePath(line[(separator + 1)..]);
        }

        return string.Empty;
    }

    internal static string ExtractExecutablePath(string pathName)
    {
        var trimmed = pathName.Trim();
        if (trimmed.StartsWith("\"", StringComparison.Ordinal))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            if (endQuote > 1)
            {
                return Path.GetFullPath(trimmed[1..endQuote]);
            }
        }

        // An unquoted BINARY_PATH_NAME ends its executable at the first ".exe" that is
        // followed by whitespace or the end of the value, so a directory segment such as
        // "Tools.exe.d" earlier in the path does not truncate it.
        var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        while (exeIndex >= 0)
        {
            var end = exeIndex + ".exe".Length;
            if (end == trimmed.Length || char.IsWhiteSpace(trimmed[end]))
            {
                return Path.GetFullPath(trimmed[..end]);
            }

            exeIndex = trimmed.IndexOf(".exe", end, StringComparison.OrdinalIgnoreCase);
        }

        var firstToken = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstToken) ? string.Empty : Path.GetFullPath(firstToken);
    }

    internal static void DeleteWindowsService(string serviceName)
    {
        if (!ServiceExists(serviceName))
        {
            return;
        }

        StopService(serviceName);
        InstallOutput.Info($"  delete service {serviceName}");
        var result = RunProcess(GetScPath(), ["delete", serviceName], throwOnFailure: false);
        if (result.ExitCode != 0 && !IsScServiceNotFound(result))
        {
            throw new InvalidOperationException(
                $"sc.exe failed with exit code {result.ExitCode} while deleting Windows service '{serviceName}': {result.StdOut}{result.StdErr}");
        }
    }

    internal static bool IsScServiceNotFound(ProcessResult result)
    {
        var text = result.StdOut + Environment.NewLine + result.StdErr;
        return result.ExitCode == 1060
            || text.Contains("FAILED 1060", StringComparison.OrdinalIgnoreCase)
            || text.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
    }

    internal static JsonNode? GetJsonObjectProperty(JsonNode? node, string propertyName)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        return obj.FirstOrDefault(property => property.Key.Equals(propertyName, StringComparison.OrdinalIgnoreCase)).Value;
    }

    internal static string GetJsonStringProperty(JsonNode? node, string propertyName)
    {
        var value = GetJsonObjectProperty(node, propertyName);
        return value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)
            ? text.Trim()
            : string.Empty;
    }

    internal static int GetJsonIntProperty(JsonNode? node, string propertyName, int defaultValue)
    {
        var value = GetJsonObjectProperty(node, propertyName);
        if (value is not JsonValue jsonValue)
        {
            return defaultValue;
        }

        if (jsonValue.TryGetValue<int>(out var number))
        {
            return number;
        }

        return jsonValue.TryGetValue<string>(out var text)
            && int.TryParse(text, out var parsed)
            ? parsed
            : defaultValue;
    }

    internal static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static string Truncate(string value, int maxLength)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    internal static bool ServiceExists(string serviceName)
    {
        var result = RunProcess(GetScPath(), ["query", serviceName], throwOnFailure: false);
        return result.ExitCode == 0;
    }

    internal static bool HostAgentServiceWithPrefixExists(string serviceNamePrefix)
    {
        var prefix = ResolveHostAgentServiceNamePrefix(serviceNamePrefix);
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return false;
        }

        return EnumerateHostAgentWindowsServices(prefix, installPath: null).Count > 0;
    }

    internal static IReadOnlyList<WindowsServiceCandidate> EnumerateHostAgentWindowsServices(
        string serviceNamePrefix,
        string? installPath)
    {
        var prefixes = GetKnownHostAgentServiceNamePrefixes(serviceNamePrefix);
        return EnumerateWindowsServiceNames()
            .Select(serviceName => new WindowsServiceCandidate(
                serviceName,
                GetWindowsServiceExecutablePath(serviceName)))
            .Where(service =>
                IsKnownHostAgentServiceName(service.Name, prefixes)
                || IsHostAgentWindowsServiceExecutable(service.ExecutablePath, installPath))
            .OrderBy(service => service.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlySet<string> GetKnownHostAgentServiceNamePrefixes(string serviceNamePrefix)
    {
        var prefixes = new HashSet<string>(KnownHostAgentServiceNamePrefixes, StringComparer.OrdinalIgnoreCase);
        var prefix = ResolveHostAgentServiceNamePrefix(serviceNamePrefix);
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            prefixes.Add(prefix);
        }

        return prefixes;
    }

    internal static bool IsKnownHostAgentServiceName(string serviceName, IEnumerable<string> serviceNamePrefixes)
        // The independent alarm service shares the prefix but is not a HostAgent: its
        // presence must not make upgrade/complete skip installing a missing HostAgent.
        // Same rule as HostAgentSelfUpgradeService.IsHostAgentServiceName.
        => !serviceName.Equals("OMP.HostAgent.Sentinel", StringComparison.OrdinalIgnoreCase)
            && serviceNamePrefixes.Any(prefix =>
                serviceName.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || serviceName.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));

    internal static bool IsHostAgentWindowsServiceExecutable(string executablePath, string? installPath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        var fullExecutablePath = Path.GetFullPath(executablePath);
        if (!Path.GetFileName(fullExecutablePath).Equals(HostAgentWindowsServiceExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(installPath)
            || IsSameOrChildPath(installPath, fullExecutablePath);
    }

    internal static void StopService(string serviceName)
    {
        var state = QueryServiceState(serviceName);
        if (!state.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)
            && !state.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase)
            && !state.Contains("PAUSED", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        InstallOutput.Info($"> Stop service {serviceName}");
        RunProcess(GetScPath(), ["stop", serviceName], throwOnFailure: false);
        WaitForServiceState(serviceName, "STOPPED", TimeSpan.FromSeconds(ServiceStopTimeoutSeconds));
    }

    internal static void StartService(string serviceName)
    {
        InstallOutput.Info($"> Start service {serviceName}");
        RunProcess(GetScPath(), ["start", serviceName]);
    }

    internal static void CreateService(
        HostAgentInstallOptions hostAgent,
        HostAgentBootstrapServiceIdentity serviceIdentity,
        string executablePath,
        string serviceAccountPassword)
    {
        InstallOutput.Info($"> Create service {serviceIdentity.ServiceName}");
        var arguments = CreateServiceArguments("create", hostAgent, serviceIdentity, executablePath, serviceAccountPassword);
        RunProcess(GetScPath(), arguments);
        SetServiceDescription(hostAgent, serviceIdentity.ServiceName);
    }

    internal static void ConfigureService(
        HostAgentInstallOptions hostAgent,
        HostAgentBootstrapServiceIdentity serviceIdentity,
        string executablePath,
        string serviceAccountPassword)
    {
        InstallOutput.Info($"> Configure service {serviceIdentity.ServiceName}");
        var arguments = CreateServiceArguments("config", hostAgent, serviceIdentity, executablePath, serviceAccountPassword);
        RunProcess(GetScPath(), arguments);
        SetServiceDescription(hostAgent, serviceIdentity.ServiceName);
    }

    internal static string[] CreateServiceArguments(
        string verb,
        HostAgentInstallOptions hostAgent,
        HostAgentBootstrapServiceIdentity serviceIdentity,
        string executablePath,
        string serviceAccountPassword)
    {
        var arguments = new List<string>
        {
            verb,
            serviceIdentity.ServiceName,
            "binPath=",
            CreateHostAgentServiceBinaryPath(executablePath, serviceIdentity.ServiceName),
            "start=",
            "auto",
            "DisplayName=",
            serviceIdentity.DisplayName
        };

        if (!string.IsNullOrWhiteSpace(hostAgent.ServiceAccountName))
        {
            arguments.Add("obj=");
            arguments.Add(hostAgent.ServiceAccountName.Trim());

            if (!string.IsNullOrWhiteSpace(serviceAccountPassword))
            {
                arguments.Add("password=");
                arguments.Add(serviceAccountPassword);
            }
        }

        return [.. arguments];
    }

    internal static string CreateHostAgentServiceBinaryPath(string executablePath, string serviceName)
    {
        var quotedExecutablePath = "\"" + executablePath.Trim().Trim('"') + "\"";
        return string.IsNullOrWhiteSpace(serviceName)
            ? quotedExecutablePath
            : $"{quotedExecutablePath} --service-name={serviceName.Trim()}";
    }

    internal static void SetServiceDescription(HostAgentInstallOptions hostAgent, string serviceName)
    {
        if (!string.IsNullOrWhiteSpace(hostAgent.Description))
        {
            RunProcess(GetScPath(), ["description", serviceName, hostAgent.Description]);
        }
    }

    internal static string ResolveSystemServiceDisplayName(
        string configuredDisplayName,
        string serviceName,
        string? version = null)
    {
        var displayName = string.IsNullOrWhiteSpace(configuredDisplayName)
            ? serviceName.Trim()
            : configuredDisplayName.Trim();

        if (displayName.StartsWith("OMP", StringComparison.OrdinalIgnoreCase))
        {
            return AppendDisplayVersion(displayName, version);
        }

        if (displayName.StartsWith("OpenModulePlatform ", StringComparison.OrdinalIgnoreCase))
        {
            return AppendDisplayVersion("OMP " + displayName["OpenModulePlatform ".Length..], version);
        }

        return AppendDisplayVersion("OMP " + displayName, version);
    }

    internal static string AppendDisplayVersion(string displayName, string? version)
    {
        if (string.IsNullOrWhiteSpace(version)
            || displayName.EndsWith(" " + version.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return displayName;
        }

        return $"{displayName} {version.Trim()}";
    }

    internal static void WaitForServiceState(string serviceName, string expectedState, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (QueryServiceState(serviceName).Contains(expectedState, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Thread.Sleep(1000);
        }

        throw new TimeoutException($"Service '{serviceName}' did not reach state '{expectedState}' within {timeout.TotalSeconds:n0} seconds.");
    }

    internal static string QueryServiceState(string serviceName)
    {
        var result = RunProcess(GetScPath(), ["query", serviceName], throwOnFailure: false);
        return result.StdOut + Environment.NewLine + result.StdErr;
    }

    internal static ProcessResult RunProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        bool throwOnFailure = true,
        string? workingDirectory = null,
        TimeSpan? timeout = null)
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
            // A pwsh 7 launcher's PSModulePath breaks Windows PowerShell 5.1
            // children (5.1 resolves pwsh's incompatible core modules first).
            // Hand them the machine scope plus the user-scope module folder, and
            // skip the policy check for our own repo scripts. Mirrors
            // RunProcessStreaming in Program.Refresh.cs (R4-G9).
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
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var exited = true;
        if (timeout is { } maxWait)
        {
            exited = process.WaitForExit(GetProcessTimeoutMilliseconds(maxWait));
        }
        else
        {
            // R11-B4. Deliberately the overload, even for an unbounded wait. The argument
            // -- less version waits for the redirected streams to reach EOF as well as for
            // the process to exit, so a grandchild that inherited the handles hangs it
            // forever even though the process itself is long gone. The overload waits only
            // for exit; DrainRedirectedStreams below puts a bound on the EOF wait.
            process.WaitForExit(Timeout.Infinite);
        }

        if (!exited)
        {
            var timeoutMessage = $"{Path.GetFileName(fileName)} timed out after {timeout!.Value.TotalSeconds:n0} seconds.";
            try
            {
                // R11-B4. WaitForExit() with no argument also waits for the redirected
                // streams to reach EOF, and a grandchild that escaped the kill keeps them
                // open -- so the call meant to clean up after a timeout had no timeout of
                // its own. Bounded here; DrainRedirectedStreams below takes whatever
                // arrived either way.
                process.Kill(entireProcessTree: true);
                process.WaitForExit(GetProcessTimeoutMilliseconds(TimeSpan.FromSeconds(5)));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                var killFailureMessage = $"{timeoutMessage} Could not terminate the process: {ex.Message}";
                if (throwOnFailure)
                {
                    throw new TimeoutException(killFailureMessage, ex);
                }

                return new ProcessResult(-1, string.Empty, killFailureMessage);
            }

            var (timedOutStdout, timedOutStderr) =
                OmpProcessStreamDrain.Drain(stdoutTask, stderrTask);
            var timedOutError = string.IsNullOrWhiteSpace(timedOutStderr)
                ? timeoutMessage
                : timedOutStderr + Environment.NewLine + timeoutMessage;
            if (throwOnFailure)
            {
                throw new TimeoutException($"{timeoutMessage}: {timedOutStdout}{timedOutError}");
            }

            return new ProcessResult(-1, timedOutStdout, timedOutError);
        }

        // R11-B4. The shared bounded drain, so neither branch above can be left waiting on
        // a pipe a surviving grandchild is holding open. This matters most in the GUI: a
        // hang here leaves the operator watching a window that reports an action still in
        // progress when nothing is progressing, and R11-B3 warns before letting them close
        // out of it.
        var (stdout, stderr) = OmpProcessStreamDrain.Drain(stdoutTask, stderrTask);

        if (throwOnFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(fileName)} failed with exit code {process.ExitCode}: {stdout}{stderr}");
        }

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    internal static int GetProcessTimeoutMilliseconds(TimeSpan timeout)
    {
        if (timeout.TotalMilliseconds <= 0)
        {
            return 1;
        }

        if (timeout.TotalMilliseconds >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return (int)Math.Ceiling(timeout.TotalMilliseconds);
    }

    internal static string GetScPath()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return Path.Join(windows, "System32", "sc.exe");
    }

    internal static string CreateBackupPath(string installPath)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(installPath))
            ?? throw new InvalidOperationException($"Cannot resolve parent folder for {installPath}.");
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(installPath));
        // UTC plus a suffix: a local timestamp repeats during the autumn DST hour,
        // and CopyDirectory overwrites, so an existing backup must never be reused.
        var basePath = Path.Join(parent + "Backups",
            $"{name}-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}Z");
        var candidate = basePath;
        for (var suffix = 2; Directory.Exists(candidate) || File.Exists(candidate); suffix++)
        {
            candidate = $"{basePath}-{suffix}";
        }

        return candidate;
    }

    internal static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var relativeDirectory in Directory
            .EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories)
            .Select(directory => Path.GetRelativePath(sourceDirectory, directory)))
        {
            Directory.CreateDirectory(Path.Join(targetDirectory, relativeDirectory));
        }

        foreach (var relativeFile in Directory
            .EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(sourceDirectory, file)))
        {
            var target = Path.Join(targetDirectory, relativeFile);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Join(sourceDirectory, relativeFile), target, overwrite: true);
        }
    }

    internal static void RemoveRuntimeConfigurationFiles(string root)
    {
        var runtimeConfigurationFiles = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(file =>
            {
                var fileName = Path.GetFileName(file);
                return fileName.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase)
                    || (fileName.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase)
                        && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    || fileName.Equals("odv.site.config.js", StringComparison.OrdinalIgnoreCase);
            });

        foreach (var file in runtimeConfigurationFiles)
        {
            File.Delete(file);
        }
    }

    internal static string ResolvePath(string root, string path)
        => Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Join(root, path));

    internal static string ResolvePackageDataPath(string packageRoot, string path)
        => ResolvePath(packageRoot, path);

    internal static string ResolvePackageDataPath(string packageRoot, string configPath, string path)
    {
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        var candidate = EnumerateHostAndGlobalDataRoots(packageRoot, configPath)
            .Select(root => Path.GetFullPath(Path.Join(root, path)))
            .FirstOrDefault(candidate => File.Exists(candidate) || Directory.Exists(candidate));

        return candidate ?? ResolvePackageDataPath(packageRoot, path);
    }

    internal static string ResolvePackageModuleDefinitionsRoot(string packageRoot)
        => ResolvePath(packageRoot, Path.Join("data", "global", "module-definitions"));

    internal static string ResolvePackageArtifactsRoot(string packageRoot)
        => ResolvePath(packageRoot, Path.Join("data", "global", "artifacts"));

    internal static string ResolvePackageHostConfigurationsRoot(string packageRoot)
        => ResolvePath(packageRoot, Path.Join("data", "global", "host-configs"));

    internal static string ResolvePackageConfigOverlaysRoot(string packageRoot)
        => ResolvePath(packageRoot, Path.Join("data", "global", "config-overlays"));

    internal static string ResolvePackageWidgetsRoot(string packageRoot)
        => ResolvePath(packageRoot, Path.Join("data", "global", "widgets"));

    internal static string ResolvePackageWidgetDataRoot(string packageRoot)
        => ResolvePath(packageRoot, Path.Join("data", "global", "widget-data"));

    internal static IEnumerable<string> EnumerateHostAndGlobalDataRoots(string packageRoot, string configPath)
    {
        var configKey = Path.GetFileNameWithoutExtension(configPath);
        if (!string.IsNullOrWhiteSpace(configKey))
        {
            yield return Path.Join(packageRoot, "data", "hosts", configKey);
            yield return Path.Join(packageRoot, "data", "profiles", configKey);
        }

        yield return Path.Join(packageRoot, "data", "global");
    }

    internal static string CombineUnderRoot(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root.Trim());
        var fullPath = Path.GetFullPath(Path.Join(fullRoot, relative.Trim().Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSameOrChildPath(fullRoot, fullPath))
        {
            throw new InvalidOperationException($"Path '{relative}' escapes root path '{fullRoot}'.");
        }

        return fullPath;
    }

    /// <remarks>
    /// Delegates to the shared helper. This was one of three private copies, not all of which
    /// normalized their inputs, so a path with ".." segments could pass a containment check that
    /// was meant to stop exactly that (R8-P2-16..23).
    /// </remarks>
    internal static bool IsSameOrChildPath(string rootPath, string candidatePath)
        => OmpPathContainment.IsSameOrChildPath(rootPath, candidatePath);

    internal static void TryDeleteFileOrDirectory(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        else
        {
            TryDeleteDirectory(path);
        }
    }

    internal static void TryDeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private const int LockRetryAttempts = 5;

    private static readonly TimeSpan DefaultLockRetryDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Moves <paramref name="source"/> to <paramref name="destination"/>, retrying while a
    /// transient handle blocks the rename.
    /// </summary>
    /// <remarks>
    /// Antivirus scanners with real-time protection and file-sync engines open freshly written
    /// files without FileShare.Delete. While such a handle is open anywhere below a directory,
    /// Windows refuses to rename that directory: "Access to the path ... is denied", which .NET
    /// surfaces from a directory move as an IOException carrying ERROR_ACCESS_DENIED
    /// (HResult 0x80070005). UnauthorizedAccessException is retried too, so the retry does not
    /// depend on that mapping.
    /// </remarks>
    /// <param name="copyAsLastResort">
    /// When the final attempt still fails, copy the tree instead of moving it (a scanner still
    /// lets the files be read). The source is left for the caller's cleanup. The fallback never
    /// merges into an existing destination. A copy that fails part-way deletes its partial
    /// destination with retries before rethrowing; if even that fails, an error line names the
    /// leftover directory, because an add-missing-only pass would otherwise accept it as
    /// installed. A process killed mid-copy can still leave a partial destination behind.
    /// </param>
    /// <param name="retryDelay">Test seam. Defaults to 3 seconds.</param>
    internal static void MoveDirectoryWithRetry(
        string source,
        string destination,
        bool copyAsLastResort = false,
        TimeSpan? retryDelay = null)
    {
        var delay = retryDelay ?? DefaultLockRetryDelay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < LockRetryAttempts)
                {
                    InstallOutput.Info($"Package directory is locked ({ex.Message.TrimEnd('.')}); retrying in {delay.TotalSeconds:0.#} seconds (attempt {attempt}/{LockRetryAttempts}).");
                    Thread.Sleep(delay);
                    continue;
                }

                // Directory.Move is a single rename on the same volume, so a failed attempt never
                // leaves a partial destination behind. An existing destination therefore belongs
                // to someone else and must not be merged into.
                if (!copyAsLastResort
                    || !Directory.Exists(source)
                    || Directory.Exists(destination)
                    || File.Exists(destination))
                {
                    throw;
                }

                InstallOutput.Info($"Package directory is still locked ({ex.Message.TrimEnd('.')}); copying it instead of moving it.");
                try
                {
                    CopyDirectory(source, destination);
                }
                catch
                {
                    RemovePartialCopy(destination, delay);
                    throw;
                }

                return;
            }
        }
    }

    private static void RemovePartialCopy(string destination, TimeSpan delay)
    {
        try
        {
            DeleteFileOrDirectoryWithRetry(destination, delay);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The copy failure is the exception that propagates; this only has to make the
            // leftover impossible to miss.
            InstallOutput.Error($"Partially copied directory '{destination}' could not be removed ({ex.Message.TrimEnd('.')}). Delete it before the next install; otherwise it is treated as already installed.");
        }
    }

    /// <summary>
    /// Deletes a file or directory tree, retrying while a transient scanner handle blocks it.
    /// </summary>
    /// <param name="retryDelay">Test seam. Defaults to 3 seconds.</param>
    internal static void DeleteFileOrDirectoryWithRetry(string path, TimeSpan? retryDelay = null)
    {
        var delay = retryDelay ?? DefaultLockRetryDelay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                TryDeleteFileOrDirectory(path);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < LockRetryAttempts)
            {
                InstallOutput.Info($"'{path}' is locked ({ex.Message.TrimEnd('.')}); retrying delete in {delay.TotalSeconds:0.#} seconds (attempt {attempt}/{LockRetryAttempts}).");
                Thread.Sleep(delay);
            }
        }
    }

    /// <summary>
    /// Removes a temporary directory without letting a failure escape. A leftover staging
    /// directory is harmless; failing to remove it (for example while a scanner still holds one
    /// of its files) must neither fail an install that otherwise succeeded nor hide an exception
    /// that is already propagating.
    /// </summary>
    internal static void TryDeleteTemporaryDirectoryBestEffort(string path)
    {
        try
        {
            TryDeleteDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            InstallOutput.Info($"Could not delete temporary directory '{path}': {ex.Message}");
        }
    }

    internal static string NormalizePathForMatch(string path)
        => path.Replace('\\', '/').TrimStart('/').Trim();

    // Process-lifetime console bindings. See EnsureConsole: these are deliberately never
    // disposed, because Console holds them for as long as the process runs.
    /// <summary>
    /// Completes when <paramref name="task"/> has settled and never throws. A boundary that must
    /// survive every failure awaits this and then reads the task's state, so no catch clause is
    /// needed to observe a fault.
    /// </summary>
    internal static Task WhenSettledAsync(Task task)
        => task.ContinueWith(
            static _ => { },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>The failure a settled task ended with, or <c>null</c> when it ran to completion.</summary>
    internal static Exception? FailureOf(Task task)
        => task.Exception?.GetBaseException() ?? (task.IsCanceled ? new TaskCanceledException(task) : null);

    internal static string ConvertToSqlBracketName(string value)
        => "[" + value.Replace("]", "]]", StringComparison.Ordinal) + "]";

    internal static string ConvertToSqlUnicodeLiteral(string value)
        => "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    [GeneratedRegex(@"^\s*:r\s+(?<path>.+?)\s*$", RegexOptions.IgnoreCase)]
    internal static partial Regex SqlCmdIncludeRegex();

    [GeneratedRegex(@"(?im)^\s*USE\s+\[OpenModulePlatform\]\s*;?\s*$")]
    internal static partial Regex UseDatabaseRegex();

    [GeneratedRegex(@"(?im)^\s*GO(?:\s+(?<repeat>[0-9]+))?\s*(?:--.*)?$")]
    internal static partial Regex GoBatchRegex();

    [GeneratedRegex(@"(?m)^\s*DECLARE\s+@ArtifactVersion\s+nvarchar\(\d+\)\s*=\s*N'(?:''|[^'])*';\s*$")]
    internal static partial Regex ArtifactVersionDeclarationRegex();

    [GeneratedRegex(@"DECLARE\s+@BootstrapPortalAdminPrincipal\s+nvarchar\(\d+\)\s*=\s*N'(?:''|[^'])*';")]
    internal static partial Regex BootstrapPrincipalDeclarationRegex();

    [GeneratedRegex(@"DECLARE\s+@BootstrapPortalAdminPrincipalType\s+nvarchar\(\d+\)\s*=\s*N'(?:''|[^'])*';")]
    internal static partial Regex BootstrapPrincipalTypeDeclarationRegex();

    [GeneratedRegex(@"(?im)^\s*USE\s+(?:\[[^\]]+\]|[A-Za-z0-9_]+)\s*;?\s*$")]
    internal static partial Regex ModuleDefinitionUseDatabaseDirectiveRegex();

    [GeneratedRegex(@"(?is)\bDROP\s+(?:DATABASE|SCHEMA|TABLE)\b")]
    internal static partial Regex ModuleDefinitionDropObjectRegex();

    [GeneratedRegex(@"(?is)\bTRUNCATE\s+TABLE\b")]
    internal static partial Regex ModuleDefinitionTruncateTableRegex();

    [GeneratedRegex(@"(?is)\b(?:INSERT|UPDATE|DELETE|MERGE|CREATE|ALTER|DROP|TRUNCATE|EXEC(?:UTE)?|GRANT|REVOKE|DENY)\b")]
    internal static partial Regex ModuleDefinitionReadOnlyBlockedCommandRegex();

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
            : Path.Join(userProfile, "WindowsPowerShell", "Modules");

        if (string.IsNullOrWhiteSpace(userScope))
        {
            return machineModulePath;
        }

        return string.IsNullOrWhiteSpace(machineModulePath)
            ? userScope
            : $"{userScope};{machineModulePath}";
    }
}

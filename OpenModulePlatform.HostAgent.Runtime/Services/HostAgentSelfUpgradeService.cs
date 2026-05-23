using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed class HostAgentSelfUpgradeService
{
    private const int ScServiceNotFoundExitCode = 1060;

    private readonly IOptionsMonitor<HostAgentSettings> _settings;
    private readonly OmpHostArtifactRepository _repository;
    private readonly ArtifactProvisioner _provisioner;
    private readonly HostAgentProcessContext _process;
    private readonly HostAgentCredentialStoreService _credentialStore;
    private readonly ILogger<HostAgentSelfUpgradeService> _logger;

    public HostAgentSelfUpgradeService(
        IOptionsMonitor<HostAgentSettings> settings,
        OmpHostArtifactRepository repository,
        ArtifactProvisioner provisioner,
        HostAgentProcessContext process,
        HostAgentCredentialStoreService credentialStore,
        ILogger<HostAgentSelfUpgradeService> logger)
    {
        _settings = settings;
        _repository = repository;
        _provisioner = provisioner;
        _process = process;
        _credentialStore = credentialStore;
        _logger = logger;
    }

    public async Task CheckAndPrepareUpgradeAsync(
        string hostKey,
        Guid hostId,
        CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;
        if (!settings.SelfUpgrade.IsEnabled)
        {
            return;
        }

        var desired = await _repository.GetDesiredHostAgentUpgradeAsync(hostKey, cancellationToken);
        if (desired is null)
        {
            return;
        }

        if (IsCurrentVersion(desired))
        {
            return;
        }

        var provisionResult = await _provisioner.EnsureAsync(desired.ToArtifactDescriptor(), cancellationToken);
        await _repository.PublishResultAsync(desired.ToArtifactDescriptor(), provisionResult, cancellationToken);
        if (!provisionResult.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Desired HostAgent artifact '{desired.ArtifactId}' could not be provisioned: {provisionResult.ErrorMessage}");
        }

        var installPath = ResolveInstallPath(settings, desired);
        var serviceName = ResolveServiceName(settings, desired);
        if (string.Equals(serviceName, _process.ServiceName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "HostAgent self-upgrade requires a new versioned Windows service name; the resolved service name matches the current service.");
        }

        await _repository.PublishHostAgentRuntimeStateAsync(
            hostId,
            _process,
            _process.RuntimeMode,
            desired.ArtifactId,
            AppContext.BaseDirectory,
            isActive: true,
            $"Preparing HostAgent {desired.Version} as {serviceName}.",
            cancellationToken);

        StopServiceIfRunning(serviceName, settings.SelfUpgrade.TakeoverStopTimeoutSeconds);
        PrepareInstallDirectory(provisionResult.LocalPath, installPath, cancellationToken);
        var executablePath = FindHostAgentExecutable(installPath);
        WriteTakeoverSettings(settings, installPath, desired, serviceName);
        var resolvedSelfUpgradeSettings = await ResolveSelfUpgradeSettingsAsync(settings.SelfUpgrade, cancellationToken);
        EnsureService(
            serviceName,
            executablePath,
            CreateTakeoverArguments(serviceName, _process.ServiceName),
            resolvedSelfUpgradeSettings);

        if (settings.SelfUpgrade.StartPreparedService)
        {
            StartServiceIfStopped(serviceName, settings.SelfUpgrade.TakeoverStopTimeoutSeconds);
        }

        _logger.LogInformation(
            "Prepared HostAgent takeover service. CurrentService={CurrentService}, NewService={NewService}, Version={Version}, InstallPath={InstallPath}",
            _process.ServiceName,
            serviceName,
            desired.Version,
            installPath);
    }

    public async Task CompleteTakeoverAsync(
        string hostKey,
        Guid hostId,
        CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;
        var previousServiceName = _process.TakeoverFromServiceName;
        if (string.IsNullOrWhiteSpace(previousServiceName)
            || string.Equals(previousServiceName, _process.ServiceName, StringComparison.OrdinalIgnoreCase))
        {
            _process.MarkNormal();
            return;
        }

        await _repository.RequestHostAgentQuiesceAsync(
            hostId,
            previousServiceName,
            $"Takeover by {_process.ServiceName}",
            cancellationToken);

        StopServiceIfRunning(previousServiceName, settings.SelfUpgrade.TakeoverStopTimeoutSeconds);
        await _repository.MarkHostAgentQuiescedAsync(hostId, previousServiceName, cancellationToken);

        if (settings.SelfUpgrade.DeletePreviousServiceAfterTakeover)
        {
            DeleteServiceIfExists(previousServiceName);
        }

        var executablePath = FindHostAgentExecutable(AppContext.BaseDirectory);
        ConfigureService(_process.ServiceName, executablePath, CreateNormalArguments(_process.ServiceName));
        WriteNormalSettings(AppContext.BaseDirectory, _process.ServiceName);
        _process.MarkNormal();
        var currentDesired = await _repository.GetDesiredHostAgentUpgradeAsync(hostKey, cancellationToken);
        var artifactId = currentDesired is not null && IsCurrentVersion(currentDesired)
            ? currentDesired.ArtifactId
            : (int?)null;

        await _repository.PublishHostAgentRuntimeStateAsync(
            hostId,
            _process,
            HostAgentRuntimeMode.Normal,
            artifactId,
            AppContext.BaseDirectory,
            isActive: true,
            $"HostAgent takeover completed for host '{hostKey}'.",
            cancellationToken);

        _logger.LogInformation(
            "Completed HostAgent takeover. CurrentService={CurrentService}, PreviousService={PreviousService}",
            _process.ServiceName,
            previousServiceName);
    }

    private bool IsCurrentVersion(HostAgentUpgradeDescriptor desired)
        => string.Equals(desired.Version, _process.Version, StringComparison.OrdinalIgnoreCase);

    private static string ResolveInstallPath(HostAgentSettings settings, HostAgentUpgradeDescriptor desired)
    {
        var root = FirstNonEmpty(desired.InstallRoot, settings.SelfUpgrade.InstallRoot, settings.ServicesRoot, AppContext.BaseDirectory);
        var folderName = "HostAgent-" + SanitizeForPath(desired.Version);
        return DeploymentPath.CombineUnderRoot(Path.GetFullPath(root), folderName, nameof(settings.SelfUpgrade.InstallRoot));
    }

    private string ResolveServiceName(HostAgentSettings settings, HostAgentUpgradeDescriptor desired)
    {
        var prefix = FirstNonEmpty(desired.ServiceNamePrefix, settings.SelfUpgrade.ServiceNamePrefix);
        if (string.IsNullOrWhiteSpace(prefix))
        {
            prefix = TrimTrailingVersion(_process.ServiceName);
        }

        return $"{prefix.Trim().TrimEnd('.')}.{SanitizeForServiceName(desired.Version)}";
    }

    private static string TrimTrailingVersion(string serviceName)
    {
        var trimmed = serviceName.Trim();
        var lastDot = trimmed.LastIndexOf('.');
        if (lastDot <= 0)
        {
            return trimmed;
        }

        var suffix = trimmed[(lastDot + 1)..];
        return suffix.Any(char.IsDigit) ? trimmed[..lastDot] : trimmed;
    }

    private static void PrepareInstallDirectory(
        string sourceLocalPath,
        string installPath,
        CancellationToken cancellationToken)
    {
        var stagingPath = installPath + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            if (File.Exists(sourceLocalPath))
            {
                ZipFile.ExtractToDirectory(sourceLocalPath, stagingPath, overwriteFiles: true);
            }
            else if (Directory.Exists(sourceLocalPath))
            {
                ArtifactDirectoryMirror.MirrorDirectory(sourceLocalPath, stagingPath, [], cancellationToken);
            }
            else
            {
                throw new DirectoryNotFoundException($"Provisioned HostAgent artifact path was not found: '{sourceLocalPath}'.");
            }

            if (Directory.Exists(installPath))
            {
                Directory.Delete(installPath, recursive: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(installPath)!);
            Directory.Move(stagingPath, installPath);
        }
        finally
        {
            TryDeleteDirectory(stagingPath);
        }
    }

    private void WriteTakeoverSettings(
        HostAgentSettings settings,
        string installPath,
        HostAgentUpgradeDescriptor desired,
        string serviceName)
    {
        var targetSettingsPath = Path.Join(installPath, "appsettings.Production.json");
        var json = LoadCurrentSettingsJson() ?? new JsonObject();
        var hostAgent = GetOrCreateObject(json, "HostAgent");
        hostAgent["ServiceName"] = serviceName;
        hostAgent["Version"] = desired.Version;
        hostAgent["RuntimeMode"] = HostAgentRuntimeMode.Takeover;
        hostAgent["TakeoverFromServiceName"] = _process.ServiceName;
        hostAgent["HostKey"] = settings.HostKey;
        hostAgent["HostName"] = settings.HostName;
        hostAgent.Remove("IisAppPoolPassword");
        ScrubPlainTextIisOverridePasswords(hostAgent);

        var selfUpgrade = GetOrCreateObject(hostAgent, "SelfUpgrade");
        selfUpgrade["ServiceAccountName"] = settings.SelfUpgrade.ServiceAccountName;
        selfUpgrade["ServiceAccountPasswordCredentialKey"] = settings.SelfUpgrade.ServiceAccountPasswordCredentialKey;
        selfUpgrade.Remove("ServiceAccountPassword");

        var credentialStore = GetOrCreateObject(hostAgent, "CredentialStore");
        credentialStore["AutomationMode"] = settings.CredentialStore.AutomationMode;
        credentialStore["FilePath"] = ResolveCredentialStoreFilePath(settings.CredentialStore);
        credentialStore["ProtectionScope"] = settings.CredentialStore.ProtectionScope;
        credentialStore["EntropyPurpose"] = settings.CredentialStore.EntropyPurpose;

        var connectionStrings = GetOrCreateObject(json, "ConnectionStrings");
        connectionStrings["OmpDb"] = _repository.GetConfiguredConnectionString();

        Directory.CreateDirectory(Path.GetDirectoryName(targetSettingsPath)!);
        File.WriteAllText(
            targetSettingsPath,
            json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void ScrubPlainTextIisOverridePasswords(JsonObject hostAgent)
    {
        if (hostAgent["IisAppPoolOverrides"] is not JsonObject overrides)
        {
            return;
        }

        // Materialize before removal so the JsonObject collection is not mutated during enumeration.
        foreach (var identity in overrides.ToArray().Select(pair => pair.Value).OfType<JsonObject>())
        {
            identity.Remove("Password");
        }
    }

    private async Task<HostAgentUpgradeSettings> ResolveSelfUpgradeSettingsAsync(
        HostAgentUpgradeSettings settings,
        CancellationToken cancellationToken)
    {
        var result = new HostAgentUpgradeSettings
        {
            IsEnabled = settings.IsEnabled,
            InstallRoot = settings.InstallRoot,
            ServiceNamePrefix = settings.ServiceNamePrefix,
            ServiceAccountName = settings.ServiceAccountName,
            ServiceAccountPassword = settings.ServiceAccountPassword,
            ServiceAccountPasswordCredentialKey = settings.ServiceAccountPasswordCredentialKey,
            TakeoverStopTimeoutSeconds = settings.TakeoverStopTimeoutSeconds,
            DeletePreviousServiceAfterTakeover = settings.DeletePreviousServiceAfterTakeover,
            StartPreparedService = settings.StartPreparedService
        };

        if (string.IsNullOrWhiteSpace(result.ServiceAccountPasswordCredentialKey))
        {
            return result;
        }

        var credential = await _credentialStore.TryReadCredentialAsync(
            result.ServiceAccountPasswordCredentialKey,
            cancellationToken);
        if (credential is null)
        {
            throw new InvalidOperationException(
                $"HostAgent self-upgrade credential '{result.ServiceAccountPasswordCredentialKey}' could not be resolved from HostAgent credential store.");
        }

        if (string.IsNullOrWhiteSpace(result.ServiceAccountName))
        {
            result.ServiceAccountName = credential.UserName;
        }

        result.ServiceAccountPassword = credential.Password;
        return result;
    }

    private static string ResolveCredentialStoreFilePath(HostAgentCredentialStoreSettings settings)
        => string.IsNullOrWhiteSpace(settings.FilePath)
            ? Path.Join(AppContext.BaseDirectory, "hostagent.credentials.json")
            : settings.FilePath.Trim();

    private static void WriteNormalSettings(string installPath, string serviceName)
    {
        var targetSettingsPath = Path.Join(installPath, "appsettings.Production.json");
        if (!File.Exists(targetSettingsPath))
        {
            return;
        }

        var node = JsonNode.Parse(File.ReadAllText(targetSettingsPath));
        if (node is not JsonObject json)
        {
            return;
        }

        var hostAgent = GetOrCreateObject(json, "HostAgent");
        hostAgent["ServiceName"] = serviceName;
        hostAgent["RuntimeMode"] = HostAgentRuntimeMode.Normal;
        hostAgent["TakeoverFromServiceName"] = string.Empty;

        File.WriteAllText(
            targetSettingsPath,
            json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static JsonObject? LoadCurrentSettingsJson()
    {
        return new[] { "appsettings.Production.json", "appsettings.json" }
            .Select(fileName => Path.Join(AppContext.BaseDirectory, fileName))
            .Where(File.Exists)
            .Select(path => JsonNode.Parse(File.ReadAllText(path)))
            .OfType<JsonObject>()
            .Select(obj => obj.DeepClone().AsObject())
            .FirstOrDefault();
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        parent[propertyName] = created;
        return created;
    }

    private static string FindHostAgentExecutable(string root)
    {
        var direct = Path.Join(root, "OpenModulePlatform.HostAgent.WindowsService.exe");
        if (File.Exists(direct))
        {
            return direct;
        }

        var matches = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "OpenModulePlatform.HostAgent.WindowsService.exe", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

        if (matches.Length == 1)
        {
            return matches[0];
        }

        throw new FileNotFoundException($"HostAgent executable was not found below '{root}'.");
    }

    private static string[] CreateTakeoverArguments(string serviceName, string previousServiceName)
        =>
        [
            $"--service-name={serviceName}",
            $"--runtime-mode={HostAgentRuntimeMode.Takeover}",
            $"--takeover-from={previousServiceName}"
        ];

    private static string[] CreateNormalArguments(string serviceName)
        => [$"--service-name={serviceName}"];

    private static void EnsureService(
        string serviceName,
        string executablePath,
        IReadOnlyList<string> arguments,
        HostAgentUpgradeSettings upgradeSettings)
    {
        if (GetServiceState(serviceName) is null)
        {
            var createArguments = new List<string>
            {
                "create",
                serviceName,
                "binPath=",
                CreateBinaryPath(executablePath, arguments),
                "start=",
                "auto",
                "DisplayName=",
                ResolveServiceDisplayName(serviceName)
            };
            AddServiceAccountArguments(createArguments, upgradeSettings);
            RunScChecked(createArguments.ToArray());
        }
        else
        {
            ConfigureService(serviceName, executablePath, arguments, upgradeSettings);
        }

        RunScChecked("description", serviceName, "OpenModulePlatform HostAgent runtime service.");
    }

    private static void ConfigureService(
        string serviceName,
        string executablePath,
        IReadOnlyList<string> arguments,
        HostAgentUpgradeSettings? upgradeSettings = null)
    {
        var configArguments = new List<string>
        {
            "config",
            serviceName,
            "binPath=",
            CreateBinaryPath(executablePath, arguments),
            "start=",
            "auto",
            "DisplayName=",
            ResolveServiceDisplayName(serviceName)
        };
        if (upgradeSettings is not null)
        {
            AddServiceAccountArguments(configArguments, upgradeSettings);
        }

        RunScChecked(configArguments.ToArray());
    }

    private static string ResolveServiceDisplayName(string serviceName)
        => serviceName.StartsWith("OMP", StringComparison.OrdinalIgnoreCase)
            ? serviceName
            : $"OMP {serviceName}";

    private static void AddServiceAccountArguments(List<string> arguments, HostAgentUpgradeSettings upgradeSettings)
    {
        if (string.IsNullOrWhiteSpace(upgradeSettings.ServiceAccountName))
        {
            return;
        }

        arguments.Add("obj=");
        arguments.Add(upgradeSettings.ServiceAccountName.Trim());
        if (!string.IsNullOrWhiteSpace(upgradeSettings.ServiceAccountPassword))
        {
            arguments.Add("password=");
            arguments.Add(upgradeSettings.ServiceAccountPassword);
        }
    }

    private static string CreateBinaryPath(string executablePath, IReadOnlyList<string> arguments)
    {
        var parts = new List<string> { Quote(executablePath) };
        parts.AddRange(arguments.Select(QuoteIfNeeded));
        return string.Join(" ", parts);
    }

    private static void StopServiceIfRunning(string serviceName, int timeoutSeconds)
    {
        var state = GetServiceState(serviceName);
        if (state is null || state.Equals("STOPPED", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RunScChecked("stop", serviceName);
        WaitForServiceState(serviceName, "STOPPED", timeoutSeconds);
    }

    private static void StartServiceIfStopped(string serviceName, int timeoutSeconds)
    {
        var state = GetServiceState(serviceName);
        if (state is null)
        {
            throw new InvalidOperationException($"Windows service '{serviceName}' was not found.");
        }

        if (!state.Equals("RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            RunScChecked("start", serviceName);
            WaitForServiceState(serviceName, "RUNNING", timeoutSeconds);
        }
    }

    private static void DeleteServiceIfExists(string serviceName)
    {
        if (GetServiceState(serviceName) is null)
        {
            return;
        }

        RunScChecked("delete", serviceName);
    }

    private static void WaitForServiceState(string serviceName, string desiredState, int timeoutSeconds)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = GetServiceState(serviceName);
            if (state is null && desiredState.Equals("DELETED", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (state is not null && state.Equals(desiredState, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException($"Windows service '{serviceName}' did not reach state '{desiredState}' within {timeoutSeconds} seconds.");
    }

    private static string? GetServiceState(string serviceName)
    {
        var result = RunSc("query", serviceName);
        if (result.ExitCode != 0)
        {
            return IsServiceNotFound(result) ? null : throw new InvalidOperationException(result.CombinedOutput.Trim());
        }

        foreach (var line in result.Output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var stateIndex = line.IndexOf("STATE", StringComparison.OrdinalIgnoreCase);
            if (stateIndex < 0)
            {
                continue;
            }

            var separatorIndex = line.IndexOf(':', stateIndex);
            if (separatorIndex < 0)
            {
                continue;
            }

            var parts = line[(separatorIndex + 1)..].Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0)
            {
                return parts[^1];
            }
        }

        return null;
    }

    private static ScResult RunSc(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "sc.exe"),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start sc.exe.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ScResult(process.ExitCode, output, error);
    }

    private static void RunScChecked(params string[] arguments)
    {
        var result = RunSc(arguments);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"sc.exe failed with exit code {result.ExitCode}: {result.CombinedOutput.Trim()}");
        }
    }

    private static bool IsServiceNotFound(ScResult result)
        => result.ExitCode == ScServiceNotFoundExitCode
            || result.CombinedOutput.Contains("FAILED 1060", StringComparison.OrdinalIgnoreCase)
            || result.CombinedOutput.Contains("does not exist", StringComparison.OrdinalIgnoreCase);

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
            ?? throw new InvalidOperationException("A required HostAgent self-upgrade path was not configured.");

    private static string SanitizeForPath(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim('.', ' ');
    }

    private static string SanitizeForServiceName(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_').ToArray();
        return new string(chars).Trim('.', ' ');
    }

    private static string Quote(string value)
        => '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';

    private static string QuoteIfNeeded(string value)
        => value.Contains(' ', StringComparison.Ordinal) ? Quote(value) : value;

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort staging cleanup: the next upgrade creates a unique staging folder, so locked leftovers are harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort staging cleanup: lack of delete permission should not mask the actual self-upgrade result.
        }
    }

    private sealed record ScResult(int ExitCode, string Output, string Error)
    {
        public string CombinedOutput => string.Concat(Output, "\n", Error);
    }
}

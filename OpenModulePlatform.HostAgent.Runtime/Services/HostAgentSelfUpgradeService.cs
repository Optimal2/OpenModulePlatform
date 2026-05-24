using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
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
    private const int DirectoryDeleteMaxAttempts = 20;
    private const string DefaultNLogAppName = "OpenModulePlatform.HostAgent.WindowsService";
    private const string DefaultNLogDirectory = "${basedir}/logs";
    private static readonly TimeSpan DirectoryDeleteRetryDelay = TimeSpan.FromMilliseconds(500);

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
            resolvedSelfUpgradeSettings,
            desired.Version);

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

    public async Task<bool> ShouldForceLeaseTakeoverAsync(
        string hostKey,
        CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;
        if (!settings.SelfUpgrade.IsEnabled)
        {
            return false;
        }

        var desired = await _repository.GetDesiredHostAgentUpgradeAsync(hostKey, cancellationToken);
        return desired is not null
            && IsCurrentVersion(desired)
            && string.Equals(ResolveServiceName(settings, desired), _process.ServiceName, StringComparison.OrdinalIgnoreCase);
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

        try
        {
            await ValidateTakeoverReadinessAsync(settings, cancellationToken);
        }
        catch (Exception ex) when (IsExpectedTakeoverReadinessFailure(ex))
        {
            await FailTakeoverAsync(
                hostId,
                $"HostAgent takeover failed before retiring '{previousServiceName}': {ex.Message}",
                cancellationToken);
            throw;
        }

        var previousExecutablePath = TryGetServiceExecutablePath(previousServiceName);

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

        TryDeletePreviousInstallDirectory(previousServiceName, previousExecutablePath, settings, cancellationToken);

        var executablePath = FindHostAgentExecutable(AppContext.BaseDirectory);
        ConfigureService(_process.ServiceName, executablePath, CreateNormalArguments(_process.ServiceName), version: _process.Version);
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

        await CleanupSupersededHostAgentServicesAsync(hostKey, hostId, cancellationToken);

        _logger.LogInformation(
            "Completed HostAgent takeover. CurrentService={CurrentService}, PreviousService={PreviousService}",
            _process.ServiceName,
            previousServiceName);
    }

    private async Task ValidateTakeoverReadinessAsync(
        HostAgentSettings settings,
        CancellationToken cancellationToken)
    {
        _ = FindHostAgentExecutable(AppContext.BaseDirectory);

        var credentialKeys = GetRequiredCredentialKeys(settings)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (credentialKeys.Length == 0)
        {
            return;
        }

        if (!settings.CredentialStore.IsEnabled())
        {
            throw new InvalidOperationException(
                "HostAgent takeover requires stored credentials, but HostAgent:CredentialStore:AutomationMode is Disabled.");
        }

        foreach (var credentialKey in credentialKeys)
        {
            var credential = await _credentialStore.TryReadCredentialAsync(credentialKey, cancellationToken);
            if (credential is null)
            {
                throw new InvalidOperationException(
                    $"HostAgent takeover credential '{credentialKey}' could not be resolved from the credential store.");
            }
        }
    }

    private async Task FailTakeoverAsync(
        Guid hostId,
        string statusMessage,
        CancellationToken cancellationToken)
    {
        _process.MarkQuiesced();
        await _repository.PublishHostAgentRuntimeStateAsync(
            hostId,
            _process,
            HostAgentRuntimeMode.Failed,
            artifactId: null,
            AppContext.BaseDirectory,
            isActive: false,
            statusMessage,
            cancellationToken);
        await _repository.ReleaseHostAgentLeaseAsync(hostId, _process.ServiceName, cancellationToken);

        _logger.LogError(
            "HostAgent takeover failed before the previous service was retired. CurrentService={CurrentService}, StatusMessage={StatusMessage}",
            _process.ServiceName,
            statusMessage);
    }

    private static IEnumerable<string> GetRequiredCredentialKeys(HostAgentSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.SelfUpgrade.ServiceAccountPasswordCredentialKey))
        {
            yield return settings.SelfUpgrade.ServiceAccountPasswordCredentialKey.Trim();
        }

        if (settings.DeployWebApps)
        {
            if (!string.IsNullOrWhiteSpace(settings.IisAppPoolPasswordCredentialKey))
            {
                yield return settings.IisAppPoolPasswordCredentialKey.Trim();
            }

            foreach (var key in settings.IisAppPoolOverrides.Values
                .Select(static identity => identity.PasswordCredentialKey)
                .Where(static key => !string.IsNullOrWhiteSpace(key)))
            {
                yield return key.Trim();
            }
        }

        if (!settings.DeployServiceApps)
        {
            yield break;
        }

        var serviceAppCredentialKey = string.IsNullOrWhiteSpace(settings.ServiceAppPasswordCredentialKey)
            ? settings.SelfUpgrade.ServiceAccountPasswordCredentialKey
            : settings.ServiceAppPasswordCredentialKey;
        if (!string.IsNullOrWhiteSpace(serviceAppCredentialKey))
        {
            yield return serviceAppCredentialKey.Trim();
        }

        foreach (var key in settings.ServiceAppIdentityOverrides.Values
            .Select(static identity => identity.PasswordCredentialKey)
            .Where(static key => !string.IsNullOrWhiteSpace(key)))
        {
            yield return key.Trim();
        }
    }

    private static bool IsExpectedTakeoverReadinessFailure(Exception ex)
        => ex is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or CryptographicException
            or FormatException;

    public async Task CleanupSupersededHostAgentServicesAsync(
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
        if (desired is null || !IsCurrentVersion(desired))
        {
            return;
        }

        var expectedCurrentServiceName = ResolveServiceName(settings, desired);
        if (!string.Equals(expectedCurrentServiceName, _process.ServiceName, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Skipping HostAgent cleanup because the current service name does not match the desired versioned service name. CurrentService={CurrentService}, ExpectedService={ExpectedService}",
                _process.ServiceName,
                expectedCurrentServiceName);
            return;
        }

        var serviceNamePrefix = ResolveServiceNamePrefix(settings, desired);
        foreach (var service in EnumerateSupersededHostAgentServices(serviceNamePrefix))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(service.Name, _process.ServiceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                StopServiceIfRunning(service.Name, settings.SelfUpgrade.TakeoverStopTimeoutSeconds);
                DeleteServiceIfExists(service.Name);
                TryDeleteSupersededInstallDirectory(service.ExecutablePath, settings, cancellationToken);
                await _repository.MarkHostAgentRetiredAsync(hostId, service.Name, _process.ServiceName, cancellationToken);

                _logger.LogInformation(
                    "Removed superseded HostAgent service. CurrentService={CurrentService}, RemovedService={RemovedService}",
                    _process.ServiceName,
                    service.Name);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not remove superseded HostAgent service. CurrentService={CurrentService}, SupersededService={SupersededService}",
                    _process.ServiceName,
                    service.Name);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not remove superseded HostAgent service files. CurrentService={CurrentService}, SupersededService={SupersededService}",
                    _process.ServiceName,
                    service.Name);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not remove superseded HostAgent service files because access was denied. CurrentService={CurrentService}, SupersededService={SupersededService}",
                    _process.ServiceName,
                    service.Name);
            }
        }

        CleanupOrphanedInstallDirectories(settings, serviceNamePrefix, cancellationToken);
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
        var prefix = ResolveServiceNamePrefix(settings, desired);
        return $"{prefix.Trim().TrimEnd('.')}.{SanitizeForServiceName(desired.Version)}";
    }

    private string ResolveServiceNamePrefix(HostAgentSettings settings, HostAgentUpgradeDescriptor desired)
    {
        var prefix = FirstNonEmpty(desired.ServiceNamePrefix, settings.SelfUpgrade.ServiceNamePrefix);
        if (string.IsNullOrWhiteSpace(prefix))
        {
            prefix = TrimTrailingVersion(_process.ServiceName);
        }

        return prefix.Trim().TrimEnd('.');
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
        var targetCredentialStorePath = Path.Join(installPath, "hostagent.credentials.json");
        TryCopyCredentialStoreFile(settings.CredentialStore, targetCredentialStorePath);
        credentialStore["AutomationMode"] = settings.CredentialStore.AutomationMode;
        credentialStore["FilePath"] = targetCredentialStorePath;
        credentialStore["ProtectionScope"] = settings.CredentialStore.ProtectionScope;
        credentialStore["EntropyPurpose"] = settings.CredentialStore.EntropyPurpose;

        var connectionStrings = GetOrCreateObject(json, "ConnectionStrings");
        connectionStrings["OmpDb"] = _repository.GetConfiguredConnectionString();
        EnsureDefaultLoggingSettings(json);

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

    private static void TryCopyCredentialStoreFile(
        HostAgentCredentialStoreSettings settings,
        string targetCredentialStorePath)
    {
        var sourceCredentialStorePath = ResolveCredentialStoreFilePath(settings);
        if (!File.Exists(sourceCredentialStorePath))
        {
            return;
        }

        var fullSourcePath = Path.GetFullPath(sourceCredentialStorePath);
        var fullTargetPath = Path.GetFullPath(targetCredentialStorePath);
        if (string.Equals(fullSourcePath, fullTargetPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullTargetPath)!);
        File.Copy(fullSourcePath, fullTargetPath, overwrite: true);
    }

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
        JsonObject? merged = null;
        foreach (var fileName in new[] { "appsettings.json", "appsettings.Production.json" })
        {
            var path = Path.Join(AppContext.BaseDirectory, fileName);
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject json)
            {
                continue;
            }

            merged ??= [];
            MergeJsonObject(merged, json);
        }

        return merged;
    }

    private static void MergeJsonObject(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (value is JsonObject sourceObject && target[key] is JsonObject targetObject)
            {
                MergeJsonObject(targetObject, sourceObject);
                continue;
            }

            target[key] = value?.DeepClone();
        }
    }

    private static void EnsureDefaultLoggingSettings(JsonObject json)
    {
        if (json["Logging"] is null)
        {
            json["Logging"] = new JsonObject
            {
                ["LogLevel"] = new JsonObject
                {
                    ["Default"] = "Information",
                    ["Microsoft.Hosting.Lifetime"] = "Information"
                }
            };
        }

        if (json["NLog"] is not null)
        {
            return;
        }

        json["NLog"] = new JsonObject
        {
            ["autoReload"] = true,
            ["throwConfigExceptions"] = true,
            ["variables"] = new JsonObject
            {
                ["appName"] = DefaultNLogAppName,
                ["logDirectory"] = DefaultNLogDirectory
            },
            ["targets"] = new JsonObject
            {
                ["logfile"] = new JsonObject
                {
                    ["type"] = "File",
                    ["fileName"] = "${var:logDirectory}/${var:appName}-${shortdate}.log",
                    ["layout"] = "${longdate}|${uppercase:${level}}|${logger}|${message}${onexception:inner= ${exception:format=tostring}}"
                },
                ["console"] = new JsonObject
                {
                    ["type"] = "Console",
                    ["layout"] = "${longdate}|${uppercase:${level}}|${logger}|${message}${onexception:inner= ${exception:format=tostring}}"
                }
            },
            ["rules"] = new JsonArray
            {
                new JsonObject
                {
                    ["logger"] = "Microsoft.Hosting.Lifetime",
                    ["minLevel"] = "Info",
                    ["writeTo"] = "console,logfile",
                    ["final"] = true
                },
                new JsonObject
                {
                    ["logger"] = "Microsoft.*",
                    ["maxLevel"] = "Info",
                    ["final"] = true
                },
                new JsonObject
                {
                    ["logger"] = "*",
                    ["minLevel"] = "Info",
                    ["writeTo"] = "console,logfile"
                }
            }
        };
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
        HostAgentUpgradeSettings upgradeSettings,
        string version)
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
                ResolveServiceDisplayName(serviceName, version)
            };
            AddServiceAccountArguments(createArguments, upgradeSettings);
            RunScChecked(createArguments.ToArray());
        }
        else
        {
            ConfigureService(serviceName, executablePath, arguments, upgradeSettings, version);
        }

        RunScChecked("description", serviceName, "OpenModulePlatform HostAgent runtime service.");
    }

    private static void ConfigureService(
        string serviceName,
        string executablePath,
        IReadOnlyList<string> arguments,
        HostAgentUpgradeSettings? upgradeSettings = null,
        string? version = null)
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
            ResolveServiceDisplayName(serviceName, version)
        };
        if (upgradeSettings is not null)
        {
            AddServiceAccountArguments(configArguments, upgradeSettings);
        }

        RunScChecked(configArguments.ToArray());
    }

    private static string ResolveServiceDisplayName(string serviceName, string? version)
    {
        var trimmed = serviceName.Trim();
        if (!string.IsNullOrWhiteSpace(version)
            && trimmed.EndsWith("." + version.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            var prefix = trimmed[..^(version.Trim().Length + 1)];
            var baseDisplayName = prefix.Replace('.', ' ');
            if (!baseDisplayName.StartsWith("OMP", StringComparison.OrdinalIgnoreCase))
            {
                baseDisplayName = "OMP " + baseDisplayName;
            }

            return $"{baseDisplayName} {version.Trim()}";
        }

        return trimmed.StartsWith("OMP", StringComparison.OrdinalIgnoreCase)
            ? trimmed.Replace('.', ' ')
            : $"OMP {trimmed}";
    }

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

    private static IReadOnlyList<HostAgentServiceCandidate> EnumerateSupersededHostAgentServices(string serviceNamePrefix)
    {
        var prefix = serviceNamePrefix.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return [];
        }

        return EnumerateHostAgentServices(prefix)
            .Where(service => IsSupersededHostAgentServiceName(service.Name, prefix))
            .ToArray();
    }

    private static IReadOnlyList<HostAgentServiceCandidate> EnumerateHostAgentServices(string serviceNamePrefix)
    {
        var prefix = serviceNamePrefix.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return [];
        }

        var result = RunSc("queryex", "type=", "service", "state=", "all");
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"sc.exe failed with exit code {result.ExitCode}: {result.CombinedOutput.Trim()}");
        }

        var serviceNames = result.Output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("SERVICE_NAME:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line["SERVICE_NAME:".Length..].Trim())
            .Where(name => IsHostAgentServiceName(name, prefix))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return serviceNames
            .Select(name => new HostAgentServiceCandidate(name, TryGetServiceExecutablePath(name)))
            .ToArray();
    }

    private static bool IsSupersededHostAgentServiceName(string serviceName, string serviceNamePrefix)
        => IsHostAgentServiceName(serviceName, serviceNamePrefix);

    private static bool IsHostAgentServiceName(string serviceName, string serviceNamePrefix)
        => string.Equals(serviceName, serviceNamePrefix, StringComparison.OrdinalIgnoreCase)
            || serviceName.StartsWith(serviceNamePrefix + ".", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetServiceExecutablePath(string serviceName)
    {
        var result = RunSc("qc", serviceName);
        if (result.ExitCode != 0)
        {
            return null;
        }

        foreach (var line in result.Output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var binaryPathIndex = line.IndexOf("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase);
            if (binaryPathIndex < 0)
            {
                continue;
            }

            var separatorIndex = line.IndexOf(':', binaryPathIndex);
            if (separatorIndex < 0)
            {
                continue;
            }

            return TryExtractExecutablePath(line[(separatorIndex + 1)..].Trim());
        }

        return null;
    }

    private static string? TryExtractExecutablePath(string binaryPath)
    {
        if (string.IsNullOrWhiteSpace(binaryPath))
        {
            return null;
        }

        var trimmed = binaryPath.Trim();
        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            return closingQuote > 1 ? trimmed[1..closingQuote] : null;
        }

        var executableEnd = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return executableEnd < 0 ? null : trimmed[..(executableEnd + ".exe".Length)].Trim();
    }

    private void TryDeletePreviousInstallDirectory(
        string previousServiceName,
        string? previousExecutablePath,
        HostAgentSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            TryDeleteSupersededInstallDirectory(previousExecutablePath, settings, cancellationToken);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(
                ex,
                "Could not remove previous HostAgent install directory after takeover. CurrentService={CurrentService}, PreviousService={PreviousService}",
                _process.ServiceName,
                previousServiceName);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(
                ex,
                "Could not remove previous HostAgent install directory after takeover because access was denied. CurrentService={CurrentService}, PreviousService={PreviousService}",
                _process.ServiceName,
                previousServiceName);
        }
    }

    private static void TryDeleteSupersededInstallDirectory(
        string? executablePath,
        HostAgentSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        var installDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
        {
            return;
        }

        var allowedRoot = FirstNonEmpty(settings.SelfUpgrade.InstallRoot, settings.ServicesRoot, AppContext.BaseDirectory);
        var fullAllowedRoot = EnsureTrailingDirectorySeparator(Path.GetFullPath(allowedRoot));
        var fullInstallDirectory = Path.GetFullPath(installDirectory);
        if (!fullInstallDirectory.StartsWith(fullAllowedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var folderName = Path.GetFileName(fullInstallDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!folderName.StartsWith("HostAgent-", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (IsDirectoryProtectedByCredentialStore(fullInstallDirectory, settings))
        {
            return;
        }

        DeleteDirectoryWithRetry(fullInstallDirectory, cancellationToken);
    }

    private void CleanupOrphanedInstallDirectories(
        HostAgentSettings settings,
        string serviceNamePrefix,
        CancellationToken cancellationToken)
    {
        var allowedRoot = FirstNonEmpty(settings.SelfUpgrade.InstallRoot, settings.ServicesRoot, AppContext.BaseDirectory);
        if (!Directory.Exists(allowedRoot))
        {
            return;
        }

        var protectedDirectories = EnumerateHostAgentServices(serviceNamePrefix)
            .Select(service => service.ExecutablePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetDirectoryName(path!))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .Append(Path.GetFullPath(AppContext.BaseDirectory))
            .Concat(GetCredentialStoreProtectedDirectories(settings))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in Directory.EnumerateDirectories(allowedRoot, "HostAgent-*", SearchOption.TopDirectoryOnly))
        {
            var fullDirectory = Path.GetFullPath(directory);
            if (protectedDirectories.Contains(fullDirectory))
            {
                continue;
            }

            try
            {
                DeleteDirectoryWithRetry(fullDirectory, cancellationToken);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Could not remove orphaned HostAgent install directory. Directory={Directory}", fullDirectory);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogDebug(ex, "Could not remove orphaned HostAgent install directory because access was denied. Directory={Directory}", fullDirectory);
            }
        }
    }

    private static void DeleteDirectoryWithRetry(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < DirectoryDeleteMaxAttempts)
            {
                WaitBeforeDirectoryDeleteRetry(cancellationToken);
            }
            catch (UnauthorizedAccessException) when (attempt < DirectoryDeleteMaxAttempts)
            {
                WaitBeforeDirectoryDeleteRetry(cancellationToken);
            }
        }
    }

    private static void WaitBeforeDirectoryDeleteRetry(CancellationToken cancellationToken)
    {
        if (cancellationToken.WaitHandle.WaitOne(DirectoryDeleteRetryDelay))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static string EnsureTrailingDirectorySeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static IEnumerable<string> GetCredentialStoreProtectedDirectories(HostAgentSettings settings)
    {
        var credentialStorePath = ResolveCredentialStoreFilePath(settings.CredentialStore);
        if (string.IsNullOrWhiteSpace(credentialStorePath))
        {
            yield break;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(credentialStorePath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            yield return directory;
        }
    }

    private static bool IsDirectoryProtectedByCredentialStore(
        string installDirectory,
        HostAgentSettings settings)
    {
        var credentialStorePath = ResolveCredentialStoreFilePath(settings.CredentialStore);
        if (string.IsNullOrWhiteSpace(credentialStorePath))
        {
            return false;
        }

        var fullInstallDirectory = EnsureTrailingDirectorySeparator(Path.GetFullPath(installDirectory));
        var fullCredentialStorePath = Path.GetFullPath(credentialStorePath);
        return fullCredentialStorePath.StartsWith(fullInstallDirectory, StringComparison.OrdinalIgnoreCase);
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

    private sealed record HostAgentServiceCandidate(string Name, string? ExecutablePath);
}

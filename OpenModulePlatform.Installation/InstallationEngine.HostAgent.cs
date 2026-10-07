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
    public static async Task InstallHostAgentAsync(BootstrapConfig config, string payloadRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("HostAgent Windows service installation requires Windows.");
        }

        if (!IsWindowsAdministrator())
        {
            throw new InvalidOperationException("Run the bootstrapper as Administrator to install the HostAgent Windows service.");
        }

        var hostAgent = config.HostAgent;
        ValidateHostAgentServiceAccount(hostAgent);
        var serviceIdentity = ResolveBootstrapHostAgentServiceIdentity(config);
        if (string.IsNullOrWhiteSpace(hostAgent.ServiceName))
        {
            throw new InvalidOperationException("HostAgent:ServiceName must be configured.");
        }

        if (string.IsNullOrWhiteSpace(hostAgent.InstallPath))
        {
            throw new InvalidOperationException("HostAgent:InstallPath must be configured.");
        }

        if (string.IsNullOrWhiteSpace(hostAgent.PackagePath))
        {
            throw new InvalidOperationException("HostAgent:PackagePath must be configured.");
        }

        var packagePath = ResolvePackageDataPath(payloadRoot, hostAgent.PackagePath);
        var installPath = serviceIdentity.InstallPath;
        var stagingRoot = Path.Join(Path.GetTempPath(), "OMP.HostAgent", Guid.NewGuid().ToString("N"));
        var sourceDirectory = packagePath;

        try
        {
            if (File.Exists(packagePath) && packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(stagingRoot);
                var extraction = new ArtifactPackageExtractor().Extract(packagePath, stagingRoot);
                sourceDirectory = extraction.ArtifactContentPath;
            }

            if (!Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException($"HostAgent package folder was not found: {sourceDirectory}");
            }

            var hostAgentServices = EnumerateHostAgentWindowsServices(serviceIdentity.ServiceNamePrefix, installPath);
            foreach (var service in hostAgentServices)
            {
                if (string.Equals(service.Name, serviceIdentity.ServiceName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var usesTargetInstallPath = !string.IsNullOrWhiteSpace(service.ExecutablePath)
                    && IsSameOrChildPath(installPath, service.ExecutablePath);
                var isConfiguredBaseServiceName = !string.IsNullOrWhiteSpace(hostAgent.ServiceName)
                    && string.Equals(service.Name, hostAgent.ServiceName, StringComparison.OrdinalIgnoreCase);
                if (!usesTargetInstallPath && !isConfiguredBaseServiceName)
                {
                    continue;
                }

                InstallOutput.Info($"> Remove duplicate HostAgent service {service.Name}");
                DeleteWindowsService(service.Name);
            }

            var serviceExists = ServiceExists(serviceIdentity.ServiceName);
            if (serviceExists)
            {
                StopService(serviceIdentity.ServiceName);
            }

            if (Directory.Exists(installPath) && hostAgent.BackupExistingInstall)
            {
                var backupPath = CreateBackupPath(installPath);
                InstallOutput.Info($"> Backup HostAgent {installPath} -> {backupPath}");
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                CopyDirectory(installPath, backupPath);
            }

            InstallOutput.Info($"> Install HostAgent {installPath}");
            CopyDirectory(sourceDirectory, installPath);
            await WriteHostAgentSettingsAsync(config, installPath, serviceIdentity);

            var executablePath = Path.Join(installPath, "OpenModulePlatform.HostAgent.WindowsService.exe");
            if (!File.Exists(executablePath))
            {
                throw new FileNotFoundException("HostAgent executable was not found after installation.", executablePath);
            }

            RunHostAgentOnce(executablePath, installPath, serviceIdentity.ServiceName);
            var serviceAccountPassword = ResolveInstallerSecret(
                hostAgent.ServiceAccountPassword,
                config,
                "HostAgent:ServiceAccountPassword");

            if (serviceExists)
            {
                ConfigureService(hostAgent, serviceIdentity, executablePath, serviceAccountPassword);
            }
            else
            {
                CreateService(hostAgent, serviceIdentity, executablePath, serviceAccountPassword);
            }

            if (hostAgent.StartService)
            {
                StartService(serviceIdentity.ServiceName);
            }
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    internal static async Task WriteHostAgentSettingsAsync(
        BootstrapConfig config,
        string installPath,
        HostAgentBootstrapServiceIdentity serviceIdentity)
    {
        var hostAgent = config.HostAgent;
        var settings = hostAgent.AppSettings?.DeepClone()
            ?? CreateDefaultHostAgentSettings(config, serviceIdentity);
        var credentialPlan = CreateHostAgentCredentialPlan(config, installPath);

        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SqlConnectionString"] = BuildConnectionString(config.Sql, config.Sql.Database),
            ["ArtifactStoreRoot"] = Path.GetFullPath(config.ArtifactStoreRoot.Trim()),
            ["HostAgent.InstallPath"] = serviceIdentity.InstallPath,
            ["HostAgent.LocalArtifactCacheRoot"] = hostAgent.LocalArtifactCacheRoot,
            ["HostAgent.HostKey"] = hostAgent.HostKey,
            ["HostAgent.HostName"] = hostAgent.HostName,
            ["HostAgent.ServiceName"] = serviceIdentity.ServiceName
        };

        ReplaceTokens(settings, tokens);
        SynchronizeHostAgentSettings(settings, config, credentialPlan, serviceIdentity);

        var fileName = string.IsNullOrWhiteSpace(hostAgent.SettingsFileName)
            ? "appsettings.Production.json"
            : hostAgent.SettingsFileName.Trim();
        var path = CombineUnderRoot(installPath, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await WriteHostAgentCredentialStoreAsync(credentialPlan, hostAgent.ServiceAccountName);

        await AtomicJsonFile.WriteAsync(
            path,
            settings.ToJsonString(JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    internal static void RunHostAgentOnce(string executablePath, string installPath, string serviceName)
    {
        InstallOutput.Info("> Run HostAgent once");
        RunProcess(executablePath, ["--run-once", $"--service-name={serviceName}"], workingDirectory: installPath);
    }

    internal static void SynchronizeHostAgentSettings(
        JsonNode settings,
        BootstrapConfig config,
        HostAgentCredentialBootstrapPlan credentialPlan,
        HostAgentBootstrapServiceIdentity serviceIdentity)
    {
        if (settings is not JsonObject root)
        {
            throw new InvalidOperationException("HostAgent appsettings root must be a JSON object.");
        }

        var hostAgent = config.HostAgent;
        var connectionStrings = GetOrCreateJsonObject(root, "ConnectionStrings");
        connectionStrings["OmpDb"] = BuildConnectionString(config.Sql, config.Sql.Database);

        var hostAgentSettings = GetOrCreateJsonObject(root, "HostAgent");
        hostAgentSettings["ServiceName"] = serviceIdentity.ServiceName;
        if (!string.IsNullOrWhiteSpace(serviceIdentity.Version))
        {
            hostAgentSettings["Version"] = serviceIdentity.Version;
        }

        hostAgentSettings["HostKey"] = hostAgent.HostKey;
        hostAgentSettings["HostName"] = hostAgent.HostName;
        hostAgentSettings["RefreshSeconds"] = hostAgent.RefreshSeconds;
        hostAgentSettings["CentralArtifactRoot"] = Path.GetFullPath(config.ArtifactStoreRoot.Trim());
        hostAgentSettings["LocalArtifactCacheRoot"] = hostAgent.LocalArtifactCacheRoot;
        hostAgentSettings["DeployWebApps"] = hostAgent.DeployWebApps;
        hostAgentSettings["IisSiteName"] = hostAgent.IisSiteName;
        hostAgentSettings["EnsureIisSite"] = hostAgent.EnsureIisSite;
        hostAgentSettings["IisBindingProtocol"] = hostAgent.IisBindingProtocol;
        hostAgentSettings["IisBindingPort"] = hostAgent.IisBindingPort;
        hostAgentSettings["IisBindingHostHeader"] = hostAgent.IisBindingHostHeader;
        hostAgentSettings["IisBindingCertificateThumbprint"] = hostAgent.IisBindingCertificateThumbprint;
        hostAgentSettings["IisBindingCertificateSerialNumber"] = hostAgent.IisBindingCertificateSerialNumber;
        hostAgentSettings["IisBindingCertificateStoreName"] = hostAgent.IisBindingCertificateStoreName;
        hostAgentSettings["WebAppsRoot"] = hostAgent.WebAppsRoot;
        hostAgentSettings["PortalPhysicalPath"] = hostAgent.PortalPhysicalPath;
        hostAgentSettings["IisAppPoolNamePrefix"] = hostAgent.IisAppPoolNamePrefix;
        hostAgentSettings["IisAppPoolUserName"] = hostAgent.IisAppPoolUserName;
        hostAgentSettings["IisAppPoolPasswordCredentialKey"] = credentialPlan.DefaultIisAppPoolCredentialKey;
        hostAgentSettings.Remove("IisAppPoolPassword");
        if (credentialPlan.IisAppPoolOverrides.Count > 0)
        {
            hostAgentSettings["IisAppPoolOverrides"] = JsonSerializer.SerializeToNode(
                credentialPlan.IisAppPoolOverrides,
                JsonOptions);
        }
        else
        {
            hostAgentSettings.Remove("IisAppPoolOverrides");
        }

        hostAgentSettings["DeployServiceApps"] = hostAgent.DeployServiceApps;
        hostAgentSettings["ServicesRoot"] = hostAgent.ServicesRoot;
        hostAgentSettings["ServiceAppUserName"] = string.IsNullOrWhiteSpace(hostAgent.ServiceAppUserName)
            ? hostAgent.ServiceAccountName
            : hostAgent.ServiceAppUserName;
        hostAgentSettings["ServiceAppPasswordCredentialKey"] = credentialPlan.DefaultServiceAppCredentialKey;
        hostAgentSettings.Remove("ServiceAppPassword");
        if (credentialPlan.ServiceAppIdentityOverrides.Count > 0)
        {
            hostAgentSettings["ServiceAppIdentityOverrides"] = JsonSerializer.SerializeToNode(
                credentialPlan.ServiceAppIdentityOverrides,
                JsonOptions);
        }
        else
        {
            hostAgentSettings.Remove("ServiceAppIdentityOverrides");
        }

        var selfUpgrade = GetOrCreateJsonObject(hostAgentSettings, "SelfUpgrade");
        selfUpgrade["InstallRoot"] = hostAgent.ServicesRoot;
        selfUpgrade["ServiceNamePrefix"] = serviceIdentity.ServiceNamePrefix;
        selfUpgrade["ServiceAccountName"] = hostAgent.ServiceAccountName;
        selfUpgrade["ServiceAccountPasswordCredentialKey"] = credentialPlan.ServiceAccountCredentialKey;
        selfUpgrade.Remove("ServiceAccountPassword");

        var credentialStore = GetOrCreateJsonObject(hostAgentSettings, "CredentialStore");
        credentialStore["AutomationMode"] = credentialPlan.StoreSettings.AutomationMode;
        credentialStore["FilePath"] = credentialPlan.StoreSettings.FilePath;
        credentialStore["ProtectionScope"] = credentialPlan.StoreSettings.ProtectionScope;
        credentialStore["EntropyPurpose"] = credentialPlan.StoreSettings.EntropyPurpose;
    }

    internal static HostAgentCredentialBootstrapPlan CreateHostAgentCredentialPlan(
        BootstrapConfig config,
        string installPath)
    {
        var hostAgent = config.HostAgent;
        var credentials = new List<HostAgentPlainTextCredential>();
        var overrides = new Dictionary<string, HostAgentIisAppPoolIdentitySettings>(StringComparer.OrdinalIgnoreCase);

        var serviceAccountPassword = ResolveInstallerSecret(
            hostAgent.ServiceAccountPassword,
            config,
            "HostAgent:ServiceAccountPassword");
        var serviceAccountCredentialKey = ResolveCredentialKey(
            hostAgent.ServiceAccountCredentialKey,
            serviceAccountPassword,
            "hostagent:self-upgrade");
        AddCredentialIfConfigured(
            credentials,
            serviceAccountCredentialKey,
            hostAgent.ServiceAccountName,
            serviceAccountPassword);

        var defaultIisPassword = ResolveInstallerSecret(
            hostAgent.IisAppPoolPassword,
            config,
            "HostAgent:IisAppPoolPassword");
        var defaultIisCredentialKey = ResolveCredentialKey(
            hostAgent.IisAppPoolPasswordCredentialKey,
            defaultIisPassword,
            "iis:default");
        AddCredentialIfConfigured(
            credentials,
            defaultIisCredentialKey,
            hostAgent.IisAppPoolUserName,
            defaultIisPassword);

        var serviceAppPassword = string.IsNullOrWhiteSpace(hostAgent.ServiceAppPassword)
            ? serviceAccountPassword
            : ResolveInstallerSecret(
                hostAgent.ServiceAppPassword,
                config,
                "HostAgent:ServiceAppPassword");
        var serviceAppCredentialKey = ResolveCredentialKey(
            hostAgent.ServiceAppPasswordCredentialKey,
            serviceAppPassword,
            "service-app:default");
        var serviceAppUserName = string.IsNullOrWhiteSpace(hostAgent.ServiceAppUserName)
            ? hostAgent.ServiceAccountName
            : hostAgent.ServiceAppUserName;
        if (string.IsNullOrWhiteSpace(hostAgent.ServiceAppPasswordCredentialKey)
            && string.Equals(serviceAppUserName, hostAgent.ServiceAccountName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(serviceAppPassword, serviceAccountPassword, StringComparison.Ordinal))
        {
            serviceAppCredentialKey = serviceAccountCredentialKey;
        }

        AddCredentialIfConfigured(
            credentials,
            serviceAppCredentialKey,
            serviceAppUserName,
            serviceAppPassword);

        var serviceAppOverrides = new Dictionary<string, HostAgentServiceAppIdentitySettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in hostAgent.ServiceAppIdentityOverrides)
        {
            var configured = pair.Value;
            if (configured is null)
            {
                continue;
            }

            var overridePassword = ResolveInstallerSecret(
                configured.Password,
                config,
                $"HostAgent:ServiceAppIdentityOverrides:{pair.Key}:Password");
            var overrideCredentialKey = ResolveCredentialKey(
                configured.PasswordCredentialKey,
                overridePassword,
                "service-app:override:" + SanitizeCredentialKey(pair.Key));

            AddCredentialIfConfigured(
                credentials,
                overrideCredentialKey,
                configured.UserName,
                overridePassword);

            serviceAppOverrides[pair.Key] = new HostAgentServiceAppIdentitySettings
            {
                UserName = configured.UserName,
                PasswordCredentialKey = overrideCredentialKey
            };
        }

        foreach (var pair in hostAgent.IisAppPoolOverrides)
        {
            var configured = pair.Value;
            if (configured is null)
            {
                continue;
            }

            var overridePassword = ResolveInstallerSecret(
                configured.Password,
                config,
                $"HostAgent:IisAppPoolOverrides:{pair.Key}:Password");
            var overrideCredentialKey = ResolveCredentialKey(
                configured.PasswordCredentialKey,
                overridePassword,
                "iis:override:" + SanitizeCredentialKey(pair.Key));

            AddCredentialIfConfigured(
                credentials,
                overrideCredentialKey,
                configured.UserName,
                overridePassword);

            overrides[pair.Key] = new HostAgentIisAppPoolIdentitySettings
            {
                UserName = configured.UserName,
                PasswordCredentialKey = overrideCredentialKey
            };
        }

        var hasCredentialReferences = !string.IsNullOrWhiteSpace(serviceAccountCredentialKey)
            || !string.IsNullOrWhiteSpace(defaultIisCredentialKey)
            || !string.IsNullOrWhiteSpace(serviceAppCredentialKey)
            || serviceAppOverrides.Values.Any(static value => !string.IsNullOrWhiteSpace(value.PasswordCredentialKey))
            || overrides.Values.Any(static value => !string.IsNullOrWhiteSpace(value.PasswordCredentialKey));
        var storeSettings = CreateCredentialStoreSettings(
            hostAgent.CredentialStore,
            installPath,
            credentials.Count > 0 || hasCredentialReferences);

        if (credentials.Count > 0 && !storeSettings.IsEnabled())
        {
            throw new InvalidOperationException(
                "HostAgent credential store must be enabled when installer credentials are configured.");
        }

        return new HostAgentCredentialBootstrapPlan(
            storeSettings,
            serviceAccountCredentialKey,
            defaultIisCredentialKey,
            serviceAppCredentialKey,
            serviceAppOverrides,
            overrides,
            credentials);
    }

    internal static HostAgentCredentialStoreSettings CreateCredentialStoreSettings(
        HostAgentCredentialStoreBootstrapOptions options,
        string installPath,
        bool isRequired)
    {
        var automationMode = string.IsNullOrWhiteSpace(options.AutomationMode)
            ? (isRequired ? HostAgentCredentialAutomationModes.Full : HostAgentCredentialAutomationModes.Disabled)
            : options.AutomationMode.Trim();

        var filePath = string.IsNullOrWhiteSpace(options.FilePath) && isRequired
            ? Path.Join(Path.GetFullPath(installPath), "hostagent.credentials.json")
            : options.FilePath?.Trim() ?? string.Empty;

        var settings = new HostAgentCredentialStoreSettings
        {
            AutomationMode = automationMode,
            FilePath = filePath,
            ProtectionScope = string.IsNullOrWhiteSpace(options.ProtectionScope)
                ? HostAgentCredentialProtectionScopes.LocalMachine
                : options.ProtectionScope.Trim(),
            EntropyPurpose = string.IsNullOrWhiteSpace(options.EntropyPurpose)
                ? "OpenModulePlatform.HostAgent.CredentialStore.v1"
                : options.EntropyPurpose.Trim()
        };
        settings.Validate();
        return settings;
    }

    internal static async Task WriteHostAgentCredentialStoreAsync(
        HostAgentCredentialBootstrapPlan credentialPlan,
        string serviceAccountName)
    {
        if (credentialPlan.Credentials.Count == 0)
        {
            return;
        }

        var settings = credentialPlan.StoreSettings;
        settings.Validate();
        var path = settings.ResolveFilePath();
        var document = File.Exists(path)
            ? JsonSerializer.Deserialize<HostAgentCredentialStoreDocument>(
                await File.ReadAllTextAsync(path),
                JsonOptions) ?? new HostAgentCredentialStoreDocument()
            : new HostAgentCredentialStoreDocument();

        document.Credentials ??= new Dictionary<string, HostAgentStoredCredentialEntry>(StringComparer.OrdinalIgnoreCase);
        document.Credentials = new Dictionary<string, HostAgentStoredCredentialEntry>(
            document.Credentials,
            StringComparer.OrdinalIgnoreCase);

        foreach (var credential in credentialPlan.Credentials)
        {
            document.Credentials[credential.Key] = new HostAgentStoredCredentialEntry
            {
                UserName = credential.UserName,
                EncryptedPassword = HostAgentCredentialStoreService.ProtectPassword(credential.Password, settings),
                ProtectionProvider = "WindowsDpapi",
                ProtectionScope = settings.ProtectionScope,
                Description = "Written by OpenModulePlatform bootstrapper.",
                UpdatedUtc = DateTimeOffset.UtcNow
            };
        }

        document.UpdatedUtc = DateTimeOffset.UtcNow;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await AtomicJsonFile.WriteAsync(
            path,
            JsonSerializer.Serialize(document, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // Same protected ACL as set-hostagent-credential-store.ps1: Administrators,
        // SYSTEM, the installing identity and the HostAgent service account (B65).
        HostAgentCredentialStoreFileAcl.Apply(path, serviceAccountName);
    }

    internal static void AddCredentialIfConfigured(
        List<HostAgentPlainTextCredential> credentials,
        string key,
        string userName,
        string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("Credential key must be configured when a password is configured.");
        }

        credentials.Add(new HostAgentPlainTextCredential(key.Trim(), userName.Trim(), password));
    }

    internal static string ResolveCredentialKey(string configuredKey, string password, string defaultKey)
        => !string.IsNullOrWhiteSpace(configuredKey)
            ? configuredKey.Trim()
            : string.IsNullOrWhiteSpace(password)
                ? string.Empty
                : defaultKey;

    internal static string SanitizeCredentialKey(string value)
    {
        var chars = value
            .Trim()
            .Select(static ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_')
            .ToArray();
        var result = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(result) ? Guid.NewGuid().ToString("N") : result;
    }

    internal static string ResolveInstallerSecret(
        string? value,
        BootstrapConfig config,
        string fieldName)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (!IsPortableEncryptedSecret(value))
        {
            return value;
        }

        var key = ResolvePortableEncryptionKey(config, fieldName);
        return UnprotectPortableInstallerSecret(value.Trim(), key);
    }

    internal static bool IsPortableEncryptedSecret(string value)
        => value.TrimStart().StartsWith("enc:aesgcm:v1:", StringComparison.Ordinal);

    internal static byte[] ResolvePortableEncryptionKey(BootstrapConfig config, string fieldName)
    {
        var envName = config.Security.PortableEncryptionKeyEnvironmentVariable?.Trim();
        var keyText = !string.IsNullOrWhiteSpace(envName)
            ? Environment.GetEnvironmentVariable(envName)
            : null;
        keyText = string.IsNullOrWhiteSpace(keyText)
            ? config.Security.PortableEncryptionKey
            : keyText;

        if (string.IsNullOrWhiteSpace(keyText))
        {
            throw new InvalidOperationException(
                $"{fieldName} is encrypted, but Security:PortableEncryptionKey or Security:PortableEncryptionKeyEnvironmentVariable is not configured.");
        }

        var trimmed = keyText.Trim();
        if (trimmed.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
        {
            var decoded = Convert.FromBase64String(trimmed["base64:".Length..]);
            if (decoded.Length != 32)
            {
                throw new InvalidOperationException("Security:PortableEncryptionKey base64 value must decode to 32 bytes.");
            }

            return decoded;
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(trimmed));
    }

    internal static string UnprotectPortableInstallerSecret(string encryptedValue, byte[] key)
    {
        var parts = encryptedValue.Trim().Split(':');
        if (parts.Length != 6
            || parts[0] != "enc"
            || parts[1] != "aesgcm"
            || parts[2] != "v1")
        {
            throw new InvalidOperationException("Encrypted installer secret has an unsupported format.");
        }

        var nonce = Convert.FromBase64String(parts[3]);
        var cipherText = Convert.FromBase64String(parts[4]);
        var tag = Convert.FromBase64String(parts[5]);
        var plainText = new byte[cipherText.Length];
        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(nonce, cipherText, tag, plainText);
        return Encoding.UTF8.GetString(plainText);
    }

    internal static JsonObject GetOrCreateJsonObject(JsonObject parent, string propertyName)
    {
        foreach (var property in parent.ToArray())
        {
            if (!property.Key.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value is JsonObject existing)
            {
                return existing;
            }

            parent.Remove(property.Key);
            break;
        }

        var created = new JsonObject();
        parent[propertyName] = created;
        return created;
    }

    internal static JsonNode CreateDefaultHostAgentSettings(
        BootstrapConfig config,
        HostAgentBootstrapServiceIdentity serviceIdentity)
    {
        var hostAgent = config.HostAgent;
        var localArtifactCacheRoot = string.IsNullOrWhiteSpace(hostAgent.LocalArtifactCacheRoot)
            ? Path.Join(serviceIdentity.InstallPath, "ArtifactCache")
            : hostAgent.LocalArtifactCacheRoot.Trim();

        return JsonNode.Parse(
            JsonSerializer.Serialize(
                new
                {
                    ConnectionStrings = new
                    {
                        OmpDb = "{SqlConnectionString}"
                    },
                    HostAgent = new
                    {
                        hostAgent.ServiceName,
                        hostAgent.HostKey,
                        hostAgent.HostName,
                        RefreshSeconds = hostAgent.RefreshSeconds,
                        CentralArtifactRoot = "{ArtifactStoreRoot}",
                        LocalArtifactCacheRoot = localArtifactCacheRoot,
                        MaterializeTemplates = true,
                        ProcessHostDeployments = true,
                        ProvisionAppInstanceArtifacts = true,
                        ProvisionExplicitRequirements = true,
                        ArtifactZipImport = new
                        {
                            IsEnabled = false,
                            ImportPath = string.Empty,
                            ProcessedPath = string.Empty,
                            FailedPath = string.Empty,
                            MaxFilesPerCycle = 10,
                            CopyConfigurationFilesFromPreviousVersion = true
                        },
                        DeployWebApps = hostAgent.DeployWebApps,
                        IisSiteName = hostAgent.IisSiteName,
                        EnsureIisSite = hostAgent.EnsureIisSite,
                        IisBindingProtocol = hostAgent.IisBindingProtocol,
                        IisBindingPort = hostAgent.IisBindingPort,
                        IisBindingHostHeader = hostAgent.IisBindingHostHeader,
                        IisBindingCertificateThumbprint = hostAgent.IisBindingCertificateThumbprint,
                        IisBindingCertificateSerialNumber = hostAgent.IisBindingCertificateSerialNumber,
                        IisBindingCertificateStoreName = hostAgent.IisBindingCertificateStoreName,
                        WebAppsRoot = hostAgent.WebAppsRoot,
                        PortalPhysicalPath = hostAgent.PortalPhysicalPath,
                        IisAppPoolNamePrefix = hostAgent.IisAppPoolNamePrefix,
                        IisAppPoolUserName = hostAgent.IisAppPoolUserName,
                        IisAppPoolPasswordCredentialKey = string.Empty,
                        IisAppPoolOverrides = new Dictionary<string, object>(),
                        DeployServiceApps = hostAgent.DeployServiceApps,
                        ServicesRoot = hostAgent.ServicesRoot,
                        ServiceAppUserName = string.IsNullOrWhiteSpace(hostAgent.ServiceAppUserName)
                            ? hostAgent.ServiceAccountName
                            : hostAgent.ServiceAppUserName,
                        ServiceAppPasswordCredentialKey = string.Empty,
                        ServiceAppIdentityOverrides = new Dictionary<string, object>(),
                        SelfUpgrade = new
                        {
                            IsEnabled = true,
                            InstallRoot = hostAgent.ServicesRoot,
                            ServiceNamePrefix = hostAgent.ServiceName,
                            ServiceAccountName = hostAgent.ServiceAccountName,
                            ServiceAccountPasswordCredentialKey = string.Empty,
                            TakeoverStopTimeoutSeconds = 45,
                            DeletePreviousServiceAfterTakeover = true,
                            StartPreparedService = true
                        },
                        CredentialStore = new
                        {
                            AutomationMode = "Disabled",
                            FilePath = string.Empty,
                            ProtectionScope = "LocalMachine",
                            EntropyPurpose = "OpenModulePlatform.HostAgent.CredentialStore.v1"
                        },
                        EnableRpc = true,
                        RpcAllowedClientServiceNames = Array.Empty<string>()
                    }
                },
                JsonOptions))!;
    }

    internal static void ReplaceTokens(JsonNode node, IReadOnlyDictionary<string, string> tokens)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                if (property.Value is null)
                {
                    continue;
                }

                if (property.Value is JsonValue value
                    && value.TryGetValue<string>(out var text))
                {
                    obj[property.Key] = ReplaceTokenText(text, tokens);
                    continue;
                }

                ReplaceTokens(property.Value, tokens);
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue value
                    && value.TryGetValue<string>(out var text))
                {
                    array[i] = ReplaceTokenText(text, tokens);
                }
                else if (array[i] is not null)
                {
                    ReplaceTokens(array[i]!, tokens);
                }
            }
        }
    }

    internal static string ReplaceTokenText(string value, IReadOnlyDictionary<string, string> tokens)
    {
        var result = value;
        foreach (var token in tokens)
        {
            result = result.Replace("{" + token.Key + "}", token.Value, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    [SupportedOSPlatform("windows")]
    internal static bool IsWindowsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static void ValidateHostAgentServiceAccount(HostAgentInstallOptions hostAgent)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var account = NormalizeWindowsAccount(hostAgent.ServiceAccountName);
        if (string.IsNullOrWhiteSpace(account) || IsBuiltInServiceAccount(account))
        {
            return;
        }

        if (IsCurrentWindowsIdentity(account) && IsWindowsAdministrator())
        {
            return;
        }

        var canCheckDirectMembership = TryGetLocalAdministratorMembers(out var members, out var checkError);
        if (!canCheckDirectMembership)
        {
            InstallOutput.Info($"WARNING: Could not verify whether '{account}' is a local administrator: {checkError}");
            return;
        }

        var isDirectMember = members.Any(member => WindowsAccountEquals(member, account));
        if (isDirectMember)
        {
            return;
        }

        if (IsLocalMachineAccount(account))
        {
            throw new InvalidOperationException(
                $"HostAgent service account '{account}' is not listed as a local administrator. Add it to the local Administrators group before installing HostAgent, or choose another service account.");
        }

        InstallOutput.Info(
            $"WARNING: Could not confirm that HostAgent service account '{account}' is a direct local administrator. If access is granted through a nested domain group this may be fine; otherwise add it before installation.");
    }

    internal static bool TryGetLocalAdministratorMembers(
        out List<string> members,
        out string error)
    {
        members = [];
        error = string.Empty;

        try
        {
            var adminGroup = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
                .Translate(typeof(NTAccount))
                .Value
                .Split('\\')
                .Last();

            var result = RunProcess("net", ["localgroup", adminGroup], throwOnFailure: false);
            if (result.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut.Trim() : result.StdErr.Trim();
                return false;
            }

            // The member list follows the "---" separator and net.exe always closes a
            // successful listing with a localized completion line ("The command completed
            // successfully.", "Kommandot har utförts."), so drop the last non-empty line
            // instead of matching its text per locale.
            members.AddRange(
                result.StdOut
                    .Split([Environment.NewLine], StringSplitOptions.None)
                    .Select(static rawLine => rawLine.Trim())
                    .SkipWhile(static line => !line.StartsWith("---", StringComparison.Ordinal))
                    .Skip(1)
                    .Where(static line => line.Length > 0)
                    .SkipLast(1)
                    .Select(NormalizeWindowsAccount));

            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static bool IsCurrentWindowsIdentity(string account)
    {
        using var identity = WindowsIdentity.GetCurrent();
        return WindowsAccountEquals(identity.Name, account);
    }

    internal static bool IsLocalMachineAccount(string account)
    {
        var normalized = NormalizeWindowsAccount(account);
        var prefix = Environment.MachineName + "\\";
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsBuiltInServiceAccount(string account)
    {
        var normalized = NormalizeWindowsAccount(account);
        return normalized.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("NT AUTHORITY\\SYSTEM", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("NT AUTHORITY\\LOCAL SERVICE", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("NT AUTHORITY\\NETWORK SERVICE", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool WindowsAccountEquals(string left, string right)
        => NormalizeWindowsAccount(left).Equals(NormalizeWindowsAccount(right), StringComparison.OrdinalIgnoreCase);

    internal static string NormalizeWindowsAccount(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (trimmed.StartsWith(".\\", StringComparison.Ordinal))
        {
            return Environment.MachineName + "\\" + trimmed[2..];
        }

        if (!trimmed.Contains('\\', StringComparison.Ordinal)
            && !trimmed.Contains('@', StringComparison.Ordinal)
            && !IsBuiltInServiceAccountName(trimmed))
        {
            return Environment.MachineName + "\\" + trimmed;
        }

        return trimmed;
    }

    internal static bool IsBuiltInServiceAccountName(string value)
        => value.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
            || value.Equals("LocalService", StringComparison.OrdinalIgnoreCase)
            || value.Equals("NetworkService", StringComparison.OrdinalIgnoreCase);
}

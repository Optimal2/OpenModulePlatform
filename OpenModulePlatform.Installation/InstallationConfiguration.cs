using System.Text.Json.Nodes;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.Installation;

public sealed record PreparedArtifactConfigurationFiles(
    string ArtifactRelativePath,
    IReadOnlyList<ArtifactPackageConfigurationFile> ConfigurationFiles);

public sealed record ArtifactConfigurationCopyTarget(
    int ArtifactId,
    string Version,
    int ConfigurationFileCount);

public sealed record ConfiguredArtifactIdentity(
    string ModuleKey,
    string AppKey,
    string PackageType,
    string TargetName,
    string Version);

public sealed record AvailableArtifactPackage(
    ConfiguredArtifactIdentity Identity,
    string PackageRelativePath);


public sealed record ModuleDefinitionDocument(
    string ModuleKey,
    string DefinitionVersion,
    int FormatVersion,
    string DefinitionJson,
    string DefinitionSha256,
    IReadOnlyList<ModuleDefinitionCompatibilityEntry> CompatibleArtifacts);

public sealed record ModuleDefinitionCompatibilityEntry(
    string AppKey,
    string PackageType,
    string? TargetName,
    string? RelativePathTemplate,
    string? MinArtifactVersion,
    string? MaxArtifactVersion);

public sealed record AppliedModuleDefinition(
    int ModuleDefinitionDocumentId,
    string DefinitionVersion,
    string DefinitionSha256);

public sealed record PortableModuleDefinitionSqlScript(
    string Key,
    string Phase,
    string Scope,
    int Order,
    string Execution,
    string? Path,
    string? Source,
    string? InlineSql,
    string? ContentEncoding,
    string? Content,
    string? Sha256);

public sealed record ModuleDefinitionValidationResult(bool IsHealthy, string? Message);

public enum ArtifactPreparationMode
{
    InstallOrUpdate,
    AddMissingOnly
}

public sealed class BootstrapConfig
{
    public BootstrapProfileOptions Profile { get; set; } = new();

    public BootstrapSecurityOptions Security { get; set; } = new();

    public SqlBootstrapOptions Sql { get; set; } = new();

    public DeveloperSourceOptions DeveloperSource { get; set; } = new();

    public string ArtifactStoreRoot { get; set; } = string.Empty;

    public bool IncludeExampleApps { get; set; }

    public List<ArtifactPayloadOptions> Artifacts { get; set; } = [];

    public HostAgentInstallOptions HostAgent { get; set; } = new();

    // Preserve properties this model does not know about (schema, future
    // fields) across a typed round-trip, so the refresh merge of a package
    // config no longer silently drops them (R3-G2).
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement> ExtensionData { get; set; } = [];
}

public sealed class BootstrapProfileOptions
{
    public string DisplayName { get; set; } = string.Empty;

    public List<string> MachineNames { get; set; } = [];
}

public sealed class BootstrapSecurityOptions
{
    public string PortableEncryptionKey { get; set; } = string.Empty;

    public string PortableEncryptionKeyEnvironmentVariable { get; set; } = string.Empty;
}

public sealed class DeveloperSourceOptions
{
    public string SourceRoot { get; set; } = string.Empty;

    public string PackageConfigPath { get; set; } = string.Empty;

    public string PackageOutputRoot { get; set; } = string.Empty;
}

public sealed class SqlBootstrapOptions
{
    public bool Enabled { get; set; } = true;

    public string Server { get; set; } = "localhost";

    public string Database { get; set; } = "OpenModulePlatform";

    public bool IntegratedSecurity { get; set; } = true;

    public string UserId { get; set; } = string.Empty;

    public string? Password { get; set; }

    public bool TrustServerCertificate { get; set; }

    public bool CreateDatabase { get; set; }

    public int CommandTimeoutSeconds { get; set; } = 3600;

    public bool GrantRuntimeDatabaseAccess { get; set; }

    public string BootstrapPortalAdminPrincipal { get; set; } = string.Empty;

    public string BootstrapPortalAdminPrincipalType { get; set; } = "ADUser";

    public List<SqlScriptOptions> Scripts { get; set; } = [];

    public Dictionary<string, string> ArtifactVersionOverrides { get; set; } = new();

    public Dictionary<string, Dictionary<string, string>> ArtifactVersionVariableOverrides { get; set; } = new();
}

public sealed class SqlScriptOptions
{
    public bool Enabled { get; set; } = true;

    public string Path { get; set; } = string.Empty;
}

public sealed class ArtifactPayloadOptions
{
    public bool Enabled { get; set; } = true;

    public string Source { get; set; } = string.Empty;

    public string Target { get; set; } = string.Empty;

    public bool Overwrite { get; set; } = true;

    public bool RemoveRuntimeConfigurationFiles { get; set; } = true;

    public bool IsExample { get; set; }

    // Preserve properties this model does not know about across a typed
    // round-trip, same rationale as BootstrapConfig.ExtensionData (R3-G2):
    // the artifacts array is serialized back into host profile configs by
    // WriteSyncedArtifactTargetsIntoConfigAsync / the payload-metadata merge,
    // and without this a hand-added per-artifact property would vanish
    // silently on the next refresh-and-stage run.
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement> ExtensionData { get; set; } = [];
}

public sealed class HostAgentInstallOptions
{
    public bool Enabled { get; set; } = true;

    public string ServiceName { get; set; } = "OMP.HostAgent";

    public List<string> AdditionalServiceNamesToRemove { get; set; } = [];

    public string DisplayName { get; set; } = "OMP HostAgent";

    public string Description { get; set; } = "OpenModulePlatform artifact provisioning agent.";

    public string ServiceAccountName { get; set; } = string.Empty;

    public string ServiceAccountPassword { get; set; } = string.Empty;

    public string ServiceAccountCredentialKey { get; set; } = string.Empty;

    public string InstallPath { get; set; } = string.Empty;

    public string PackagePath { get; set; } = string.Empty;

    public bool BackupExistingInstall { get; set; } = true;

    public bool StartService { get; set; } = true;

    public string SettingsFileName { get; set; } = "appsettings.Production.json";

    public string LocalArtifactCacheRoot { get; set; } = string.Empty;

    public string HostKey { get; set; } = string.Empty;

    public string HostName { get; set; } = string.Empty;

    public int RefreshSeconds { get; set; } = 30;

    public bool DeployWebApps { get; set; } = true;

    public string IisSiteName { get; set; } = string.Empty;

    public bool EnsureIisSite { get; set; }

    public string IisBindingProtocol { get; set; } = "http";

    public int IisBindingPort { get; set; } = 80;

    public string IisBindingHostHeader { get; set; } = string.Empty;

    public string IisBindingCertificateThumbprint { get; set; } = string.Empty;

    public string IisBindingCertificateSerialNumber { get; set; } = string.Empty;

    public string IisBindingCertificateStoreName { get; set; } = "My";

    public string WebAppsRoot { get; set; } = string.Empty;

    public string PortalPhysicalPath { get; set; } = string.Empty;

    public string IisAppPoolNamePrefix { get; set; } = "OMP_";

    public string IisAppPoolUserName { get; set; } = string.Empty;

    public string IisAppPoolPassword { get; set; } = string.Empty;

    public string IisAppPoolPasswordCredentialKey { get; set; } = string.Empty;

    public Dictionary<string, IisAppPoolIdentityOptions> IisAppPoolOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool DeployServiceApps { get; set; } = true;

    public string ServicesRoot { get; set; } = string.Empty;

    public string ServiceAppUserName { get; set; } = string.Empty;

    public string ServiceAppPassword { get; set; } = string.Empty;

    public string ServiceAppPasswordCredentialKey { get; set; } = string.Empty;

    public Dictionary<string, ServiceAppIdentityOptions> ServiceAppIdentityOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public HostAgentCredentialStoreBootstrapOptions CredentialStore { get; set; } = new();

    public JsonNode? AppSettings { get; set; }
}

public sealed record HostAgentBootstrapServiceIdentity(
    string ServiceNamePrefix,
    string ServiceName,
    string InstallPath,
    string DisplayName,
    string? Version);

public sealed record WindowsServiceCandidate(
    string Name,
    string ExecutablePath);

public sealed class IisAppPoolIdentityOptions
{
    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string PasswordCredentialKey { get; set; } = string.Empty;
}

public sealed class ServiceAppIdentityOptions
{
    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string PasswordCredentialKey { get; set; } = string.Empty;
}

public sealed class HostAgentCredentialStoreBootstrapOptions
{
    public string AutomationMode { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public string ProtectionScope { get; set; } = HostAgentCredentialProtectionScopes.LocalMachine;

    public string EntropyPurpose { get; set; } = "OpenModulePlatform.HostAgent.CredentialStore.v1";
}

public sealed record HostAgentCredentialBootstrapPlan(
    HostAgentCredentialStoreSettings StoreSettings,
    string ServiceAccountCredentialKey,
    string DefaultIisAppPoolCredentialKey,
    string DefaultServiceAppCredentialKey,
    IReadOnlyDictionary<string, HostAgentServiceAppIdentitySettings> ServiceAppIdentityOverrides,
    IReadOnlyDictionary<string, HostAgentIisAppPoolIdentitySettings> IisAppPoolOverrides,
    IReadOnlyList<HostAgentPlainTextCredential> Credentials);

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

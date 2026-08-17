using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OpenModulePlatform.Auth.Services;
using OpenModulePlatform.TestSupport;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Provides a local SQL Server test database with the minimal OMP tables the
/// linked-user resolution and local-password sign-in flows touch (R7-F11,
/// R7-F15): omp.users, omp.auth_providers, omp.user_auth,
/// omp.auth_provider_lpwd and the omp configuration tables.
/// </summary>
public sealed class AuthResolutionTestFixture : IAsyncLifetime
{
    public const string DatabaseName = "OpenModulePlatform_PortalTests_AuthResolution";

    public string ConnectionString { get; } = TestSqlConnection.ForDatabase(DatabaseName);

    public async Task InitializeAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString)
        {
            InitialCatalog = "master"
        };
        await OmpTestDatabaseProvisioner.CreateDatabaseAsync(
            builder.ConnectionString,
            $"IF DB_ID(N'{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}];");

        await EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString)
        {
            InitialCatalog = "master"
        };

        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            $@"
ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE [{DatabaseName}];",
            conn);
        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqlException)
        {
            // Best-effort cleanup.
        }
    }

    public OmpAuthRepository CreateAuthRepository(IOmpLocalPasswordHasher? passwordHasher = null)
    {
        var connectionFactory = CreateConnectionFactory();
        var configurationService = new OmpConfigurationService(
            connectionFactory,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<OmpConfigurationService>.Instance);
        return new OmpAuthRepository(
            connectionFactory,
            passwordHasher ?? new OmpLocalPasswordHasher(new LocalPasswordHasher()),
            new WindowsPrincipalReader(NullLogger<WindowsPrincipalReader>.Instance),
            configurationService,
            NullLogger<OmpAuthRepository>.Instance);
    }

    public async Task<int> InsertUserAsync(string displayName, bool active)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
INSERT INTO omp.users(display_name, account_status)
OUTPUT INSERTED.user_id
VALUES(@display_name, @account_status);",
            conn);
        cmd.Parameters.AddWithValue("@display_name", displayName);
        cmd.Parameters.AddWithValue("@account_status", active ? 1 : 0);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task InsertLocalPasswordAsync(string userName, string password)
    {
        var passwordHash = new LocalPasswordHasher().Hash(password);

        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            "INSERT INTO omp.auth_provider_lpwd(user_name, password_hash) VALUES(@user_name, @password_hash);",
            conn);
        cmd.Parameters.AddWithValue("@user_name", userName);
        cmd.Parameters.AddWithValue("@password_hash", passwordHash);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task InsertAuthLinkAsync(int userId, string providerDisplayName, string providerUserKey)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
IF NOT EXISTS (SELECT 1 FROM omp.auth_providers WHERE display_name = @display_name)
BEGIN
    INSERT INTO omp.auth_providers(display_name, is_enabled)
    VALUES(@display_name, 1);
END

INSERT INTO omp.user_auth(user_id, provider_id, provider_user_key)
SELECT @user_id, p.provider_id, @provider_user_key
FROM omp.auth_providers p
WHERE p.display_name = @display_name;",
            conn);
        cmd.Parameters.AddWithValue("@user_id", userId);
        cmd.Parameters.AddWithValue("@display_name", providerDisplayName);
        cmd.Parameters.AddWithValue("@provider_user_key", providerUserKey);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Enables or disables an auth provider row, creating it when missing, so
    /// tests can drive the provider-disabled paths. EnsureProviderAsync in the
    /// repository self-heals a missing provider to enabled, so the disabled
    /// state must be written explicitly first.
    /// </summary>
    public async Task SetProviderEnabledAsync(string displayName, bool enabled)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
UPDATE omp.auth_providers
SET is_enabled = @is_enabled
WHERE display_name = @display_name;

IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO omp.auth_providers(display_name, is_enabled)
    VALUES(@display_name, @is_enabled);
END",
            conn);
        cmd.Parameters.AddWithValue("@display_name", displayName);
        cmd.Parameters.AddWithValue("@is_enabled", enabled);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Runs the R7-F12 canonicalization migration exactly as the core setup
    /// script ships it: the block is extracted from
    /// sql/1-setup-openmoduleplatform.sql between its begin/end markers, so the
    /// test exercises the shipped SQL rather than a copy of it.
    /// </summary>
    public async Task RunLocalPasswordCanonicalizationMigrationAsync()
    {
        var batches = ReadCoreSetupMigrationBatches();

        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        foreach (var batch in batches)
        {
            await using var cmd = new SqlCommand(batch, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static IReadOnlyList<string> ReadCoreSetupMigrationBatches()
    {
        var setupSql = File.ReadAllText(
            Path.Join(FindRepositoryRoot(), "sql", "1-setup-openmoduleplatform.sql"));

        const string beginMarker = "-- R7-F12: local password user-name canonicalization (begin)";
        const string endMarker = "-- R7-F12: local password user-name canonicalization (end)";

        var startIndex = setupSql.IndexOf(beginMarker, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, "Could not find the R7-F12 migration begin marker in the core setup script.");
        var endIndex = setupSql.IndexOf(endMarker, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex >= 0, "Could not find the R7-F12 migration end marker in the core setup script.");

        var block = setupSql[startIndex..endIndex];

        // Split on GO batch separators (SqlCommand cannot execute GO).
        var batches = new List<string>();
        var current = new List<string>();
        foreach (var rawLine in block.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.Equals(line.Trim(), "GO", StringComparison.OrdinalIgnoreCase))
            {
                batches.Add(string.Join('\n', current));
                current.Clear();
                continue;
            }

            current.Add(line);
        }

        batches.Add(string.Join('\n', current));
        return batches.Where(batch => !string.IsNullOrWhiteSpace(batch)).ToList();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "OpenModulePlatform.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate OpenModulePlatform repository root.");
    }

    /// <summary>
    /// Writes the global auth/selfRegistrationEnabled value the way the omp_auth
    /// seed does, so registration tests can turn the feature on deliberately.
    /// </summary>
    public async Task SetSelfRegistrationValueAsync(string? value)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
IF NOT EXISTS
(
    SELECT 1
    FROM omp.config_setting_definitions
    WHERE ConfigCategory = @category
      AND ConfigSetting = @setting
)
BEGIN
    INSERT INTO omp.config_setting_definitions(ConfigCategory, ConfigSetting)
    VALUES(@category, @setting);
END

DELETE cs
FROM omp.config_settings cs
INNER JOIN omp.config_setting_definitions def
    ON def.ConfigSettingId = cs.ConfigSettingId
WHERE def.ConfigCategory = @category
  AND def.ConfigSetting = @setting;

IF @value IS NOT NULL
BEGIN
    INSERT INTO omp.config_settings(ConfigSettingId, ConfigValue)
    SELECT def.ConfigSettingId, @value
    FROM omp.config_setting_definitions def
    WHERE def.ConfigCategory = @category
      AND def.ConfigSetting = @setting;
END",
            conn);
        cmd.Parameters.AddWithValue("@category", OmpAuthDefaults.ConfigurationCategory);
        cmd.Parameters.AddWithValue("@setting", OmpAuthDefaults.SelfRegistrationEnabledSetting);
        cmd.Parameters.AddWithValue("@value", value is null ? DBNull.Value : value);
        await cmd.ExecuteNonQueryAsync();
    }

    private SqlConnectionFactory CreateConnectionFactory()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OmpDb"] = ConnectionString
            })
            .Build();
        return new SqlConnectionFactory(configuration);
    }

    private async Task EnsureSchemaAsync()
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
IF SCHEMA_ID(N'omp') IS NULL EXEC(N'CREATE SCHEMA omp');

IF OBJECT_ID(N'omp.users', N'U') IS NULL
CREATE TABLE omp.users
(
    user_id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_omp_users PRIMARY KEY,
    display_name nvarchar(200) NOT NULL,
    account_status int NOT NULL CONSTRAINT DF_omp_users_account_status DEFAULT(1),
    security_stamp uniqueidentifier NOT NULL CONSTRAINT DF_omp_users_security_stamp DEFAULT NEWID(),
    last_login_at datetime2(3) NULL,
    created_at datetime2(3) NOT NULL CONSTRAINT DF_omp_users_created_at DEFAULT SYSUTCDATETIME(),
    updated_at datetime2(3) NOT NULL CONSTRAINT DF_omp_users_updated_at DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID(N'omp.auth_providers', N'U') IS NULL
CREATE TABLE omp.auth_providers
(
    provider_id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_omp_auth_providers PRIMARY KEY,
    display_name nvarchar(100) NOT NULL,
    is_enabled bit NOT NULL CONSTRAINT DF_omp_auth_providers_is_enabled DEFAULT(1)
);

IF OBJECT_ID(N'omp.user_auth', N'U') IS NULL
CREATE TABLE omp.user_auth
(
    user_auth_id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_omp_user_auth PRIMARY KEY,
    user_id int NOT NULL,
    provider_id int NOT NULL,
    provider_user_key nvarchar(1000) NOT NULL,
    last_used_at datetime2(3) NULL,
    auth_status nvarchar(20) NOT NULL CONSTRAINT DF_omp_user_auth_auth_status DEFAULT(N'enabled'),
    created_at datetime2(3) NOT NULL CONSTRAINT DF_omp_user_auth_created_at DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID(N'omp.auth_provider_lpwd', N'U') IS NULL
CREATE TABLE omp.auth_provider_lpwd
(
    user_name nvarchar(256) NOT NULL CONSTRAINT PK_omp_auth_provider_lpwd PRIMARY KEY,
    password_hash nvarchar(500) NOT NULL
);

IF OBJECT_ID(N'omp.config_setting_definitions', N'U') IS NULL
CREATE TABLE omp.config_setting_definitions
(
    ConfigSettingId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_omp_config_setting_definitions PRIMARY KEY,
    ConfigCategory nvarchar(100) NOT NULL,
    ConfigSetting nvarchar(200) NOT NULL,
    Description nvarchar(max) NULL,
    ValidationRegex nvarchar(500) NULL,
    ExampleValues nvarchar(500) NULL,
    SortOrder int NOT NULL CONSTRAINT DF_omp_config_setting_definitions_SortOrder DEFAULT(0),
    IsEnabled bit NOT NULL CONSTRAINT DF_omp_config_setting_definitions_IsEnabled DEFAULT(1)
);

IF OBJECT_ID(N'omp.config_settings', N'U') IS NULL
CREATE TABLE omp.config_settings
(
    ConfigId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_omp_config_settings PRIMARY KEY,
    ConfigSettingId int NOT NULL,
    ConfigValue nvarchar(max) NULL,
    ConfigUsr int NULL,
    ConfigPermission int NULL,
    ConfigRole int NULL,
    ConfigPriority int NOT NULL CONSTRAINT DF_omp_config_settings_ConfigPriority DEFAULT(0),
    ConfigScopeRank tinyint NOT NULL CONSTRAINT DF_omp_config_settings_ConfigScopeRank DEFAULT(0)
);",
            conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

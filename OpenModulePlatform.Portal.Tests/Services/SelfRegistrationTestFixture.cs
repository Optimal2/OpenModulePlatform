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
/// self-registration flow touches (R7-F17): the local-password auth tables the
/// auth repository writes, plus the omp configuration tables that carry the
/// auth/selfRegistrationEnabled flag.
/// </summary>
public sealed class SelfRegistrationTestFixture : IAsyncLifetime
{
    public const string DatabaseName = "OpenModulePlatform_PortalTests_SelfRegistration";

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

    public OmpAuthRepository CreateAuthRepository()
    {
        var connectionFactory = CreateConnectionFactory();
        // A fresh cache per repository: the configuration service caches global
        // reads, and the tests flip the flag between arrangements.
        var configurationService = new OmpConfigurationService(
            connectionFactory,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<OmpConfigurationService>.Instance);
        return new OmpAuthRepository(
            connectionFactory,
            new LocalPasswordHasher(),
            new WindowsPrincipalReader(NullLogger<WindowsPrincipalReader>.Instance),
            configurationService,
            NullLogger<OmpAuthRepository>.Instance);
    }

    /// <summary>
    /// Writes the global auth/selfRegistrationEnabled value the way the omp_auth
    /// seed does. A null value removes the row entirely, simulating an
    /// installation where the setting was never seeded.
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

    public async Task<bool> LocalPasswordUserExistsAsync(string userName)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            "SELECT COUNT(1) FROM omp.auth_provider_lpwd WHERE user_name = @user_name;",
            conn);
        cmd.Parameters.AddWithValue("@user_name", userName);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
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

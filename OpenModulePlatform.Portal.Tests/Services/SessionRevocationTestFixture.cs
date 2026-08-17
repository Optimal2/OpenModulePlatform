using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.TestSupport;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Provides a local SQL Server test database with the minimal OMP tables the
/// session revocation flow touches (R7-F10): omp.users with the security stamp
/// column, and the local-password auth tables the admin repository needs to
/// reset a password.
/// </summary>
public sealed class SessionRevocationTestFixture : IAsyncLifetime
{
    public const string DatabaseName = "OpenModulePlatform_PortalTests_SessionRevocation";

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

    public OmpUserAdminRepository CreateAdminRepository()
        => new(CreateConnectionFactory(), new LocalPasswordHasher());

    public OmpSqlSessionRevocationStore CreateRevocationStore()
    {
        var configurationService = new OmpConfigurationService(
            CreateConnectionFactory(),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<OmpConfigurationService>.Instance);
        return new OmpSqlSessionRevocationStore(CreateConnectionFactory(), configurationService);
    }

    public async Task<int> CreateUserWithLocalLoginAsync(string userName)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
INSERT INTO omp.users(display_name)
OUTPUT inserted.user_id
VALUES(N'Session Revocation Test User');",
            conn);
        var userId = Convert.ToInt32(await cmd.ExecuteScalarAsync());

        await using var linkCmd = new SqlCommand(
            @"
INSERT INTO omp.user_auth(user_id, provider_id, provider_user_key, auth_status, created_at)
VALUES(@user_id, @provider_id, @user_name, N'enabled', SYSUTCDATETIME());",
            conn);
        linkCmd.Parameters.AddWithValue("@user_id", userId);
        linkCmd.Parameters.AddWithValue("@provider_id", LocalPasswordProviderId);
        linkCmd.Parameters.AddWithValue("@user_name", userName);
        await linkCmd.ExecuteNonQueryAsync();

        var hasher = new LocalPasswordHasher();
        await using var passwordCmd = new SqlCommand(
            @"
INSERT INTO omp.auth_provider_lpwd(user_name, password_hash)
VALUES(@user_name, @password_hash);",
            conn);
        passwordCmd.Parameters.AddWithValue("@user_name", userName);
        passwordCmd.Parameters.AddWithValue("@password_hash", hasher.Hash("initial-password-1"));
        await passwordCmd.ExecuteNonQueryAsync();

        return userId;
    }

    public async Task<(int AccountStatus, Guid SecurityStamp)?> ReadAccountStateAsync(int userId)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            "SELECT account_status, security_stamp FROM omp.users WHERE user_id = @user_id;",
            conn);
        cmd.Parameters.AddWithValue("@user_id", userId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return (reader.GetInt32(0), reader.GetGuid(1));
    }

    private const int LocalPasswordProviderId = 1;

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

IF NOT EXISTS (SELECT 1 FROM omp.auth_providers WHERE provider_id = 1)
BEGIN
    SET IDENTITY_INSERT omp.auth_providers ON;
    INSERT INTO omp.auth_providers(provider_id, display_name, is_enabled)
    VALUES(1, N'lpwd', 1);
    SET IDENTITY_INSERT omp.auth_providers OFF;
END",
            conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

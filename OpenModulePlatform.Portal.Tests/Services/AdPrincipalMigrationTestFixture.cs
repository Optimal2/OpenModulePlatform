// File: OpenModulePlatform.Portal.Tests/Services/AdPrincipalMigrationTestFixture.cs
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.TestSupport;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Provides a local SQL Server test database with the minimal OMP tables the bulk
/// AD role-principal migration touches (campaign ad-principalformen-hela-vagen-adfs-till-rbac,
/// DEL 3): omp.users, omp.auth_providers, omp.user_auth, omp.Roles and
/// omp.RolePrincipals (with the real primary key on RoleId, PrincipalType, Principal).
/// Modeled on <see cref="AuthResolutionTestFixture"/>.
/// </summary>
public sealed class AdPrincipalMigrationTestFixture : IAsyncLifetime
{
    public const string DatabaseName = "OpenModulePlatform_PortalTests_AdPrincipalMigration";

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

    public AdRolePrincipalMigrationRepository CreateRepository()
        => new(CreateConnectionFactory());

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

    public async Task InsertAuthLinkAsync(
        int userId,
        string providerDisplayName,
        string providerUserKey,
        string authStatus = "enabled")
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

INSERT INTO omp.user_auth(user_id, provider_id, provider_user_key, auth_status)
SELECT @user_id, p.provider_id, @provider_user_key, @auth_status
FROM omp.auth_providers p
WHERE p.display_name = @display_name;",
            conn);
        cmd.Parameters.AddWithValue("@user_id", userId);
        cmd.Parameters.AddWithValue("@display_name", providerDisplayName);
        cmd.Parameters.AddWithValue("@provider_user_key", providerUserKey);
        cmd.Parameters.AddWithValue("@auth_status", authStatus);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> InsertRoleAsync(string name)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
INSERT INTO omp.Roles(Name)
OUTPUT INSERTED.RoleId
VALUES(@name);",
            conn);
        cmd.Parameters.AddWithValue("@name", name);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task InsertRolePrincipalAsync(int roleId, string principalType, string principal)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
INSERT INTO omp.RolePrincipals(RoleId, PrincipalType, Principal)
VALUES(@role_id, @principal_type, @principal);",
            conn);
        cmd.Parameters.AddWithValue("@role_id", roleId);
        cmd.Parameters.AddWithValue("@principal_type", principalType);
        cmd.Parameters.AddWithValue("@principal", principal);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> CountRolePrincipalsAsync(int roleId, string principalType, string principal)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
SELECT COUNT(1)
FROM omp.RolePrincipals
WHERE RoleId = @role_id
  AND PrincipalType = @principal_type
  AND Principal = @principal;",
            conn);
        cmd.Parameters.AddWithValue("@role_id", roleId);
        cmd.Parameters.AddWithValue("@principal_type", principalType);
        cmd.Parameters.AddWithValue("@principal", principal);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
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

        // Column types mirror sql/1-setup-openmoduleplatform.sql for the tables the
        // matching query touches, including the real RolePrincipals primary key.
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

IF OBJECT_ID(N'omp.Roles', N'U') IS NULL
CREATE TABLE omp.Roles
(
    RoleId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Name nvarchar(200) NOT NULL,
    Description nvarchar(500) NULL,
    CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_Roles_CreatedUtc DEFAULT SYSUTCDATETIME(),
    UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_Roles_UpdatedUtc DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_omp_Roles_Name UNIQUE(Name)
);

IF OBJECT_ID(N'omp.RolePrincipals', N'U') IS NULL
CREATE TABLE omp.RolePrincipals
(
    RoleId int NOT NULL,
    PrincipalType nvarchar(50) NOT NULL,
    Principal nvarchar(256) NOT NULL,
    CONSTRAINT PK_omp_RolePrincipals PRIMARY KEY(RoleId, PrincipalType, Principal),
    CONSTRAINT FK_omp_RolePrincipals_Role FOREIGN KEY(RoleId) REFERENCES omp.Roles(RoleId)
);",
            conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

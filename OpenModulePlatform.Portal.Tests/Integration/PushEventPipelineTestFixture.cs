using Microsoft.Data.SqlClient;
using System.Globalization;

namespace OpenModulePlatform.Portal.Tests.Integration;

/// <summary>
/// Shared test fixture that ensures a local SQL Server database exists with the
/// minimal OMP schema required to exercise the push event pipeline end-to-end.
/// </summary>
public sealed class PushEventPipelineTestFixture : IAsyncLifetime
{
    public const string DatabaseName = "OpenModulePlatform_PortalTests_PushEvents";

    public string ConnectionString { get; } = TestSqlConnection.ForDatabase(DatabaseName);

    public const int TestUserId = 42;

    public PortalWebApplicationFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await EnsureDatabaseExistsAsync();
        await EnsureSchemaExistsAsync();
        await EnsureTestUserExistsAsync();
        await CleanOutboxAsync();

        Factory = new PortalWebApplicationFactory(this);
    }

    public async Task DisposeAsync()
    {
        await CleanOutboxAsync();
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }
    }

    public async Task CleanOutboxAsync()
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("DELETE FROM omp.push_event_outbox;", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task EnsureDatabaseExistsAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString)
        {
            InitialCatalog = "master"
        };

        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            $"IF DB_ID(N'{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}];",
            conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task EnsureSchemaExistsAsync()
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var schemaCmd = new SqlCommand(
            "IF SCHEMA_ID(N'omp') IS NULL EXEC(N'CREATE SCHEMA omp');",
            conn);
        await schemaCmd.ExecuteNonQueryAsync();

        await using var usersCmd = new SqlCommand(
            @"
IF OBJECT_ID(N'omp.users', N'U') IS NULL
CREATE TABLE omp.users
(
    user_id int IDENTITY(1,1) NOT NULL,
    display_name nvarchar(200) NOT NULL,
    account_status int NOT NULL CONSTRAINT DF_omp_users_account_status DEFAULT(1),
    created_at datetime2(3) NOT NULL CONSTRAINT DF_omp_users_created_at DEFAULT SYSUTCDATETIME(),
    updated_at datetime2(3) NOT NULL CONSTRAINT DF_omp_users_updated_at DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_omp_users PRIMARY KEY(user_id)
);",
            conn);
        await usersCmd.ExecuteNonQueryAsync();

        await using var outboxCmd = new SqlCommand(
            @"
IF OBJECT_ID(N'omp.push_event_outbox', N'U') IS NULL
CREATE TABLE omp.push_event_outbox
(
    push_event_id bigint IDENTITY(1,1) NOT NULL,
    event_category nvarchar(80) NOT NULL,
    target_type nvarchar(40) NOT NULL CONSTRAINT DF_omp_push_event_outbox_target_type DEFAULT(N'user'),
    target_user_id int NULL,
    target_json nvarchar(2048) NOT NULL,
    payload_json nvarchar(max) NULL,
    deduplication_key nvarchar(200) NULL,
    correlation_key nvarchar(200) NULL,
    status nvarchar(40) NOT NULL CONSTRAINT DF_omp_push_event_outbox_status DEFAULT(N'pending'),
    lease_token uniqueidentifier NULL,
    lease_owner nvarchar(200) NULL,
    lease_until_utc datetime2(3) NULL,
    retry_count int NOT NULL CONSTRAINT DF_omp_push_event_outbox_retry_count DEFAULT(0),
    max_retries int NOT NULL CONSTRAINT DF_omp_push_event_outbox_max_retries DEFAULT(5),
    error_message nvarchar(2048) NULL,
    created_utc datetime2(3) NOT NULL CONSTRAINT DF_omp_push_event_outbox_created_utc DEFAULT SYSUTCDATETIME(),
    scheduled_utc datetime2(3) NOT NULL CONSTRAINT DF_omp_push_event_outbox_scheduled_utc DEFAULT SYSUTCDATETIME(),
    dispatched_utc datetime2(3) NULL,
    completed_utc datetime2(3) NULL,
    dead_lettered_utc datetime2(3) NULL,
    CONSTRAINT PK_omp_push_event_outbox PRIMARY KEY(push_event_id),
    CONSTRAINT FK_omp_push_event_outbox_user FOREIGN KEY(target_user_id) REFERENCES omp.users(user_id)
);",
            conn);
        await outboxCmd.ExecuteNonQueryAsync();
    }

    private async Task EnsureTestUserExistsAsync()
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
SET IDENTITY_INSERT omp.users ON;
IF NOT EXISTS (SELECT 1 FROM omp.users WHERE user_id = @user_id)
    INSERT INTO omp.users (user_id, display_name) VALUES (@user_id, @display_name);
SET IDENTITY_INSERT omp.users OFF;",
            conn);
        cmd.Parameters.Add("@user_id", System.Data.SqlDbType.Int).Value = TestUserId;
        cmd.Parameters.Add("@display_name", System.Data.SqlDbType.NVarChar, 200).Value = "Test User";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> CountOutboxRowsAsync()
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM omp.push_event_outbox;", conn);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<OutboxRowStatus>> GetOutboxStatusesAsync()
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            @"
SELECT push_event_id,
       event_category,
       target_type,
       target_user_id,
       status,
       retry_count,
       error_message
FROM omp.push_event_outbox
ORDER BY push_event_id;",
            conn);

        var rows = new List<OutboxRowStatus>();
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            rows.Add(new OutboxRowStatus(
                rdr.GetInt64(0),
                rdr.GetString(1),
                rdr.GetString(2),
                rdr.IsDBNull(3) ? null : rdr.GetInt32(3),
                rdr.GetString(4),
                rdr.GetInt32(5),
                rdr.IsDBNull(6) ? null : rdr.GetString(6)));
        }

        return rows;
    }

    public sealed record OutboxRowStatus(
        long PushEventId,
        string EventCategory,
        string TargetType,
        int? TargetUserId,
        string Status,
        int RetryCount,
        string? ErrorMessage);
}

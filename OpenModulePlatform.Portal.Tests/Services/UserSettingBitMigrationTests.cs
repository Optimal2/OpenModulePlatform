using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.TestSupport;
using OpenModulePlatform.Web.Shared.Services;
using System.Data;
using System.Text.RegularExpressions;
using Xunit;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// The on/off user settings moved from the int table to the bit table on
/// 2026-10-20 (schema migration 20261020-user-settings-bool-to-bit). These
/// tests run the real Portal setup script against a real database: once on a
/// fresh install, once over a database put back into the pre-migration shape
/// with stored values, and once more to prove the script is idempotent. The
/// service that reads and writes the settings is exercised on top.
/// </summary>
public sealed class UserSettingBitMigrationTests : IClassFixture<UserSettingBitMigrationFixture>
{
    private const string MigrationKey = "20261020-user-settings-bool-to-bit";

    private static readonly (string Name, bool Default)[] BoolSettings =
    [
        ("AdminMetricsCollapsed", false),
        ("TopbarDropdownsOpenOnHover", true),
        ("ShowPortalNavbar", true),
        ("NotificationToastsMuted", false),
        ("NotificationSoundsEnabled", true),
        ("PageSearchOverridesCtrlF", false),
    ];

    private readonly UserSettingBitMigrationFixture _fixture;

    public UserSettingBitMigrationTests(UserSettingBitMigrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FreshInstall_SeedsTheOnOffSettingsAsBitsAndTheNewDefinitions()
    {
        await _fixture.RunPortalSetupAsync();

        foreach (var (name, defaultValue) in BoolSettings)
        {
            var definition = await _fixture.GetDefinitionAsync("Portal", name);
            Assert.NotNull(definition);
            Assert.Equal(3, definition.Value.ValueKind);
            Assert.Equal(defaultValue, definition.Value.DefaultBit);
            Assert.Null(definition.Value.DefaultInt);
        }

        var deep = await _fixture.GetDefinitionAsync("Search", "DeepByDefault");
        Assert.NotNull(deep);
        Assert.Equal(3, deep.Value.ValueKind);
        Assert.False(deep.Value.DefaultBit);

        var theme = await _fixture.GetDefinitionAsync("Appearance", "Theme");
        Assert.NotNull(theme);
        Assert.Equal(1, theme.Value.ValueKind);
        Assert.Equal(0, theme.Value.DefaultInt);

        var culture = await _fixture.GetDefinitionAsync("Appearance", "Culture");
        Assert.NotNull(culture);
        Assert.Equal(2, culture.Value.ValueKind);

        Assert.True(await _fixture.MigrationAppliedAsync(MigrationKey));
    }

    [Fact]
    public async Task PreMigrationDatabase_KeepsEveryStoredValueAndTimestampAcrossTheMove()
    {
        await _fixture.RunPortalSetupAsync();
        var userId = await _fixture.InsertUserAsync("bit-migration");

        // Put the database back into the shape an install had before the
        // migration: the six definitions int-backed, two of them with stored
        // values, and no record of the migration.
        var stamp = new DateTime(2026, 10, 1, 12, 30, 0, DateTimeKind.Utc);
        await _fixture.RevertToIntBackedAsync(BoolSettings.Select(s => (s.Name, s.Default)).ToArray());
        await _fixture.InsertIntValueAsync(userId, "PageSearchOverridesCtrlF", 1, stamp);
        await _fixture.InsertIntValueAsync(userId, "TopbarDropdownsOpenOnHover", 0, stamp);
        Assert.False(await _fixture.MigrationAppliedAsync(MigrationKey));
        Assert.False(await _fixture.DefaultBitColumnExistsAsync());
        Assert.False(await _fixture.DefinitionExistsAsync("Search", "DeepByDefault"));

        await _fixture.RunPortalSetupAsync();

        Assert.True(await _fixture.MigrationAppliedAsync(MigrationKey));
        Assert.True(await _fixture.DefaultBitColumnExistsAsync());
        Assert.NotNull(await _fixture.GetDefinitionAsync("Search", "DeepByDefault"));
        Assert.Equal(0, await _fixture.CountIntRowsAsync(userId));
        var ctrlF = await _fixture.GetBitValueAsync(userId, "PageSearchOverridesCtrlF");
        Assert.NotNull(ctrlF);
        Assert.True(ctrlF.Value.Value);
        Assert.Equal(stamp, ctrlF.Value.UpdatedAt);
        var hover = await _fixture.GetBitValueAsync(userId, "TopbarDropdownsOpenOnHover");
        Assert.NotNull(hover);
        Assert.False(hover.Value.Value);
        Assert.Equal(stamp, hover.Value.UpdatedAt);
        foreach (var (name, defaultValue) in BoolSettings)
        {
            var definition = await _fixture.GetDefinitionAsync("Portal", name);
            Assert.Equal(3, definition!.Value.ValueKind);
            Assert.Equal(defaultValue, definition.Value.DefaultBit);
        }

        // A second run must change nothing: the migration key stops the move
        // and the MERGE finds the definitions already as kind 3.
        await _fixture.RunPortalSetupAsync();
        Assert.Equal(2, await _fixture.CountBitRowsAsync(userId));
        Assert.Equal(stamp, (await _fixture.GetBitValueAsync(userId, "PageSearchOverridesCtrlF"))!.Value.UpdatedAt);
    }

    [Fact]
    public async Task Service_RoundTripsAnOnOffSettingThroughTheBitTable()
    {
        await _fixture.RunPortalSetupAsync();
        var userId = await _fixture.InsertUserAsync("bit-service");
        var service = new PortalUserSettingsService(_fixture.CreateConnectionFactory());

        Assert.False((await service.GetForUserAsync(userId, CancellationToken.None)).PageSearchOverridesCtrlF);

        await service.UpsertPageSearchOverridesCtrlFAsync(userId, true, CancellationToken.None);
        Assert.True((await service.GetForUserAsync(userId, CancellationToken.None)).PageSearchOverridesCtrlF);
        var account = await service.GetAccountSettingsAsync(userId, CancellationToken.None);
        Assert.NotNull(account);
        Assert.True(account.PageSearchOverridesCtrlF);
        Assert.True(account.TopbarDropdownsOpenOnHover);
        Assert.Equal(1, await _fixture.CountBitRowsAsync(userId));

        // Saving the default removes the override row, as the int table did.
        await service.UpsertPageSearchOverridesCtrlFAsync(userId, false, CancellationToken.None);
        Assert.Equal(0, await _fixture.CountBitRowsAsync(userId));
        Assert.False((await service.GetForUserAsync(userId, CancellationToken.None)).PageSearchOverridesCtrlF);
    }

    [Fact]
    public async Task SharedWriter_StoresTheMuteToggleAndRemovesTheRowAtDefault()
    {
        await _fixture.RunPortalSetupAsync();
        var userId = await _fixture.InsertUserAsync("bit-writer");
        var writer = new PortalUserBitSettingWriter(_fixture.CreateConnectionFactory());
        var service = new PortalUserSettingsService(_fixture.CreateConnectionFactory());

        // The top bar's toggle writes the same setting the Portal's settings page reads.
        Assert.True(await writer.SetAsync(userId, "Portal", "NotificationToastsMuted", true, CancellationToken.None));
        Assert.True((await service.GetForUserAsync(userId, CancellationToken.None)).NotificationToastsMuted);
        Assert.Equal(1, await _fixture.CountBitRowsAsync(userId));

        Assert.True(await writer.SetAsync(userId, "Portal", "NotificationToastsMuted", false, CancellationToken.None));
        Assert.False((await service.GetForUserAsync(userId, CancellationToken.None)).NotificationToastsMuted);
        Assert.Equal(0, await _fixture.CountBitRowsAsync(userId));

        // An unknown definition is reported, not swallowed.
        Assert.False(await writer.SetAsync(userId, "Portal", "NoSuchSetting", true, CancellationToken.None));
    }

    [Fact]
    public async Task AdminRepository_SavesReadsAndDeletesABitValue()
    {
        await _fixture.RunPortalSetupAsync();
        var userId = await _fixture.InsertUserAsync("bit-admin");
        var repository = new PortalUserSettingsAdminRepository(_fixture.CreateConfiguration());

        var definitions = await repository.GetDefinitionsAsync(CancellationToken.None);
        var hover = Assert.Single(definitions, d => d.Key == "Portal/TopbarDropdownsOpenOnHover");
        Assert.Equal(PortalUserSettingsAdminRepository.BitValueKind, hover.ValueKind);
        Assert.True(hover.DefaultBitValue);

        Assert.True(await repository.SaveValueAsync(
            userId,
            new PortalUserSettingValueEditData(hover.UserSettingDefinitionId, hover.ValueKind, null, null, false),
            CancellationToken.None));
        var row = Assert.Single(await repository.GetValuesForUserAsync(userId, CancellationToken.None));
        Assert.Equal(hover.UserSettingDefinitionId, row.UserSettingDefinitionId);
        Assert.False(row.BitValue);
        Assert.True(row.DefaultBitValue);

        // Saving the default value removes the override instead of storing it.
        Assert.True(await repository.SaveValueAsync(
            userId,
            new PortalUserSettingValueEditData(hover.UserSettingDefinitionId, hover.ValueKind, null, null, true),
            CancellationToken.None));
        Assert.Empty(await repository.GetValuesForUserAsync(userId, CancellationToken.None));

        // A bit definition refuses an edit that carries no bit value.
        Assert.False(await repository.SaveValueAsync(
            userId,
            new PortalUserSettingValueEditData(hover.UserSettingDefinitionId, hover.ValueKind, 1, null, null),
            CancellationToken.None));
    }
}

public sealed class UserSettingBitMigrationFixture : IAsyncLifetime
{
    public static readonly string DatabaseName = OmpTestDatabaseNames.ForPortalTests("UserSettingBits");

    public string ConnectionString { get; } = TestSqlConnection.ForDatabase(DatabaseName);

    private readonly SemaphoreSlim _setupGate = new(1, 1);

    public IConfiguration CreateConfiguration()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:OmpDb"] = ConnectionString })
            .Build();

    public SqlConnectionFactory CreateConnectionFactory()
        => new(CreateConfiguration());

    public async Task InitializeAsync()
    {
        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await OmpTestDatabaseProvisioner.CreateDatabaseAsync(
            master.ConnectionString,
            $"IF DB_ID(N'{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}];");
        await RunScriptAsync(OmpRepositoryFiles.ReadRepositoryTextFile("sql", "1-setup-openmoduleplatform.sql"));
    }

    /// <summary>Runs the Portal setup script and then the targeted user-setting patch that the
    /// module definition runs after it (order 40), serialized so the tests in this class never
    /// interleave their runs. Both are idempotent and the migration keyed, so repeated runs are
    /// exactly what an installation sees across imports, and the patch running after setup is
    /// exactly the order that once wrote three definitions back to int.</summary>
    public async Task RunPortalSetupAsync()
    {
        await _setupGate.WaitAsync();
        try
        {
            await RunScriptAsync(OmpRepositoryFiles.ReadRepositoryTextFile("OpenModulePlatform.Portal", "sql", "1-setup-omp-portal.sql"));
            await RunScriptAsync(OmpRepositoryFiles.ReadRepositoryTextFile("OpenModulePlatform.Portal", "sql", "4-ensure-topbar-hover-user-setting.sql"));
        }
        finally
        {
            _setupGate.Release();
        }
    }

    public async Task<int> InsertUserAsync(string displayName)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "INSERT INTO omp.users (display_name) VALUES (@name); SELECT CAST(SCOPE_IDENTITY() AS int);", conn);
        cmd.Parameters.AddWithValue("@name", displayName + "-" + Guid.NewGuid().ToString("N")[..8]);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<(byte ValueKind, int? DefaultInt, bool? DefaultBit)?> GetDefinitionAsync(string category, string name)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            """
            SELECT value_kind, default_int_value, default_bit_value
            FROM omp_portal.user_setting_definitions
            WHERE setting_category = @category AND setting_name = @name;
            """, conn);
        cmd.Parameters.AddWithValue("@category", category);
        cmd.Parameters.AddWithValue("@name", name);
        await using var rdr = await cmd.ExecuteReaderAsync();
        if (!await rdr.ReadAsync())
        {
            return null;
        }

        return (rdr.GetByte(0), rdr.IsDBNull(1) ? null : rdr.GetInt32(1), rdr.IsDBNull(2) ? null : rdr.GetBoolean(2));
    }

    public async Task<bool> MigrationAppliedAsync(string key)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT COUNT(*) FROM omp_portal.schema_migrations WHERE migration_key = @key;", conn);
        cmd.Parameters.AddWithValue("@key", key);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
    }

    /// <summary>Puts the database back into the shape an install had before kind 3 existed: the
    /// six on/off definitions int-backed, no bit-backed definition at all, the two-kind CHECK
    /// constraints, no default_bit_value column, and no record of the migration. The next setup
    /// run then has to add the column, rewrite the constraints and perform the move, which is the
    /// whole upgrade path. Bit rows are removed first because the definition's kind cannot change
    /// while rows reference it.</summary>
    public async Task RevertToIntBackedAsync((string Name, bool Default)[] settings)
    {
        await using var conn = await OpenAsync();
        foreach (var (name, defaultValue) in settings)
        {
            await using var cmd = new SqlCommand(
                """
                DELETE v
                FROM omp_portal.user_setting_bit_values v
                INNER JOIN omp_portal.user_setting_definitions d ON d.user_setting_definition_id = v.user_setting_definition_id
                WHERE d.setting_category = N'Portal' AND d.setting_name = @name;

                UPDATE omp_portal.user_setting_definitions
                SET value_kind = 1,
                    default_int_value = @default_int,
                    default_bit_value = NULL
                WHERE setting_category = N'Portal' AND setting_name = @name;
                """, conn);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@default_int", defaultValue ? 1 : 0);
            await cmd.ExecuteNonQueryAsync();
        }

        await using var legacyShape = new SqlCommand(
            """
            DELETE v
            FROM omp_portal.user_setting_bit_values v
            INNER JOIN omp_portal.user_setting_definitions d ON d.user_setting_definition_id = v.user_setting_definition_id
            WHERE d.value_kind = 3;
            DELETE FROM omp_portal.user_setting_definitions WHERE value_kind = 3;

            ALTER TABLE omp_portal.user_setting_definitions
                DROP CONSTRAINT CK_omp_portal_user_setting_definitions_value_kind,
                                CK_omp_portal_user_setting_definitions_default_value;
            ALTER TABLE omp_portal.user_setting_definitions DROP COLUMN default_bit_value;
            ALTER TABLE omp_portal.user_setting_definitions
                ADD CONSTRAINT CK_omp_portal_user_setting_definitions_value_kind CHECK(value_kind IN (1, 2)),
                    CONSTRAINT CK_omp_portal_user_setting_definitions_default_value CHECK
                    (
                        (value_kind = 1 AND default_string_value IS NULL)
                        OR (value_kind = 2 AND default_int_value IS NULL)
                    );

            DELETE FROM omp_portal.schema_migrations WHERE migration_key = N'20261020-user-settings-bool-to-bit';
            """, conn);
        await legacyShape.ExecuteNonQueryAsync();
    }

    public async Task<bool> DefinitionExistsAsync(string category, string name)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT COUNT(*) FROM omp_portal.user_setting_definitions WHERE setting_category = @category AND setting_name = @name;", conn);
        cmd.Parameters.AddWithValue("@category", category);
        cmd.Parameters.AddWithValue("@name", name);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
    }

    public async Task<bool> DefaultBitColumnExistsAsync()
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT CASE WHEN COL_LENGTH(N'omp_portal.user_setting_definitions', N'default_bit_value') IS NULL THEN 0 ELSE 1 END;", conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1;
    }

    public async Task InsertIntValueAsync(int userId, string name, int value, DateTime updatedAtUtc)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            """
            INSERT INTO omp_portal.user_setting_int_values(user_id, user_setting_definition_id, value_kind, setting_value, updated_at)
            SELECT @user_id, user_setting_definition_id, 1, @value, @updated_at
            FROM omp_portal.user_setting_definitions
            WHERE setting_category = N'Portal' AND setting_name = @name AND value_kind = 1;
            """, conn);
        cmd.Parameters.AddWithValue("@user_id", userId);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.Parameters.Add("@updated_at", SqlDbType.DateTime2).Value = updatedAtUtc;
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
    }

    public async Task<(bool Value, DateTime UpdatedAt)?> GetBitValueAsync(int userId, string name)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            """
            SELECT v.setting_value, v.updated_at
            FROM omp_portal.user_setting_bit_values v
            INNER JOIN omp_portal.user_setting_definitions d ON d.user_setting_definition_id = v.user_setting_definition_id
            WHERE v.user_id = @user_id AND d.setting_category = N'Portal' AND d.setting_name = @name;
            """, conn);
        cmd.Parameters.AddWithValue("@user_id", userId);
        cmd.Parameters.AddWithValue("@name", name);
        await using var rdr = await cmd.ExecuteReaderAsync();
        if (!await rdr.ReadAsync())
        {
            return null;
        }

        return (rdr.GetBoolean(0), DateTime.SpecifyKind(rdr.GetDateTime(1), DateTimeKind.Utc));
    }

    public Task<int> CountIntRowsAsync(int userId)
        => CountAsync("omp_portal.user_setting_int_values", userId);

    public Task<int> CountBitRowsAsync(int userId)
        => CountAsync("omp_portal.user_setting_bit_values", userId);

    public async Task DisposeAsync()
    {
        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using var conn = new SqlConnection(master.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}];", conn);
        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqlException ex)
        {
            OmpTestCleanupLog.RecordFailure(
                nameof(UserSettingBitMigrationTests),
                $"Could not drop test database '{DatabaseName}': {ex.Message}");
        }
    }

    private async Task<int> CountAsync(string table, int userId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand($"SELECT COUNT(*) FROM {table} WHERE user_id = @user_id;", conn);
        cmd.Parameters.AddWithValue("@user_id", userId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task<SqlConnection> OpenAsync()
    {
        var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private async Task RunScriptAsync(string script)
    {
        var withoutUse = Regex.Replace(
            script,
            @"^\s*USE\s+\[OpenModulePlatform\]\s*;\s*\r?\n\s*GO\s*(?:--.*)?\s*(?:\r?\n)?",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        await using var conn = await OpenAsync();
        foreach (var batch in Regex.Split(withoutUse, @"^\s*GO\s*$", RegexOptions.Multiline).Where(b => !string.IsNullOrWhiteSpace(b)))
        {
            await using var cmd = new SqlCommand(batch, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}

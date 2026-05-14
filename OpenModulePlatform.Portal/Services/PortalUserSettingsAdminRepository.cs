using Microsoft.Data.SqlClient;
using System.Data;

namespace OpenModulePlatform.Portal.Services;

public sealed class PortalUserSettingsAdminRepository
{
    public const byte IntValueKind = 1;
    public const byte StringValueKind = 2;

    private readonly string _connectionString;

    public PortalUserSettingsAdminRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("OmpDb")
            ?? throw new InvalidOperationException("Connection string 'OmpDb' is not configured.");
    }

    public async Task<IReadOnlyList<PortalUserSettingDefinitionRow>> GetDefinitionsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT user_setting_definition_id,
                   setting_category,
                   setting_name,
                   value_kind,
                   default_int_value,
                   default_string_value,
                   description,
                   sort_order,
                   is_enabled
            FROM omp_portal.user_setting_definitions
            ORDER BY setting_category, sort_order, setting_name;
            """;

        var rows = new List<PortalUserSettingDefinitionRow>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadDefinition(reader));
        }

        return rows;
    }

    public async Task<IReadOnlyList<PortalUserSettingValueRow>> GetValuesForUserAsync(int userId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT *
            FROM
            (
                SELECT v.user_id AS UserId,
                       d.user_setting_definition_id AS UserSettingDefinitionId,
                       d.setting_category AS SettingCategory,
                       d.setting_name AS SettingName,
                       d.value_kind AS ValueKind,
                       d.default_int_value AS DefaultIntValue,
                       d.default_string_value AS DefaultStringValue,
                       d.description AS Description,
                       d.sort_order AS SortOrder,
                       d.is_enabled AS IsEnabled,
                       v.setting_value AS IntValue,
                       CAST(NULL AS nvarchar(max)) AS StringValue,
                       v.updated_at AS UpdatedAt
                FROM omp_portal.user_setting_int_values v
                INNER JOIN omp_portal.user_setting_definitions d
                    ON d.user_setting_definition_id = v.user_setting_definition_id
                   AND d.value_kind = v.value_kind
                WHERE v.user_id = @user_id

                UNION ALL

                SELECT v.user_id AS UserId,
                       d.user_setting_definition_id AS UserSettingDefinitionId,
                       d.setting_category AS SettingCategory,
                       d.setting_name AS SettingName,
                       d.value_kind AS ValueKind,
                       d.default_int_value AS DefaultIntValue,
                       d.default_string_value AS DefaultStringValue,
                       d.description AS Description,
                       d.sort_order AS SortOrder,
                       d.is_enabled AS IsEnabled,
                       CAST(NULL AS int) AS IntValue,
                       v.setting_value AS StringValue,
                       v.updated_at AS UpdatedAt
                FROM omp_portal.user_setting_string_values v
                INNER JOIN omp_portal.user_setting_definitions d
                    ON d.user_setting_definition_id = v.user_setting_definition_id
                   AND d.value_kind = v.value_kind
                WHERE v.user_id = @user_id
            ) AS rows
            ORDER BY SettingCategory, SortOrder, SettingName;
            """;

        var rows = new List<PortalUserSettingValueRow>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadValue(reader));
        }

        return rows;
    }

    public async Task<PortalUserSettingValueRow?> GetValueAsync(int userId, int userSettingDefinitionId, CancellationToken cancellationToken)
    {
        var rows = await GetValuesForUserAsync(userId, cancellationToken).ConfigureAwait(false);
        return rows.FirstOrDefault(row => row.UserSettingDefinitionId == userSettingDefinitionId);
    }

    public async Task<PortalUserSettingDefinitionRow?> GetDefinitionAsync(int userSettingDefinitionId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT user_setting_definition_id,
                   setting_category,
                   setting_name,
                   value_kind,
                   default_int_value,
                   default_string_value,
                   description,
                   sort_order,
                   is_enabled
            FROM omp_portal.user_setting_definitions
            WHERE user_setting_definition_id = @definition_id;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@definition_id", SqlDbType.Int).Value = userSettingDefinitionId;

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadDefinition(reader)
            : null;
    }

    public async Task<bool> SaveValueAsync(int userId, PortalUserSettingValueEditData editData, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var definition = await GetDefinitionAsync(connection, transaction, editData.UserSettingDefinitionId, cancellationToken).ConfigureAwait(false);
        if (definition is null || definition.ValueKind != editData.ValueKind)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (definition.ValueKind == IntValueKind)
        {
            if (editData.IntValue is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (definition.DefaultIntValue == editData.IntValue.Value)
            {
                await DeleteValueAsync(connection, transaction, userId, definition.UserSettingDefinitionId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await DeleteStringValueAsync(connection, transaction, userId, definition.UserSettingDefinitionId, cancellationToken).ConfigureAwait(false);
                await UpsertIntValueAsync(connection, transaction, userId, definition.UserSettingDefinitionId, editData.IntValue.Value, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (definition.ValueKind == StringValueKind)
        {
            var value = editData.StringValue ?? string.Empty;
            if (string.Equals(definition.DefaultStringValue, value, StringComparison.Ordinal))
            {
                await DeleteValueAsync(connection, transaction, userId, definition.UserSettingDefinitionId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await DeleteIntValueAsync(connection, transaction, userId, definition.UserSettingDefinitionId, cancellationToken).ConfigureAwait(false);
                await UpsertStringValueAsync(connection, transaction, userId, definition.UserSettingDefinitionId, value, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task DeleteValueAsync(int userId, int userSettingDefinitionId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await DeleteValueAsync(connection, transaction, userId, userSettingDefinitionId, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<PortalUserSettingDefinitionRow?> GetDefinitionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int userSettingDefinitionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT user_setting_definition_id,
                   setting_category,
                   setting_name,
                   value_kind,
                   default_int_value,
                   default_string_value,
                   description,
                   sort_order,
                   is_enabled
            FROM omp_portal.user_setting_definitions
            WHERE user_setting_definition_id = @definition_id;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@definition_id", SqlDbType.Int).Value = userSettingDefinitionId;

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadDefinition(reader)
            : null;
    }

    private static async Task UpsertIntValueAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int userId,
        int userSettingDefinitionId,
        int settingValue,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE omp_portal.user_setting_int_values
            SET setting_value = @setting_value,
                updated_at = SYSUTCDATETIME()
            WHERE user_id = @user_id
              AND user_setting_definition_id = @definition_id;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO omp_portal.user_setting_int_values(user_id, user_setting_definition_id, value_kind, setting_value)
                VALUES(@user_id, @definition_id, @value_kind, @setting_value);
            END;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@definition_id", SqlDbType.Int).Value = userSettingDefinitionId;
        command.Parameters.Add("@value_kind", SqlDbType.TinyInt).Value = IntValueKind;
        command.Parameters.Add("@setting_value", SqlDbType.Int).Value = settingValue;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertStringValueAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int userId,
        int userSettingDefinitionId,
        string settingValue,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE omp_portal.user_setting_string_values
            SET setting_value = @setting_value,
                updated_at = SYSUTCDATETIME()
            WHERE user_id = @user_id
              AND user_setting_definition_id = @definition_id;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO omp_portal.user_setting_string_values(user_id, user_setting_definition_id, value_kind, setting_value)
                VALUES(@user_id, @definition_id, @value_kind, @setting_value);
            END;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@definition_id", SqlDbType.Int).Value = userSettingDefinitionId;
        command.Parameters.Add("@value_kind", SqlDbType.TinyInt).Value = StringValueKind;
        command.Parameters.Add("@setting_value", SqlDbType.NVarChar, -1).Value = settingValue;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteValueAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int userId,
        int userSettingDefinitionId,
        CancellationToken cancellationToken)
    {
        await DeleteIntValueAsync(connection, transaction, userId, userSettingDefinitionId, cancellationToken).ConfigureAwait(false);
        await DeleteStringValueAsync(connection, transaction, userId, userSettingDefinitionId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteIntValueAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int userId,
        int userSettingDefinitionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM omp_portal.user_setting_int_values
            WHERE user_id = @user_id
              AND user_setting_definition_id = @definition_id;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@definition_id", SqlDbType.Int).Value = userSettingDefinitionId;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteStringValueAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int userId,
        int userSettingDefinitionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM omp_portal.user_setting_string_values
            WHERE user_id = @user_id
              AND user_setting_definition_id = @definition_id;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@definition_id", SqlDbType.Int).Value = userSettingDefinitionId;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static PortalUserSettingDefinitionRow ReadDefinition(SqlDataReader reader)
    {
        return new PortalUserSettingDefinitionRow(
            reader.GetInt32(reader.GetOrdinal("user_setting_definition_id")),
            reader.GetString(reader.GetOrdinal("setting_category")),
            reader.GetString(reader.GetOrdinal("setting_name")),
            reader.GetByte(reader.GetOrdinal("value_kind")),
            reader.IsDBNull(reader.GetOrdinal("default_int_value")) ? null : reader.GetInt32(reader.GetOrdinal("default_int_value")),
            reader.IsDBNull(reader.GetOrdinal("default_string_value")) ? null : reader.GetString(reader.GetOrdinal("default_string_value")),
            reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString(reader.GetOrdinal("description")),
            reader.GetInt32(reader.GetOrdinal("sort_order")),
            reader.GetBoolean(reader.GetOrdinal("is_enabled")));
    }

    private static PortalUserSettingValueRow ReadValue(SqlDataReader reader)
    {
        return new PortalUserSettingValueRow(
            reader.GetInt32(reader.GetOrdinal("UserId")),
            reader.GetInt32(reader.GetOrdinal("UserSettingDefinitionId")),
            reader.GetString(reader.GetOrdinal("SettingCategory")),
            reader.GetString(reader.GetOrdinal("SettingName")),
            reader.GetByte(reader.GetOrdinal("ValueKind")),
            reader.IsDBNull(reader.GetOrdinal("DefaultIntValue")) ? null : reader.GetInt32(reader.GetOrdinal("DefaultIntValue")),
            reader.IsDBNull(reader.GetOrdinal("DefaultStringValue")) ? null : reader.GetString(reader.GetOrdinal("DefaultStringValue")),
            reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
            reader.GetInt32(reader.GetOrdinal("SortOrder")),
            reader.GetBoolean(reader.GetOrdinal("IsEnabled")),
            reader.IsDBNull(reader.GetOrdinal("IntValue")) ? null : reader.GetInt32(reader.GetOrdinal("IntValue")),
            reader.IsDBNull(reader.GetOrdinal("StringValue")) ? null : reader.GetString(reader.GetOrdinal("StringValue")),
            reader.GetDateTime(reader.GetOrdinal("UpdatedAt")));
    }
}

public sealed record PortalUserSettingDefinitionRow(
    int UserSettingDefinitionId,
    string SettingCategory,
    string SettingName,
    byte ValueKind,
    int? DefaultIntValue,
    string? DefaultStringValue,
    string? Description,
    int SortOrder,
    bool IsEnabled)
{
    public string Key => $"{SettingCategory}/{SettingName}";
}

public sealed record PortalUserSettingValueRow(
    int UserId,
    int UserSettingDefinitionId,
    string SettingCategory,
    string SettingName,
    byte ValueKind,
    int? DefaultIntValue,
    string? DefaultStringValue,
    string? Description,
    int SortOrder,
    bool IsEnabled,
    int? IntValue,
    string? StringValue,
    DateTime UpdatedAt)
{
    public string Key => $"{SettingCategory}/{SettingName}";
}

public sealed record PortalUserSettingValueEditData(
    int UserSettingDefinitionId,
    byte ValueKind,
    int? IntValue,
    string? StringValue);

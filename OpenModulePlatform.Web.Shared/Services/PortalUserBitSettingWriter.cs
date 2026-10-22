using Microsoft.Data.SqlClient;
using System.Data;

namespace OpenModulePlatform.Web.Shared.Services;

/// <summary>
/// Writes a bit-backed Portal user setting (omp_portal.user_setting_bit_values) from any OMP
/// web app. The shared top bar already reads these rows in every app; this is the matching
/// write for the controls the top bar carries itself, such as the mute toggle in the
/// notification tray. A value equal to the definition's default removes the user's row, the
/// same rule the Portal's own settings page applies, so the two never disagree.
/// </summary>
public sealed class PortalUserBitSettingWriter
{
    private const byte BitValueKind = 3;

    private readonly SqlConnectionFactory _db;

    public PortalUserBitSettingWriter(SqlConnectionFactory db)
    {
        _db = db;
    }

    /// <summary>Reads the user's value, or the definition's default, or <paramref name="defaultValue"/>
    /// when the installation lacks the definition.</summary>
    public async Task<bool> GetAsync(int userId, string category, string settingName, bool defaultValue, CancellationToken ct)
    {
        const string sql = @"
SELECT CAST(COALESCE(v.setting_value, d.default_bit_value, @default_value) AS bit)
FROM omp_portal.user_setting_definitions d
LEFT JOIN omp_portal.user_setting_bit_values v
    ON v.user_setting_definition_id = d.user_setting_definition_id
   AND v.user_id = @user_id
WHERE d.setting_category = @setting_category
  AND d.setting_name = @setting_name
  AND d.value_kind = @value_kind
  AND d.is_enabled = 1;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        cmd.Parameters.Add("@setting_category", SqlDbType.NVarChar, 100).Value = category;
        cmd.Parameters.Add("@setting_name", SqlDbType.NVarChar, 200).Value = settingName;
        cmd.Parameters.Add("@value_kind", SqlDbType.TinyInt).Value = BitValueKind;
        cmd.Parameters.Add("@default_value", SqlDbType.Bit).Value = defaultValue;
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is bool value ? value : defaultValue;
    }

    /// <summary>Stores <paramref name="value"/> for the user; returns false when the definition
    /// does not exist or is disabled, so a caller can answer with a clear failure instead of a
    /// silent no-op.</summary>
    public async Task<bool> SetAsync(int userId, string category, string settingName, bool value, CancellationToken ct)
    {
        const string sql = @"
DECLARE @definition_id int;
DECLARE @default_value bit;
SELECT @definition_id = user_setting_definition_id,
       @default_value = ISNULL(default_bit_value, 0)
FROM omp_portal.user_setting_definitions
WHERE setting_category = @setting_category
  AND setting_name = @setting_name
  AND value_kind = @value_kind
  AND is_enabled = 1;

IF @definition_id IS NULL
BEGIN
    SELECT CAST(0 AS bit);
    RETURN;
END

IF @setting_value = @default_value
BEGIN
    DELETE FROM omp_portal.user_setting_bit_values
    WHERE user_id = @user_id
      AND user_setting_definition_id = @definition_id;
END
ELSE
BEGIN
    UPDATE omp_portal.user_setting_bit_values
    SET setting_value = @setting_value,
        updated_at = SYSUTCDATETIME()
    WHERE user_id = @user_id
      AND user_setting_definition_id = @definition_id;

    IF @@ROWCOUNT = 0
    BEGIN
        INSERT INTO omp_portal.user_setting_bit_values(user_id, user_setting_definition_id, value_kind, setting_value)
        VALUES(@user_id, @definition_id, @value_kind, @setting_value);
    END
END

SELECT CAST(1 AS bit);";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        cmd.Parameters.Add("@setting_category", SqlDbType.NVarChar, 100).Value = category;
        cmd.Parameters.Add("@setting_name", SqlDbType.NVarChar, 200).Value = settingName;
        cmd.Parameters.Add("@value_kind", SqlDbType.TinyInt).Value = BitValueKind;
        cmd.Parameters.Add("@setting_value", SqlDbType.Bit).Value = value;
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is bool found && found;
    }
}

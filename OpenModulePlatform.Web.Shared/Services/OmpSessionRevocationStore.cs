using Microsoft.Data.SqlClient;
using OpenModulePlatform.Web.Shared.Security;

namespace OpenModulePlatform.Web.Shared.Services;

/// <summary>
/// The session-relevant state of an OMP account (R7-F10): its status and the
/// current security stamp that session cookies are compared against.
/// </summary>
public readonly record struct OmpSessionAccountState(int AccountStatus, Guid SecurityStamp);

/// <summary>
/// Reads the account state and revocation settings the session validation hook
/// needs. Kept as a narrow interface so the validator can be tested without a
/// database.
/// </summary>
public interface IOmpSessionRevocationStore
{
    /// <summary>Returns the account state, or null when the user no longer exists.</summary>
    Task<OmpSessionAccountState?> GetAccountStateAsync(int userId, CancellationToken ct);

    /// <summary>Returns the effective revocation settings; never throws on unreadable values.</summary>
    Task<OmpSessionRevocationSettings> GetSettingsAsync(CancellationToken ct);
}

/// <summary>
/// SQL-backed <see cref="IOmpSessionRevocationStore"/> over omp.users and the
/// omp configuration table.
/// </summary>
public sealed class OmpSqlSessionRevocationStore : IOmpSessionRevocationStore
{
    private const string AccountStateSql = @"
SELECT account_status,
       security_stamp
FROM omp.users
WHERE user_id = @user_id;";

    private readonly SqlConnectionFactory _db;
    private readonly OmpConfigurationService _configuration;

    public OmpSqlSessionRevocationStore(
        SqlConnectionFactory db,
        OmpConfigurationService configuration)
    {
        _db = db;
        _configuration = configuration;
    }

    public async Task<OmpSessionAccountState?> GetAccountStateAsync(int userId, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(AccountStateSql, conn);
        cmd.Parameters.AddWithValue("@user_id", userId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new OmpSessionAccountState(reader.GetInt32(0), reader.GetGuid(1));
    }

    public async Task<OmpSessionRevocationSettings> GetSettingsAsync(CancellationToken ct)
    {
        var failureMode = await _configuration.ReadGlobalStringAsync(
            OmpAuthDefaults.ConfigurationCategory,
            OmpAuthDefaults.SessionRevocationFailureModeSetting,
            ct);
        var cacheSeconds = await _configuration.ReadGlobalStringAsync(
            OmpAuthDefaults.ConfigurationCategory,
            OmpAuthDefaults.SessionRevocationCacheSecondsSetting,
            ct);

        return OmpSessionRevocationSettings.Parse(failureMode, cacheSeconds);
    }
}

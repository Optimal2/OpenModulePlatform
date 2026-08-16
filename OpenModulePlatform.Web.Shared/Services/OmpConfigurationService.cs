using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Threading;

namespace OpenModulePlatform.Web.Shared.Services;

/// <summary>
/// The outcome of a configuration read: the value, and whether the read failed.
/// </summary>
/// <param name="Value">The configured value, or null when unset or unreadable.</param>
/// <param name="Failed">
/// True when the query itself failed. A caller making a security decision must not treat
/// this as "unset" (R4-E1).
/// </param>
public readonly record struct OmpConfigurationRead(string? Value, bool Failed);

public sealed class OmpConfigurationService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);
    private const string FalseSqlCondition = "0 = 1";

    // A monotonic generation token folded into every cache key. Removing only the
    // exact global key left every effective-scoped copy of a setting (which folds
    // in the global fallback) serving a stale value for the full cache lifetime,
    // so an updated config was not picked up. Bumping the generation makes the
    // global entry and all effective entries unreachable at once (R5-E3).
    /// <summary>
    /// The process-wide cache generation shared by every instance of the service.
    /// </summary>
    /// <remarks>
    /// This was a private static field that instance methods incremented and read directly,
    /// which CodeQL reports as a static field written by an instance method
    /// (cs/static-field-written-by-instance). The report is about clarity, not correctness:
    /// the counter genuinely has to be process-wide, because the IMemoryCache it invalidates
    /// is shared and a per-instance counter would leave other instances serving stale values.
    ///
    /// Naming it makes that intent visible at each call site instead of hiding shared mutable
    /// state behind what looks like an ordinary field.
    /// </remarks>
    private static class CacheGeneration
    {
        private static long _value;

        public static long Current => Interlocked.Read(ref _value);

        public static void Invalidate() => Interlocked.Increment(ref _value);
    }

    private readonly SqlConnectionFactory _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<OmpConfigurationService> _log;

    public OmpConfigurationService(
        SqlConnectionFactory db,
        IMemoryCache cache,
        ILogger<OmpConfigurationService> log)
    {
        _db = db;
        _cache = cache;
        _log = log;
    }

    public async Task<string?> GetGlobalStringAsync(
        string category,
        string setting,
        CancellationToken ct)
        => (await ReadGlobalStringAsync(category, setting, ct)).Value;

    /// <summary>
    /// Reads a global setting and reports whether the read itself failed, so a caller can
    /// tell "not configured" apart from "could not be read".
    /// </summary>
    /// <remarks>
    /// R4-E1. <see cref="GetGlobalStringAsync"/> returns null for both, and for most
    /// callers that is right: branding falling back to a default on a transient database
    /// blip is better than an error page. For an access decision it is not. The
    /// AuthenticatedUsers domain allowlist treats an absent value as "no restriction
    /// configured, allow every domain" -- which is the correct default, and exactly the
    /// wrong reading of a failed query. A read error would silently widen access for
    /// every request until the database recovered.
    ///
    /// Callers that make security decisions use this overload and fail closed. Nothing is
    /// cached on failure, so the next request retries rather than repeating the verdict
    /// for the full cache lifetime.
    /// </remarks>
    public async Task<OmpConfigurationRead> ReadGlobalStringAsync(
        string category,
        string setting,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(setting))
        {
            return new OmpConfigurationRead(null, Failed: false);
        }

        var cacheKey = CreateGlobalCacheKey(category, setting);

        if (_cache.TryGetValue<string?>(cacheKey, out var cachedValue))
        {
            return new OmpConfigurationRead(cachedValue, Failed: false);
        }

        string? value;
        try
        {
            value = await QueryGlobalStringAsync(category.Trim(), setting.Trim(), ct);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // Do NOT cache on failure: a read error used to cache null for the
            // full lifetime, and callers that fail open on a missing value (e.g.
            // the AuthenticatedUsers domain allowlist) then dropped their
            // restriction for every request in that window (R4-E1). Returning
            // uncached lets the next request retry.
            LogConfigReadFailure(ex, category, setting, effective: false);
            return new OmpConfigurationRead(null, Failed: true);
        }

        _cache.Set(cacheKey, value, CacheLifetime);
        return new OmpConfigurationRead(value, Failed: false);
    }

    public async Task<string?> GetEffectiveStringAsync(
        string category,
        string setting,
        int? userId,
        int? activeRoleId,
        IReadOnlyCollection<string> effectivePermissions,
        CancellationToken ct)
        => (await ReadEffectiveStringAsync(category, setting, userId, activeRoleId, effectivePermissions, ct)).Value;

    /// <summary>
    /// Reads a user-, role- and permission-scoped setting and reports whether the read
    /// itself failed, so a caller can tell "not configured" apart from "could not be read".
    /// </summary>
    /// <remarks>
    /// R12-E5. R4-E1 gave the global read this contract and left the effective read with the
    /// old one, where a failed query and an unset setting are both null. The distinction
    /// matters more here, not less: effective settings are the per-user and per-role ones, so
    /// this is where a read failure is most likely to be read as "this principal has no
    /// override" and quietly hand out whatever the fall-through default is. Nothing is cached
    /// on failure, so the next request retries instead of repeating the verdict for the full
    /// cache lifetime.
    /// </remarks>
    public async Task<OmpConfigurationRead> ReadEffectiveStringAsync(
        string category,
        string setting,
        int? userId,
        int? activeRoleId,
        IReadOnlyCollection<string> effectivePermissions,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(setting))
        {
            return new OmpConfigurationRead(null, Failed: false);
        }

        var permissionNames = effectivePermissions
            .Where(permission => !string.IsNullOrWhiteSpace(permission))
            .Select(permission => permission.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!userId.HasValue && !activeRoleId.HasValue && permissionNames.Length == 0)
        {
            // No principal to scope by: this is the global read, and it already carries the
            // failure flag. Returning its result whole is what keeps the two contracts from
            // drifting apart a second time.
            return await ReadGlobalStringAsync(category, setting, ct);
        }

        // Cache the resolved value like the global variant: branding alone asks
        // for several effective settings on every request, and they change
        // rarely (R3-E9). The key includes the user, role and permission set so
        // different principals never share a value.
        var permissionKey = string.Join('|', permissionNames);
        var cacheKey = string.Create(CultureInfo.InvariantCulture,
            $"omp-cfg-eff::{CacheGeneration.Current}::{category.Trim()}::{setting.Trim()}::{userId?.ToString(CultureInfo.InvariantCulture) ?? "-"}::{activeRoleId?.ToString(CultureInfo.InvariantCulture) ?? "-"}::{permissionKey}");
        if (_cache.TryGetValue<string?>(cacheKey, out var cachedValue))
        {
            return new OmpConfigurationRead(cachedValue, Failed: false);
        }

        try
        {
            var value = await QueryEffectiveStringAsync(
                category.Trim(),
                setting.Trim(),
                userId,
                activeRoleId,
                permissionNames,
                ct);
            _cache.Set(cacheKey, value, CacheLifetime);
            return new OmpConfigurationRead(value, Failed: false);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // Do not cache a read failure (R4-E1).
            LogConfigReadFailure(ex, category, setting, effective: true);
            return new OmpConfigurationRead(null, Failed: true);
        }
    }

    public void ClearGlobalString(string category, string setting)
    {
        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(setting))
        {
            return;
        }

        // Bump the generation so both the global entry and every effective-scoped
        // copy of the setting are invalidated, not just the exact global key (R5-E3).
        InvalidateAll();
    }

    /// <summary>
    /// Invalidates every cached configuration value so subsequent reads reload from the database.
    /// Call this after any configuration write so updated values are picked up immediately.
    /// </summary>
    public void InvalidateAll() => CacheGeneration.Invalidate();

    private static string CreateGlobalCacheKey(string category, string setting)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"omp-config:global:{CacheGeneration.Current}:{category.Trim().ToLowerInvariant()}:{setting.Trim().ToLowerInvariant()}");

    private void LogConfigReadFailure(Exception ex, string category, string setting, bool effective)
    {
        var scope = effective ? "effective OMP config setting" : "OMP config setting";
        _log.LogWarning(
            ex,
            "Failed to read {ConfigSettingScope} {ConfigCategory}/{ConfigSetting}; using runtime defaults.",
            scope,
            category,
            setting);
    }

    private async Task<string?> QueryGlobalStringAsync(
        string category,
        string setting,
        CancellationToken ct)
    {
        const string sql = """
SELECT TOP (1) cs.ConfigValue
FROM omp.config_settings cs
INNER JOIN omp.config_setting_definitions def
    ON def.ConfigSettingId = cs.ConfigSettingId
WHERE def.ConfigCategory = @category
  AND def.ConfigSetting = @setting
  AND cs.ConfigUsr IS NULL
  AND cs.ConfigPermission IS NULL
  AND cs.ConfigRole IS NULL
ORDER BY cs.ConfigScopeRank DESC,
         cs.ConfigPriority DESC,
         cs.ConfigId DESC;
""";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@category", category);
        cmd.Parameters.AddWithValue("@setting", setting);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    private async Task<string?> QueryEffectiveStringAsync(
        string category,
        string setting,
        int? userId,
        int? activeRoleId,
        IReadOnlyList<string> effectivePermissions,
        CancellationToken ct)
    {
        var permissionClause = FalseSqlCondition;
        if (effectivePermissions.Count > 0)
        {
            var permissionParameters = string.Join(
                ", ",
                Enumerable.Range(0, effectivePermissions.Count).Select(i => $"@permission{i}"));

            permissionClause = $"""
EXISTS
(
    SELECT 1
    FROM omp.Permissions p
    WHERE p.PermissionId = cs.ConfigPermission
      AND p.Name IN ({permissionParameters})
)
""";
        }

        // permissionClause is assembled only from FalseSqlCondition or generated
        // parameter names. Permission values are always passed as SQL parameters.
        var sql = $"""
SELECT TOP (1) cs.ConfigValue
FROM omp.config_settings cs
INNER JOIN omp.config_setting_definitions def
    ON def.ConfigSettingId = cs.ConfigSettingId
WHERE def.ConfigCategory = @category
  AND def.ConfigSetting = @setting
  AND
  (
      (cs.ConfigUsr IS NULL AND cs.ConfigPermission IS NULL AND cs.ConfigRole IS NULL)
      OR (@userId IS NOT NULL AND cs.ConfigUsr = @userId)
      OR (cs.ConfigUsr IS NULL AND cs.ConfigPermission IS NOT NULL AND {permissionClause})
      OR (cs.ConfigUsr IS NULL AND cs.ConfigPermission IS NULL AND @activeRoleId IS NOT NULL AND cs.ConfigRole = @activeRoleId)
  )
ORDER BY cs.ConfigScopeRank DESC,
         cs.ConfigPriority DESC,
         cs.ConfigId DESC;
""";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@category", category);
        cmd.Parameters.AddWithValue("@setting", setting);
        cmd.Parameters.AddWithValue("@userId", userId.HasValue ? userId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@activeRoleId", activeRoleId.HasValue ? activeRoleId.Value : DBNull.Value);

        for (var i = 0; i < effectivePermissions.Count; i++)
        {
            cmd.Parameters.AddWithValue($"@permission{i}", effectivePermissions[i]);
        }

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
    }
}

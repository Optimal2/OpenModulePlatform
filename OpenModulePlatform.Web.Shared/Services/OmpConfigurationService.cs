using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Threading;

namespace OpenModulePlatform.Web.Shared.Services;

public sealed class OmpConfigurationService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);
    private const string FalseSqlCondition = "0 = 1";

    // A monotonic generation token folded into every cache key. Removing only the
    // exact global key left every effective-scoped copy of a setting (which folds
    // in the global fallback) serving a stale value for the full cache lifetime,
    // so an updated config was not picked up. Bumping the generation makes the
    // global entry and all effective entries unreachable at once (R5-E3).
    private static long _generation;

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
    {
        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(setting))
        {
            return null;
        }

        var cacheKey = CreateGlobalCacheKey(category, setting);

        if (_cache.TryGetValue<string?>(cacheKey, out var cachedValue))
        {
            return cachedValue;
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
            return null;
        }

        _cache.Set(cacheKey, value, CacheLifetime);
        return value;
    }

    public async Task<string?> GetEffectiveStringAsync(
        string category,
        string setting,
        int? userId,
        int? activeRoleId,
        IReadOnlyCollection<string> effectivePermissions,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(setting))
        {
            return null;
        }

        var permissionNames = effectivePermissions
            .Where(permission => !string.IsNullOrWhiteSpace(permission))
            .Select(permission => permission.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!userId.HasValue && !activeRoleId.HasValue && permissionNames.Length == 0)
        {
            return await GetGlobalStringAsync(category, setting, ct);
        }

        // Cache the resolved value like the global variant: branding alone asks
        // for several effective settings on every request, and they change
        // rarely (R3-E9). The key includes the user, role and permission set so
        // different principals never share a value.
        var permissionKey = string.Join('|', permissionNames);
        var cacheKey = string.Create(CultureInfo.InvariantCulture,
            $"omp-cfg-eff::{Interlocked.Read(ref _generation)}::{category.Trim()}::{setting.Trim()}::{userId?.ToString(CultureInfo.InvariantCulture) ?? "-"}::{activeRoleId?.ToString(CultureInfo.InvariantCulture) ?? "-"}::{permissionKey}");
        if (_cache.TryGetValue<string?>(cacheKey, out var cachedValue))
        {
            return cachedValue;
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
            return value;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // Do not cache a read failure (R4-E1).
            LogConfigReadFailure(ex, category, setting, effective: true);
        }

        return null;
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
    public void InvalidateAll() => Interlocked.Increment(ref _generation);

    private static string CreateGlobalCacheKey(string category, string setting)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"omp-config:global:{Interlocked.Read(ref _generation)}:{category.Trim().ToLowerInvariant()}:{setting.Trim().ToLowerInvariant()}");

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

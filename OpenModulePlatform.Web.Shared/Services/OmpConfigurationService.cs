using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace OpenModulePlatform.Web.Shared.Services;

public sealed class OmpConfigurationService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);

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

        string? value = null;
        try
        {
            value = await QueryGlobalStringAsync(category.Trim(), setting.Trim(), ct);
        }
        catch (SqlException ex)
        {
            _log.LogWarning(
                ex,
                "Failed to read OMP config setting {ConfigCategory}/{ConfigSetting}; using runtime defaults.",
                category,
                setting);
        }
        catch (InvalidOperationException ex)
        {
            _log.LogWarning(
                ex,
                "Failed to read OMP config setting {ConfigCategory}/{ConfigSetting}; using runtime defaults.",
                category,
                setting);
        }

        _cache.Set(cacheKey, value, CacheLifetime);
        return value;
    }

    public void ClearGlobalString(string category, string setting)
    {
        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(setting))
        {
            return;
        }

        _cache.Remove(CreateGlobalCacheKey(category, setting));
    }

    private static string CreateGlobalCacheKey(string category, string setting)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"omp-config:global:{category.Trim().ToLowerInvariant()}:{setting.Trim().ToLowerInvariant()}");

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
}

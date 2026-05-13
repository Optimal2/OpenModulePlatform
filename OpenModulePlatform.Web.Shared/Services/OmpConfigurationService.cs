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

        var cacheKey = string.Create(
            CultureInfo.InvariantCulture,
            $"omp-config:global:{category.Trim().ToLowerInvariant()}:{setting.Trim().ToLowerInvariant()}");

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

    private async Task<string?> QueryGlobalStringAsync(
        string category,
        string setting,
        CancellationToken ct)
    {
        const string sql = """
SELECT TOP (1) ConfigValue
FROM omp.config_settings
WHERE ConfigCategory = @category
  AND ConfigSetting = @setting
  AND ConfigUsr IS NULL
  AND ConfigPermission IS NULL
  AND ConfigRole IS NULL
ORDER BY ConfigScopeRank DESC,
         ConfigPriority DESC,
         ConfigId DESC;
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

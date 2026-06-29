using Microsoft.Data.SqlClient;
using OpenModulePlatform.Portal.Models;

namespace OpenModulePlatform.Portal.Services;

public sealed partial class OmpAdminRepository
{
    public async Task<IReadOnlyList<HostResourceLatestRow>> GetHostResourceLatestAsync(
        Guid hostId,
        CancellationToken ct)
    {
        const string sql = @"
IF OBJECT_ID(N'omp.HostResourceLatest', N'U') IS NULL
BEGIN
    SELECT TOP (0)
        CAST(NULL AS uniqueidentifier) AS HostId,
        CAST(NULL AS nvarchar(128)) AS HostKey,
        CAST(NULL AS nvarchar(200)) AS HostDisplayName,
        CAST(NULL AS datetime2(3)) AS HostLastSeenUtc,
        CAST(NULL AS nvarchar(100)) AS SampleKey,
        CAST(NULL AS float) AS SampleValue,
        CAST(NULL AS int) AS SampleCount,
        CAST(NULL AS datetime2(3)) AS FirstSampledUtc,
        CAST(NULL AS datetime2(3)) AS LastSampledUtc,
        CAST(NULL AS float) AS MinValue,
        CAST(NULL AS float) AS MaxValue;
    RETURN;
END;

SELECT host.HostId,
       host.HostKey,
       host.DisplayName AS HostDisplayName,
       host.LastSeenUtc AS HostLastSeenUtc,
       latest.SampleKey,
       latest.SampleValue,
       latest.SampleCount,
       latest.FirstSampledUtc,
       latest.LastSampledUtc,
       latest.MinValue,
       latest.MaxValue
FROM omp.Hosts host
LEFT JOIN omp.HostResourceLatest latest
    ON latest.HostId = host.HostId
WHERE host.HostId = @HostId
ORDER BY latest.SampleKey;";

        var rows = new List<HostResourceLatestRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@HostId", hostId);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new HostResourceLatestRow
            {
                HostId = rdr.GetGuid(0),
                HostKey = rdr.GetString(1),
                HostDisplayName = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                HostLastSeenUtc = rdr.IsDBNull(3) ? null : rdr.GetDateTime(3),
                SampleKey = rdr.IsDBNull(4) ? string.Empty : rdr.GetString(4),
                SampleValue = rdr.IsDBNull(5) ? 0 : rdr.GetDouble(5),
                SampleCount = rdr.IsDBNull(6) ? 0 : rdr.GetInt32(6),
                FirstSampledUtc = rdr.IsDBNull(7) ? DateTime.MinValue : rdr.GetDateTime(7),
                LastSampledUtc = rdr.IsDBNull(8) ? DateTime.MinValue : rdr.GetDateTime(8),
                MinValue = rdr.IsDBNull(9) ? null : rdr.GetDouble(9),
                MaxValue = rdr.IsDBNull(10) ? null : rdr.GetDouble(10)
            });
        }

        return rows;
    }

    public async Task<IReadOnlyList<HostResourceLatestRow>> GetHostResourceLatestForAllHostsAsync(
        CancellationToken ct)
    {
        const string sql = @"
IF OBJECT_ID(N'omp.HostResourceLatest', N'U') IS NULL
BEGIN
    SELECT TOP (0)
        CAST(NULL AS uniqueidentifier) AS HostId,
        CAST(NULL AS nvarchar(128)) AS HostKey,
        CAST(NULL AS nvarchar(200)) AS HostDisplayName,
        CAST(NULL AS datetime2(3)) AS HostLastSeenUtc,
        CAST(NULL AS nvarchar(100)) AS SampleKey,
        CAST(NULL AS float) AS SampleValue,
        CAST(NULL AS int) AS SampleCount,
        CAST(NULL AS datetime2(3)) AS FirstSampledUtc,
        CAST(NULL AS datetime2(3)) AS LastSampledUtc,
        CAST(NULL AS float) AS MinValue,
        CAST(NULL AS float) AS MaxValue;
    RETURN;
END;

SELECT host.HostId,
       host.HostKey,
       host.DisplayName AS HostDisplayName,
       host.LastSeenUtc AS HostLastSeenUtc,
       latest.SampleKey,
       latest.SampleValue,
       latest.SampleCount,
       latest.FirstSampledUtc,
       latest.LastSampledUtc,
       latest.MinValue,
       latest.MaxValue
FROM omp.Hosts host
LEFT JOIN omp.HostResourceLatest latest
    ON latest.HostId = host.HostId
WHERE host.IsEnabled = 1
ORDER BY host.HostKey, latest.SampleKey;";

        var rows = new List<HostResourceLatestRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new HostResourceLatestRow
            {
                HostId = rdr.GetGuid(0),
                HostKey = rdr.GetString(1),
                HostDisplayName = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                HostLastSeenUtc = rdr.IsDBNull(3) ? null : rdr.GetDateTime(3),
                SampleKey = rdr.IsDBNull(4) ? string.Empty : rdr.GetString(4),
                SampleValue = rdr.IsDBNull(5) ? 0 : rdr.GetDouble(5),
                SampleCount = rdr.IsDBNull(6) ? 0 : rdr.GetInt32(6),
                FirstSampledUtc = rdr.IsDBNull(7) ? DateTime.MinValue : rdr.GetDateTime(7),
                LastSampledUtc = rdr.IsDBNull(8) ? DateTime.MinValue : rdr.GetDateTime(8),
                MinValue = rdr.IsDBNull(9) ? null : rdr.GetDouble(9),
                MaxValue = rdr.IsDBNull(10) ? null : rdr.GetDouble(10)
            });
        }

        return rows;
    }

    public async Task<IReadOnlyList<HostResourceHistoryRow>> GetHostResourceHistoryAsync(
        Guid hostId,
        string sampleKey,
        DateTime sinceUtc,
        CancellationToken ct)
    {
        const string sql = @"
IF OBJECT_ID(N'omp.HostResourceSamples', N'U') IS NULL
BEGIN
    SELECT TOP (0)
        CAST(NULL AS uniqueidentifier) AS HostId,
        CAST(NULL AS nvarchar(100)) AS SampleKey,
        CAST(NULL AS datetime2(3)) AS SampleBucketUtc,
        CAST(NULL AS float) AS SampleValue,
        CAST(NULL AS int) AS SampleCount,
        CAST(NULL AS datetime2(3)) AS FirstSampledUtc,
        CAST(NULL AS datetime2(3)) AS LastSampledUtc,
        CAST(NULL AS float) AS MinValue,
        CAST(NULL AS float) AS MaxValue;
    RETURN;
END;

SELECT HostId,
       SampleKey,
       SampleBucketUtc,
       SampleValue,
       SampleCount,
       FirstSampledUtc,
       LastSampledUtc,
       MinValue,
       MaxValue
FROM omp.HostResourceSamples
WHERE HostId = @HostId
  AND SampleKey = @SampleKey
  AND SampleBucketUtc >= @SinceUtc
ORDER BY SampleBucketUtc DESC;";

        var rows = new List<HostResourceHistoryRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@HostId", hostId);
        cmd.Parameters.AddWithValue("@SampleKey", sampleKey);
        cmd.Parameters.AddWithValue("@SinceUtc", sinceUtc);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new HostResourceHistoryRow
            {
                HostId = rdr.GetGuid(0),
                SampleKey = rdr.GetString(1),
                SampleBucketUtc = rdr.GetDateTime(2),
                SampleValue = rdr.GetDouble(3),
                SampleCount = rdr.GetInt32(4),
                FirstSampledUtc = rdr.GetDateTime(5),
                LastSampledUtc = rdr.GetDateTime(6),
                MinValue = rdr.IsDBNull(7) ? null : rdr.GetDouble(7),
                MaxValue = rdr.IsDBNull(8) ? null : rdr.GetDouble(8)
            });
        }

        return rows;
    }
}

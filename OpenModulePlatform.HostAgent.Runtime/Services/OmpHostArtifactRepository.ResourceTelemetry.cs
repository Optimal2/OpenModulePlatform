using System.Data;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed partial class OmpHostArtifactRepository
{
    private const int HostResourceSampleKeyMaxLength = 100;

    /// <summary>
    /// Returns the running worker processes on this host, as (worker instance key, process id).
    /// </summary>
    /// <remarks>
    /// R8-P5-20. The resource collector sweeps IIS app pools and Windows services.
    /// OpenModulePlatform.WorkerProcessHost is neither -- WorkerManager starts it -- so the whole
    /// worker fleet was missing from the host summary. On this installation that was eight
    /// processes at roughly 66 MB each: the page showed 1 070 MB while OMP actually held about
    /// 1 600 MB, and nothing in the view suggested a third of it was unaccounted for.
    ///
    /// The process ids do not have to be discovered. WorkerManager already publishes them per
    /// worker instance in omp.WorkerInstanceRuntimeStates, which is also what gives each sample a
    /// stable name to be keyed by -- reading the ids back is both cheaper and better attributed
    /// than matching on process name, which could only ever produce one anonymous lump.
    ///
    /// ObservedState 1..3 are the running states; anything else has no live process to sample.
    /// </remarks>
    public async Task<IReadOnlyList<(string WorkerInstanceKey, int ProcessId)>> GetLocalWorkerProcessTargetsAsync(
        CancellationToken ct)
    {
        const string sql = @"
SET NOCOUNT ON;

SELECT COALESCE(NULLIF(LTRIM(RTRIM(rs.WorkerInstanceKey)), N''), CONVERT(nvarchar(50), wi.WorkerInstanceId)) AS WorkerInstanceKey,
       rs.ProcessId
FROM omp.WorkerInstanceRuntimeStates rs
INNER JOIN omp.WorkerInstances wi ON wi.WorkerInstanceId = rs.WorkerInstanceId
LEFT JOIN omp.AppInstances ai ON ai.AppInstanceId = wi.AppInstanceId
-- No host filter. Both omp.WorkerInstances.HostId and omp.AppInstances.HostId are null for every
-- worker row here: a host-agnostic app instance is placed by template and host role, not by a
-- stored host id, so there is nothing to filter on. Two successive attempts to attribute by host
-- in SQL therefore returned nothing at all. The caller keeps only the rows whose process id
-- belongs to a live worker host process on this machine, which is both the accurate test and the
-- one the collector can actually make.
WHERE rs.ProcessId IS NOT NULL
  AND rs.ProcessId > 0
  AND rs.ObservedState IN (1, 2, 3);";

        var targets = new List<(string WorkerInstanceKey, int ProcessId)>();

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var key = reader.GetString(0);
            var processId = reader.GetInt32(1);
            if (!string.IsNullOrWhiteSpace(key))
            {
                targets.Add((key, processId));
            }
        }

        return targets;
    }

    public async Task UpsertHostResourceSamplesAsync(
        Guid hostId,
        IReadOnlyCollection<HostResourceSample> samples,
        int bucketMinutes,
        CancellationToken ct)
    {
        if (samples.Count == 0)
        {
            return;
        }

        const string mergeLatestSql = @"
MERGE omp.HostResourceLatest WITH (HOLDLOCK) AS target
USING (SELECT @hostId AS HostId, @sampleKey AS SampleKey) AS source
ON target.HostId = source.HostId AND target.SampleKey = source.SampleKey
WHEN MATCHED THEN
    UPDATE SET
        SampleValue = @sampleValue,
        SampleCount = target.SampleCount + 1,
        FirstSampledUtc = CASE WHEN @sampledUtc < target.FirstSampledUtc THEN @sampledUtc ELSE target.FirstSampledUtc END,
        LastSampledUtc = CASE WHEN @sampledUtc > target.LastSampledUtc THEN @sampledUtc ELSE target.LastSampledUtc END,
        MinValue = CASE WHEN target.MinValue IS NULL OR @minValue < target.MinValue THEN @minValue ELSE target.MinValue END,
        MaxValue = CASE WHEN target.MaxValue IS NULL OR @maxValue > target.MaxValue THEN @maxValue ELSE target.MaxValue END
WHEN NOT MATCHED THEN
    INSERT (HostId, SampleKey, SampleValue, SampleCount, FirstSampledUtc, LastSampledUtc, MinValue, MaxValue)
    VALUES (@hostId, @sampleKey, @sampleValue, 1, @sampledUtc, @sampledUtc, @minValue, @maxValue);";

        const string mergeSamplesSql = @"
DECLARE @bucketUtc datetime2(3) = DATEADD(minute, DATEDIFF(minute, 0, @sampledUtc) / @bucketMinutes * @bucketMinutes, 0);

MERGE omp.HostResourceSamples WITH (HOLDLOCK) AS target
USING (SELECT @hostId AS HostId, @bucketUtc AS SampleBucketUtc, @sampleKey AS SampleKey) AS source
ON target.HostId = source.HostId AND target.SampleBucketUtc = source.SampleBucketUtc AND target.SampleKey = source.SampleKey
WHEN MATCHED THEN
    UPDATE SET
        SampleValue = ((target.SampleValue * target.SampleCount) + (@sampleValue * 1.0)) / (target.SampleCount + 1),
        SampleCount = target.SampleCount + 1,
        FirstSampledUtc = CASE WHEN @sampledUtc < target.FirstSampledUtc THEN @sampledUtc ELSE target.FirstSampledUtc END,
        LastSampledUtc = CASE WHEN @sampledUtc > target.LastSampledUtc THEN @sampledUtc ELSE target.LastSampledUtc END,
        MinValue = CASE WHEN target.MinValue IS NULL OR @minValue < target.MinValue THEN @minValue ELSE target.MinValue END,
        MaxValue = CASE WHEN target.MaxValue IS NULL OR @maxValue > target.MaxValue THEN @maxValue ELSE target.MaxValue END
WHEN NOT MATCHED THEN
    INSERT (HostId, SampleBucketUtc, SampleKey, SampleValue, SampleCount, FirstSampledUtc, LastSampledUtc, MinValue, MaxValue)
    VALUES (@hostId, @bucketUtc, @sampleKey, @sampleValue, 1, @sampledUtc, @sampledUtc, @minValue, @maxValue);";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            var effectiveBucketMinutes = Math.Max(1, bucketMinutes);
            var currentSampleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var sample in samples)
            {
                var key = Truncate(sample.SampleKey, HostResourceSampleKeyMaxLength);
                var minValue = sample.MinValue ?? sample.SampleValue;
                var maxValue = sample.MaxValue ?? sample.SampleValue;
                currentSampleKeys.Add(key);

                await using (var latestCmd = new SqlCommand(mergeLatestSql, conn, tx))
                {
                    Add(latestCmd, "@hostId", SqlDbType.UniqueIdentifier, hostId);
                    Add(latestCmd, "@sampleKey", SqlDbType.NVarChar, HostResourceSampleKeyMaxLength, key);
                    Add(latestCmd, "@sampleValue", SqlDbType.Float, sample.SampleValue);
                    Add(latestCmd, "@sampledUtc", SqlDbType.DateTime2, sample.SampledUtc);
                    Add(latestCmd, "@minValue", SqlDbType.Float, minValue);
                    Add(latestCmd, "@maxValue", SqlDbType.Float, maxValue);
                    await latestCmd.ExecuteNonQueryAsync(ct);
                }

                await using (var sampleCmd = new SqlCommand(mergeSamplesSql, conn, tx))
                {
                    Add(sampleCmd, "@hostId", SqlDbType.UniqueIdentifier, hostId);
                    Add(sampleCmd, "@sampleKey", SqlDbType.NVarChar, HostResourceSampleKeyMaxLength, key);
                    Add(sampleCmd, "@sampleValue", SqlDbType.Float, sample.SampleValue);
                    Add(sampleCmd, "@sampledUtc", SqlDbType.DateTime2, sample.SampledUtc);
                    Add(sampleCmd, "@minValue", SqlDbType.Float, minValue);
                    Add(sampleCmd, "@maxValue", SqlDbType.Float, maxValue);
                    Add(sampleCmd, "@bucketMinutes", SqlDbType.Int, effectiveBucketMinutes);
                    await sampleCmd.ExecuteNonQueryAsync(ct);
                }
            }

            await DeleteMissingLatestSamplesAsync(conn, tx, hostId, currentSampleKeys, ct);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<int> PruneHostResourceSamplesAsync(int retainHours, int retainDays, CancellationToken ct)
    {
        const string sql = "EXEC omp.PruneHostResourceSamples @RetainHours = @retainHours, @RetainDays = @retainDays;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@retainHours", SqlDbType.Int, Math.Max(1, retainHours));
        Add(cmd, "@retainDays", SqlDbType.Int, Math.Max(1, retainDays));

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is int count
            ? count
            : Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task DeleteMissingLatestSamplesAsync(
        SqlConnection conn,
        SqlTransaction tx,
        Guid hostId,
        IReadOnlyCollection<string> currentSampleKeys,
        CancellationToken ct)
    {
        if (currentSampleKeys.Count == 0)
        {
            return;
        }

        var parameterNames = currentSampleKeys
            .Select(static (_, index) => $"@sampleKey{index}")
            .ToArray();
        var sql = $@"
DELETE FROM omp.HostResourceLatest
WHERE HostId = @hostId
  AND SampleKey NOT IN ({string.Join(", ", parameterNames)});";

        await using var cmd = new SqlCommand(sql, conn, tx);
        Add(cmd, "@hostId", SqlDbType.UniqueIdentifier, hostId);

        var index = 0;
        foreach (var sampleKey in currentSampleKeys)
        {
            Add(cmd, parameterNames[index], SqlDbType.NVarChar, HostResourceSampleKeyMaxLength, sampleKey);
            index++;
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }
}

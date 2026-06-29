using System.Data;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed partial class OmpHostArtifactRepository
{
    private const int HostResourceSampleKeyMaxLength = 100;

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

            foreach (var sample in samples)
            {
                var key = Truncate(sample.SampleKey, HostResourceSampleKeyMaxLength);
                var minValue = sample.MinValue ?? sample.SampleValue;
                var maxValue = sample.MaxValue ?? sample.SampleValue;

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

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<int> PruneHostResourceSamplesAsync(int retainHours, CancellationToken ct)
    {
        const string sql = "EXEC omp.PruneHostResourceSamples @RetainHours = @retainHours;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@retainHours", SqlDbType.Int, Math.Max(1, retainHours));

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is int count
            ? count
            : Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }
}

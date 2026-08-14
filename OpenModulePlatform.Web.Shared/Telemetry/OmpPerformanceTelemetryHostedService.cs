// File: OpenModulePlatform.Web.Shared/Telemetry/OmpPerformanceTelemetryHostedService.cs
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenModulePlatform.Web.Shared.Services;
using System.Data;

namespace OpenModulePlatform.Web.Shared.Telemetry;

/// <summary>
/// Writes accumulated performance buckets to the database and keeps the tables bounded.
/// </summary>
/// <remarks>
/// Runs in every web app. Concurrent writers are expected and safe: the MERGE folds a
/// flush into whatever is already stored for the same bucket, so two apps -- or two
/// instances of the same app -- accumulate rather than overwrite.
///
/// Every failure path here is swallowed after logging. Telemetry that can take an
/// application down is worse than no telemetry, and this service has no output anyone
/// waits for.
/// </remarks>
public sealed class OmpPerformanceTelemetryHostedService : BackgroundService
{
    private const string MergeSql = @"
MERGE omp.PerformanceSamples WITH (HOLDLOCK) AS target
USING (SELECT @appKey AS AppKey, @scope AS Scope, @metricKey AS MetricKey, @bucketUtc AS SampleBucketUtc) AS source
ON target.SampleBucketUtc = source.SampleBucketUtc
   AND target.AppKey = source.AppKey
   AND target.Scope = source.Scope
   AND target.MetricKey = source.MetricKey
WHEN MATCHED THEN
    UPDATE SET
        SampleCount = target.SampleCount + @count,
        TotalValue = target.TotalValue + @total,
        MinValue = CASE WHEN @min < target.MinValue THEN @min ELSE target.MinValue END,
        MaxValue = CASE WHEN @max > target.MaxValue THEN @max ELSE target.MaxValue END,
        FirstSampledUtc = CASE WHEN @firstUtc < target.FirstSampledUtc THEN @firstUtc ELSE target.FirstSampledUtc END,
        LastSampledUtc = CASE WHEN @lastUtc > target.LastSampledUtc THEN @lastUtc ELSE target.LastSampledUtc END
WHEN NOT MATCHED THEN
    INSERT(AppKey, Scope, MetricKey, SampleBucketUtc, SampleCount, TotalValue, MinValue, MaxValue, FirstSampledUtc, LastSampledUtc)
    VALUES(@appKey, @scope, @metricKey, @bucketUtc, @count, @total, @min, @max, @firstUtc, @lastUtc);";

    private readonly OmpPerformanceTelemetry _telemetry;
    private readonly OmpPerformanceTelemetryOptions _options;
    private readonly SqlConnectionFactory _db;
    private readonly ILogger<OmpPerformanceTelemetryHostedService> _logger;

    /// <summary>The THROW number omp.CaptureQueryCostSnapshot raises when VIEW SERVER STATE is missing.</summary>
    private const int QueryCostPermissionErrorNumber = 51001;

    private DateTime _lastMaintenanceUtc = DateTime.MinValue;
    private bool _queryCostSnapshotsUnavailable;

    public OmpPerformanceTelemetryHostedService(
        OmpPerformanceTelemetry telemetry,
        OmpPerformanceTelemetryOptions options,
        SqlConnectionFactory db,
        ILogger<OmpPerformanceTelemetryHostedService> logger)
    {
        _telemetry = telemetry;
        _options = options;
        _db = db;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.FlushIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await FlushSafelyAsync(stoppingToken);
        }

        // A final flush on shutdown, so a restart does not discard up to a whole interval
        // of measurements. Its own cancellation token: the stopping token is already
        // cancelled by the time we get here.
        using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await FlushSafelyAsync(shutdownCts.Token);
    }

    private async Task FlushSafelyAsync(CancellationToken ct)
    {
        try
        {
            await FlushAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or TimeoutException)
        {
            // Measurements for this interval are lost. That is the correct trade: the
            // alternative is retaining them and growing the in-memory map without limit
            // while the database is unavailable.
            _logger.LogWarning(ex, "Performance telemetry flush failed; this interval's samples were dropped.");
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        var samples = _telemetry.Drain();
        var dropped = _telemetry.DroppedKeys;

        if (samples.Count == 0 && !ShouldRunMaintenance())
        {
            return;
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        foreach (var sample in samples)
        {
            await using var cmd = new SqlCommand(MergeSql, conn);
            cmd.Parameters.Add("@appKey", SqlDbType.NVarChar, 100).Value = sample.AppKey;
            cmd.Parameters.Add("@scope", SqlDbType.NVarChar, 150).Value = sample.Scope;
            cmd.Parameters.Add("@metricKey", SqlDbType.NVarChar, 100).Value = sample.MetricKey;
            cmd.Parameters.Add("@bucketUtc", SqlDbType.DateTime2).Value = sample.BucketUtc;
            cmd.Parameters.Add("@count", SqlDbType.BigInt).Value = sample.Count;
            cmd.Parameters.Add("@total", SqlDbType.Float).Value = sample.Total;
            cmd.Parameters.Add("@min", SqlDbType.Float).Value = sample.Min;
            cmd.Parameters.Add("@max", SqlDbType.Float).Value = sample.Max;
            cmd.Parameters.Add("@firstUtc", SqlDbType.DateTime2).Value = sample.FirstUtc;
            cmd.Parameters.Add("@lastUtc", SqlDbType.DateTime2).Value = sample.LastUtc;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (dropped > 0)
        {
            _logger.LogWarning(
                "Performance telemetry discarded {DroppedKeys} distinct metric keys because the ceiling of {MaxKeys} was reached. "
                    + "The recorded data is therefore incomplete; check for an unbounded scope label.",
                dropped,
                OmpPerformanceTelemetry.MaxTrackedKeys);
        }

        if (ShouldRunMaintenance())
        {
            await RunMaintenanceAsync(conn, ct);
        }
    }

    // Roll-up and prune once an hour is plenty: the hourly retention is measured in weeks,
    // so nothing is served by running it every flush.
    private bool ShouldRunMaintenance()
        => DateTime.UtcNow - _lastMaintenanceUtc >= TimeSpan.FromHours(1);

    private async Task RunMaintenanceAsync(SqlConnection conn, CancellationToken ct)
    {
        _lastMaintenanceUtc = DateTime.UtcNow;

        await using (var cmd = new SqlCommand("omp.RollUpAndPrunePerformanceSamples", conn)
        {
            CommandType = CommandType.StoredProcedure
        })
        {
            cmd.Parameters.Add("@RetainHours", SqlDbType.Int).Value = _options.RetainHours;
            cmd.Parameters.Add("@RetainDays", SqlDbType.Int).Value = _options.RetainDays;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!_options.CaptureQueryCostSnapshots || _queryCostSnapshotsUnavailable)
        {
            return;
        }

        // Hourly, from whichever app got here first. sys.dm_exec_query_stats is
        // server-wide, so several apps capturing it would store the same rows several
        // times; the snapshot is idempotent enough that duplicates are noise rather than
        // error, and hourly means at most a handful per hour.
        try
        {
            await using var snapshotCommand = new SqlCommand("omp.CaptureQueryCostSnapshot", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            snapshotCommand.Parameters.Add("@TopStatements", SqlDbType.Int).Value = _options.QueryCostSnapshotStatements;
            snapshotCommand.Parameters.Add("@RetainDays", SqlDbType.Int).Value = _options.RetainDays;
            await snapshotCommand.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex) when (ex.Number == QueryCostPermissionErrorNumber)
        {
            // The application's SQL identity lacks VIEW SERVER STATE. Nothing here can fix
            // that, and retrying every hour would log the same line forever, so it is said
            // once -- clearly enough to act on -- and then left alone for this process.
            //
            // This is not hypothetical: on the first installation the snapshot found no
            // permission, the DMV returned nothing, and the whole feature would have sat
            // there looking enabled and collecting nothing.
            _queryCostSnapshotsUnavailable = true;
            _logger.LogWarning(
                "Query cost snapshots are enabled but this application's SQL identity lacks VIEW SERVER STATE, so none will be captured by {AppKey}. "
                    + "Grant it (GRANT VIEW SERVER STATE TO [<identity>]) or set Telemetry:CaptureQueryCostSnapshots to false. Message: {Message}",
                AppDomain.CurrentDomain.FriendlyName,
                ex.Message);
        }
    }
}

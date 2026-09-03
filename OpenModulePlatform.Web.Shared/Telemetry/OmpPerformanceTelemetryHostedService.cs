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
    /// <remarks>
    /// R12-A20. This used to be 51001, which sql/2-initialize-openmoduleplatform.sql already
    /// raises for a completely unrelated condition (the default instance template could not
    /// be resolved after seeding). One number for two conditions meant the catch below could
    /// mistake a seeding failure for a missing permission and silence it. The setup script
    /// now raises 51070 for this and nothing else.
    /// </remarks>
    private const int QueryCostPermissionErrorNumber = 51070;

    /// <summary>SQL Server's error number for "Could not find stored procedure".</summary>
    /// <remarks>
    /// R12-A10. Raised when the application runs against a database whose omp schema has not
    /// been applied yet -- which is a normal window during a module upgrade, since HostAgent
    /// imports the module definition into a database the applications are already connected
    /// to. Without this the missing procedure fell through as an ordinary flush failure and
    /// was reported as lost samples, which is not what happened.
    /// </remarks>
    private const int ProcedureNotFoundErrorNumber = 2812;

    private const int MaintenanceIntervalHours = 1;

    /// <summary>How long to wait before retrying maintenance that failed.</summary>
    /// <remarks>
    /// R12-E9. The clock used to be moved forward before the work, so a maintenance pass that
    /// threw was booked as done and nothing ran for another hour. Waiting the full hour after
    /// a failure is equally wrong in the other direction: retrying on the next 60-second flush
    /// would put a failing roll-up in a tight loop and fill the log. Ten minutes is short
    /// enough that a transient failure heals within the same shift and long enough that a
    /// persistent one logs six lines an hour, not sixty.
    /// </remarks>
    private static readonly TimeSpan MaintenanceRetryDelay = TimeSpan.FromMinutes(10);

    private DateTime _nextMaintenanceUtc = DateTime.MinValue;
    private bool _queryCostSnapshotsUnavailable;
    private bool _missingProcedureReported;

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
            // Shutdown, or the final flush exceeding its own 10-second budget. Neither is
            // a fault: the interval's samples are gone either way, and logging on every
            // application stop would be noise in every log this platform produces.
        }
        // Catch-all (except the cancellation handled above). R12-E1: the curated list here
        // was SqlException, InvalidOperationException and TimeoutException. Anything else --
        // an ObjectDisposedException from a connection torn down under shutdown, a
        // NullReferenceException from a defect in the drain, an OverflowException on a
        // pathological sample -- escaped ExecuteAsync, and .NET's default
        // BackgroundServiceExceptionBehavior.StopHost then stops the entire host process.
        // Telemetry is on by default in every OMP web application, so that one uncaught type
        // would take down IbsPackager.Web, the Portal and every other consumer without any of
        // their own code being involved. The two sibling background services already learned
        // this (R3-E4 in PushEventDispatcherHostedService, R5-D1 in HostAgentHostedService);
        // the class remark above has claimed since it was written that every failure path is
        // swallowed after logging, and this is what makes that true.
        catch (Exception ex) when (ex is not OperationCanceledException)
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
            await RunMaintenanceSafelyAsync(conn, ct);
        }
    }

    // Roll-up and prune once an hour is plenty: the hourly retention is measured in weeks,
    // so nothing is served by running it every flush.
    private bool ShouldRunMaintenance()
        => DateTime.UtcNow >= _nextMaintenanceUtc;

    /// <summary>
    /// Runs the maintenance pass and schedules the next one from what actually happened.
    /// </summary>
    /// <remarks>
    /// R12-E9. Maintenance failures used to surface through the flush handler, which reports
    /// "this interval's samples were dropped" -- untrue here, because the samples were
    /// already written before maintenance runs. They are reported for what they are, and the
    /// next attempt is scheduled from the outcome rather than from the attempt.
    /// </remarks>
    private async Task RunMaintenanceSafelyAsync(SqlConnection conn, CancellationToken ct)
    {
        try
        {
            await RunMaintenanceAsync(conn, ct);
            _nextMaintenanceUtc = DateTime.UtcNow.AddHours(MaintenanceIntervalHours);
        }
        catch (OperationCanceledException)
        {
            // Shutdown. Leave the schedule alone so the next process start runs maintenance.
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _nextMaintenanceUtc = DateTime.UtcNow.Add(MaintenanceRetryDelay);
            _logger.LogWarning(
                ex,
                "Performance telemetry maintenance (roll-up, prune and query cost snapshot) failed. "
                    + "The samples for this interval were written; retrying in {RetryMinutes} minutes.",
                MaintenanceRetryDelay.TotalMinutes);
        }
    }

    private async Task RunMaintenanceAsync(SqlConnection conn, CancellationToken ct)
    {
        try
        {
            await using var cmd = new SqlCommand("omp.RollUpAndPrunePerformanceSamples", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.Add("@RetainHours", SqlDbType.Int).Value = _options.RetainHours;
            cmd.Parameters.Add("@RetainDays", SqlDbType.Int).Value = _options.RetainDays;
            cmd.Parameters.Add("@QueryCostRetainDays", SqlDbType.Int).Value = _options.QueryCostRetainDays;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex) when (ex.Number == ProcedureNotFoundErrorNumber)
        {
            // The omp schema is older than this application. Report it once and keep trying:
            // the module definition is applied to a running installation, so the procedure
            // can appear without this process being restarted (R12-A10).
            ReportMissingProcedureOnce(ex, "omp.RollUpAndPrunePerformanceSamples");
            return;
        }

        if (!_options.CaptureQueryCostSnapshots || _queryCostSnapshotsUnavailable)
        {
            return;
        }

        // Hourly, from whichever app got here first. sys.dm_exec_query_stats is server-wide,
        // so several apps capture the same statements; the snapshot folds them onto one row
        // per statement and day (R12-E6), so a second app running the same hour updates that
        // row instead of adding another copy of the statement text.
        try
        {
            await using var snapshotCommand = new SqlCommand("omp.CaptureQueryCostSnapshot", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            snapshotCommand.Parameters.Add("@TopStatements", SqlDbType.Int).Value = _options.QueryCostSnapshotStatements;
            await snapshotCommand.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex) when (ex.Number == ProcedureNotFoundErrorNumber)
        {
            // R12-A10. The exception filter added when the permission case was handled only
            // matched the permission case, so an installation whose omp schema predates the
            // snapshot procedure logged nothing recognisable and lost the roll-up's own
            // result with it. Report once, keep trying: the schema arrives by module import
            // while the application is running.
            ReportMissingProcedureOnce(ex, "omp.CaptureQueryCostSnapshot");
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

    /// <summary>
    /// Reports a missing telemetry procedure once per process.
    /// </summary>
    /// <remarks>
    /// Once, because the condition resolves by itself the moment the module definition is
    /// imported, and until then it would otherwise repeat every maintenance pass for as long
    /// as the application runs. Naming the procedure makes the line actionable: it says which
    /// schema version the database is missing, not merely that something failed.
    /// </remarks>
    private void ReportMissingProcedureOnce(SqlException exception, string procedureName)
    {
        if (_missingProcedureReported)
        {
            return;
        }

        _missingProcedureReported = true;
        _logger.LogWarning(
            exception,
            "Performance telemetry cannot run maintenance: {ProcedureName} does not exist in this database. "
                + "The omp_core module definition is older than this application; import it to create the procedure. "
                + "Further occurrences are not logged, and maintenance keeps retrying.",
            procedureName);
    }
}

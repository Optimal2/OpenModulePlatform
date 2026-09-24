// File: OpenModulePlatform.HostAgent.Runtime/Services/SystemLogRetentionService.cs
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>
/// Prunes omp.SystemLog, the platform's central Warn-and-above log that every
/// host-side process writes to through NLog's database target. Rows older than
/// <see cref="HostAgentSettings.SystemLogRetentionDays"/> are deleted in small
/// batches a few times a day. Every HostAgent runs this against the same table;
/// the deletes are idempotent and cheap, so two hosts sweeping at once only
/// share the work. The log files each process keeps are pruned by NLog itself
/// (maxArchiveDays in each process's NLog section), not here.
/// </summary>
public sealed class SystemLogRetentionService : BackgroundService
{
    internal const int BatchSize = 5000;
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly ISqlConnectionFactory _connections;
    private readonly IOptionsMonitor<HostAgentSettings> _settings;
    private readonly HostAgentProcessContext _process;
    private readonly ILogger<SystemLogRetentionService> _logger;

    public SystemLogRetentionService(
        ISqlConnectionFactory connections,
        IOptionsMonitor<HostAgentSettings> settings,
        HostAgentProcessContext process,
        ILogger<SystemLogRetentionService> logger)
    {
        _connections = connections;
        _settings = settings;
        _process = process;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first sweep waits a little so a host that has just started (or
        // just upgraded) settles before it touches the database for housekeeping.
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var days = _settings.CurrentValue.SystemLogRetentionDays;
            if (days > 0)
            {
                try
                {
                    var deleted = await PruneAsync(days, stoppingToken);
                    if (deleted > 0)
                    {
                        _logger.LogInformation(
                            "System log pruned. Deleted={Deleted}, RetentionDays={RetentionDays}, ServiceName={ServiceName}",
                            deleted,
                            days,
                            _process.ServiceName);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Housekeeping must never take the HostAgent down; the next
                    // sweep tries again, and the warning itself lands in the log.
                    _logger.LogWarning(ex, "System log pruning failed; will retry next sweep. ServiceName={ServiceName}", _process.ServiceName);
                }
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Deletes rows older than the retention in batches until none are left,
    /// so a long backlog never holds one big lock. Returns the rows deleted.
    /// </summary>
    internal async Task<long> PruneAsync(int retentionDays, CancellationToken ct)
    {
        const string sql = @"
DELETE TOP (@BatchSize) FROM omp.SystemLog
WHERE LoggedUtc < @Cutoff;";

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        long total = 0;
        await using var conn = new SqlConnection(_connections.GetConnectionString());
        await conn.OpenAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
            cmd.Parameters.Add(new SqlParameter("@BatchSize", System.Data.SqlDbType.Int) { Value = BatchSize });
            cmd.Parameters.Add(new SqlParameter("@Cutoff", System.Data.SqlDbType.DateTime2) { Value = cutoff });
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            total += deleted;
            if (deleted < BatchSize)
            {
                break;
            }
        }

        return total;
    }
}

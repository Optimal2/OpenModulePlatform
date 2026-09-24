// File: OpenModulePlatform.Portal/Services/OmpAdminRepository.SystemLog.cs
using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.Portal.Services;

public sealed class SystemLogFilter
{
    /// <summary>Process names (the NLog appName of each writer) to include; empty means every process.</summary>
    public IReadOnlyList<string> Processes { get; init; } = [];

    /// <summary>Levels to include (Warn, Error, Fatal); empty means every level.</summary>
    public IReadOnlyList<string> Levels { get; init; } = [];

    public DateTime? FromUtc { get; init; }

    /// <summary>Exclusive upper bound.</summary>
    public DateTime? ToUtc { get; init; }

    /// <summary>Substring matched against the message and the logger name.</summary>
    public string? Text { get; init; }

    public int Take { get; init; } = 200;
}

public sealed class SystemLogRow
{
    public long SystemLogId { get; init; }
    public DateTime LoggedUtc { get; init; }
    public string HostName { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public string Level { get; init; } = string.Empty;
    public string Logger { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? Exception { get; init; }
    public string? CorrelationId { get; init; }
}

public sealed partial class OmpAdminRepository
{
    public const int MaxSystemLogTake = 1000;

    /// <summary>The levels NLog writes to the table, lowest first; nothing below Warn is ever stored.</summary>
    public static readonly IReadOnlyList<string> SystemLogLevels = ["Warn", "Error", "Fatal"];

    /// <summary>
    /// Every process that has written to the system log, for the process
    /// filter. Read from the rows themselves: nothing registers as a writer,
    /// a process appears the first time it has something to say.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetSystemLogProcessesAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT DISTINCT ProcessName
FROM omp.SystemLog
ORDER BY ProcessName;";

        var rows = new List<string>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(rdr.GetString(0));
        }

        return rows;
    }

    /// <summary>The newest rows that match the filter, newest first.</summary>
    public async Task<IReadOnlyList<SystemLogRow>> SearchSystemLogAsync(SystemLogFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var take = Math.Clamp(filter.Take, 1, MaxSystemLogTake);
        var processes = filter.Processes.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var levels = filter.Levels
            .Where(l => SystemLogLevels.Contains(l, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sql = new StringBuilder();
        sql.Append("SELECT TOP (@Take) s.SystemLogId, s.LoggedUtc, s.HostName, s.ProcessName, s.Level, s.Logger, s.Message, s.Exception, s.CorrelationId\n");
        sql.Append("FROM omp.SystemLog s\nWHERE 1 = 1");
        if (processes.Count > 0)
        {
            sql.Append(" AND s.ProcessName IN (").Append(string.Join(", ", processes.Select((_, i) => "@Process" + i))).Append(')');
        }

        if (levels.Count > 0)
        {
            sql.Append(" AND s.Level IN (").Append(string.Join(", ", levels.Select((_, i) => "@Level" + i))).Append(')');
        }

        if (filter.FromUtc.HasValue)
        {
            sql.Append(" AND s.LoggedUtc >= @FromUtc");
        }

        if (filter.ToUtc.HasValue)
        {
            sql.Append(" AND s.LoggedUtc < @ToUtc");
        }

        var hasText = !string.IsNullOrWhiteSpace(filter.Text);
        if (hasText)
        {
            sql.Append(" AND (s.Message LIKE @Text ESCAPE '\\' OR s.Logger LIKE @Text ESCAPE '\\')");
        }

        sql.Append("\nORDER BY s.LoggedUtc DESC, s.SystemLogId DESC;");

        var rows = new List<SystemLogRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql.ToString(), conn);
        cmd.Parameters.Add(new SqlParameter("@Take", SqlDbType.Int) { Value = take });
        for (var i = 0; i < processes.Count; i++)
        {
            cmd.Parameters.Add(new SqlParameter("@Process" + i, SqlDbType.NVarChar, 128) { Value = processes[i] });
        }

        for (var i = 0; i < levels.Count; i++)
        {
            cmd.Parameters.Add(new SqlParameter("@Level" + i, SqlDbType.NVarChar, 16) { Value = levels[i] });
        }

        if (filter.FromUtc.HasValue)
        {
            cmd.Parameters.Add(new SqlParameter("@FromUtc", SqlDbType.DateTime2) { Value = filter.FromUtc.Value });
        }

        if (filter.ToUtc.HasValue)
        {
            cmd.Parameters.Add(new SqlParameter("@ToUtc", SqlDbType.DateTime2) { Value = filter.ToUtc.Value });
        }

        if (hasText)
        {
            cmd.Parameters.Add(new SqlParameter("@Text", SqlDbType.NVarChar, -1) { Value = "%" + EscapeLike(filter.Text!.Trim()) + "%" });
        }

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new SystemLogRow
            {
                SystemLogId = rdr.GetInt64(0),
                LoggedUtc = DateTime.SpecifyKind(rdr.GetDateTime(1), DateTimeKind.Utc),
                HostName = rdr.GetString(2),
                ProcessName = rdr.GetString(3),
                Level = rdr.GetString(4),
                Logger = rdr.GetString(5),
                Message = rdr.GetString(6),
                Exception = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                CorrelationId = rdr.IsDBNull(8) ? null : rdr.GetString(8)
            });
        }

        return rows;
    }
}

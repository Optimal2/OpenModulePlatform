// File: OpenModulePlatform.Web.Shared/ActivityLog/ActivityLogWriter.cs
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Web.Shared.ActivityLog;

/// <summary>
/// Where a module's activity entries go: its own schema, its module key, and the
/// component key of the app writing them.
/// </summary>
public sealed class ActivityLogOptions
{
    /// <summary>The module's SQL schema (omp.Modules.SchemaName), e.g. <c>omp_ibs_packager</c>. The table is <c>&lt;schema&gt;.ActivityLog</c>.</summary>
    public required string SchemaName { get; init; }

    /// <summary>The module key (omp.Modules.ModuleKey), e.g. <c>ibs_packager</c>.</summary>
    public required string ModuleKey { get; init; }

    /// <summary>The component key of the writing app, e.g. <c>ibs-packager-web</c>.</summary>
    public required string AppKey { get; init; }
}

/// <summary>
/// Appends activity entries to the module's ActivityLog table as the versioned JSON
/// envelope. One row per entry: when it happened, which OMP user, and the envelope.
/// </summary>
/// <remarks>
/// A failed write is logged as an error and does not fail the business action: the
/// activity log is a record of what happened, and refusing the action because the
/// record could not be written would change what happened. That trade-off is
/// deliberate for version 1 and is the reason every failure is logged at error level.
/// <para>
/// This is a user log: every entry is something a signed-in person did with a
/// click, never something the system did on its own schedule. A row without an
/// OMP user id is therefore a defect in the caller, not a "system action"; the
/// writer still stores it (losing the record would hide the defect) and warns.
/// </para>
/// </remarks>
public sealed class ActivityLogWriter
{
    private readonly SqlConnectionFactory _db;
    private readonly ActivityLogOptions _options;
    private readonly ILogger<ActivityLogWriter> _logger;
    private readonly string _insertSql;

    public ActivityLogWriter(SqlConnectionFactory db, ActivityLogOptions options, ILogger<ActivityLogWriter> logger)
    {
        _db = db;
        _options = options;
        _logger = logger;
        _insertSql = ActivityLogSql.InsertStatement(options.SchemaName);
    }

    public ActivityLogOptions Options => _options;

    /// <summary>
    /// Writes an entry on behalf of a signed-in principal: the OMP user id comes
    /// from the principal, and a missing <see cref="ActivityEntry.Actor"/> is filled
    /// with the principal's display name.
    /// </summary>
    public Task WriteAsync(ActivityEntry entry, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        var userId = OmpUserIdentity.TryGetOmpUserId(user);
        var actor = entry.Actor ?? new ActivityActor(ActivityActorKinds.User, OmpUserIdentity.TryGetDisplayName(user));
        return WriteAsync(entry with { Actor = actor }, userId, ct);
    }

    /// <summary>
    /// Writes an entry with an explicit OMP user id. Null is accepted so no record is
    /// lost, but it is logged as a warning: an activity entry always belongs to a person.
    /// </summary>
    public async Task WriteAsync(ActivityEntry entry, int? ompUserId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (ompUserId is null)
        {
            _logger.LogWarning(
                "Activity log entry for {Module}/{App} event {Event} has no OMP user id; the activity log is a user log and every entry should name the person who acted: {Summary}",
                _options.ModuleKey, _options.AppKey, entry.Event, entry.Summary);
        }

        if (string.IsNullOrWhiteSpace(entry.Event))
        {
            throw new ArgumentException("An activity entry needs an event key.", nameof(entry));
        }

        if (string.IsNullOrWhiteSpace(entry.Summary))
        {
            throw new ArgumentException("An activity entry needs a summary.", nameof(entry));
        }

        var envelope = ToEnvelope(entry);
        var json = ActivityLogJson.Serialize(envelope);

        try
        {
            await using var connection = _db.Create();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = new SqlCommand(_insertSql, connection);
            command.Parameters.Add(new SqlParameter("@OmpUserId", System.Data.SqlDbType.Int) { Value = (object?)ompUserId ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@Entry", System.Data.SqlDbType.NVarChar, -1) { Value = json });
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception,
                "Activity log write failed for {Module}/{App} event {Event} (user {UserId}); the entry is lost: {Summary}",
                _options.ModuleKey, _options.AppKey, entry.Event, ompUserId, entry.Summary);
        }
    }

    private ActivityEnvelope ToEnvelope(ActivityEntry entry)
    {
        JsonElement? data = null;
        if (entry.Data is { Count: > 0 })
        {
            data = JsonSerializer.SerializeToElement(entry.Data, ActivityLogJson.Options);
        }

        return new ActivityEnvelope
        {
            V = ActivityEnvelope.CurrentVersion,
            Event = entry.Event.Trim(),
            Module = _options.ModuleKey,
            App = _options.AppKey,
            Outcome = string.IsNullOrWhiteSpace(entry.Outcome) ? ActivityOutcomes.Ok : entry.Outcome,
            Summary = entry.Summary.Trim(),
            Subject = entry.Subject,
            Actor = entry.Actor,
            Correlation = string.IsNullOrWhiteSpace(entry.Correlation) ? null : entry.Correlation,
            Data = data
        };
    }
}

/// <summary>
/// The SQL every module uses for its ActivityLog table. The DDL is the canonical
/// text a module copies into its idempotent setup script (module SQL ships as files
/// embedded in the module definition, so it cannot reference this class at run time);
/// the insert is what the writer executes.
/// </summary>
public static class ActivityLogSql
{
    public const string TableName = "ActivityLog";

    public static string InsertStatement(string schemaName)
        => $"INSERT INTO [{Quote(schemaName)}].[{TableName}] (LoggedUtc, OmpUserId, Entry) VALUES (SYSUTCDATETIME(), @OmpUserId, @Entry);";

    /// <summary>Idempotent DDL: table, primary key, JSON check and the two read indexes.</summary>
    public static string CreateTableStatement(string schemaName)
    {
        var schema = Quote(schemaName);
        return $"""
IF OBJECT_ID(N'[{schema}].[{TableName}]', N'U') IS NULL
BEGIN
    CREATE TABLE [{schema}].[{TableName}]
    (
        ActivityLogId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_{schema}_{TableName} PRIMARY KEY CLUSTERED,
        LoggedUtc datetime2(3) NOT NULL CONSTRAINT DF_{schema}_{TableName}_LoggedUtc DEFAULT (SYSUTCDATETIME()),
        OmpUserId int NULL,
        Entry nvarchar(max) NOT NULL CONSTRAINT CK_{schema}_{TableName}_Entry CHECK (ISJSON(Entry) = 1)
    );
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_{schema}_{TableName}_LoggedUtc' AND object_id = OBJECT_ID(N'[{schema}].[{TableName}]'))
    CREATE INDEX IX_{schema}_{TableName}_LoggedUtc ON [{schema}].[{TableName}] (LoggedUtc DESC, ActivityLogId DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_{schema}_{TableName}_OmpUserId' AND object_id = OBJECT_ID(N'[{schema}].[{TableName}]'))
    CREATE INDEX IX_{schema}_{TableName}_OmpUserId ON [{schema}].[{TableName}] (OmpUserId, LoggedUtc DESC, ActivityLogId DESC);
""";
    }

    private static string Quote(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Any(c => !(char.IsLetterOrDigit(c) || c == '_')))
        {
            throw new ArgumentException($"'{identifier}' is not a plain SQL identifier.", nameof(identifier));
        }

        return identifier;
    }
}

// File: OpenModulePlatform.Web.ContentWebAppModule/Services/ServerReportQueryRunner.cs
using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.ContentWebAppModule.Models;
using OpenModulePlatform.Web.ContentWebAppModule.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Web.ContentWebAppModule.Services;

public sealed partial class ServerReportQueryRunner
{
    private readonly SqlConnectionFactory _db;
    private readonly IOptions<ContentWebAppModuleOptions> _options;
    private readonly ILogger<ServerReportQueryRunner> _logger;

    public ServerReportQueryRunner(
        SqlConnectionFactory db,
        IOptions<ContentWebAppModuleOptions> options,
        ILogger<ServerReportQueryRunner> logger)
    {
        _db = db;
        _options = options;
        _logger = logger;
    }

    public async Task<ServerReportResult> ExecuteAsync(ServerReportDefinition definition, CancellationToken ct)
    {
        var database = ResolveDatabase(definition.Database);
        if (database.ErrorMessage is not null)
        {
            return new ServerReportResult
            {
                Title = definition.Title,
                Queries =
                [
                    new ServerReportQueryResult
                    {
                        Title = "Server report",
                        ErrorMessage = database.ErrorMessage
                    }
                ]
            };
        }

        var results = new List<ServerReportQueryResult>();
        foreach (var query in definition.Queries)
        {
            results.Add(await ExecuteQueryAsync(query, database.DatabaseName, ct));
        }

        return new ServerReportResult
        {
            Title = definition.Title,
            Queries = results
        };
    }

    private async Task<ServerReportQueryResult> ExecuteQueryAsync(
        ServerReportQueryDefinition query,
        string? databaseName,
        CancellationToken ct)
    {
        var maxRows = GetMaxRows(query.MaxRows);
        var result = new ServerReportQueryResult
        {
            Name = query.Name,
            Title = string.IsNullOrWhiteSpace(query.Title) ? query.Name : query.Title,
            MaxRows = maxRows
        };

        var validationError = ValidateSql(query.Sql);
        if (validationError is not null)
        {
            result.ErrorMessage = validationError;
            return result;
        }

        try
        {
            await using var conn = string.IsNullOrWhiteSpace(databaseName)
                ? _db.Create()
                : _db.CreateForDatabase(databaseName);
            await conn.OpenAsync(ct);

            await using var cmd = new SqlCommand(query.Sql.Trim(), conn)
            {
                CommandType = CommandType.Text,
                CommandTimeout = Math.Max(1, _options.Value.ServerReportQueryTimeoutSeconds)
            };

            await using var rdr = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            var columns = Enumerable.Range(0, rdr.FieldCount)
                .Select(rdr.GetName)
                .ToArray();

            var rows = new List<IReadOnlyList<string?>>();
            while (await rdr.ReadAsync(ct))
            {
                if (rows.Count >= maxRows)
                {
                    result.IsTruncated = true;
                    break;
                }

                var values = new string?[rdr.FieldCount];
                for (var i = 0; i < rdr.FieldCount; i++)
                {
                    values[i] = rdr.IsDBNull(i)
                        ? null
                        : FormatValue(rdr.GetValue(i));
                }

                rows.Add(values);
            }

            result.Columns = columns;
            result.Rows = rows;
            return result;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Server report query failed: {QueryName}", query.Name);
            result.ErrorMessage = "The report query failed.";
            return result;
        }
    }

    private DatabaseResolution ResolveDatabase(string? requestedDatabase)
    {
        if (string.IsNullOrWhiteSpace(requestedDatabase))
        {
            return new DatabaseResolution(null, null);
        }

        var database = requestedDatabase.Trim();
        if (database.Length > 128 || database.Any(char.IsControl))
        {
            return new DatabaseResolution(null, "The report database is not allowed.");
        }

        var match = (_options.Value.AllowedServerReportDatabases ?? [])
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Select(static x => x.Trim())
            .FirstOrDefault(x => string.Equals(x, database, StringComparison.OrdinalIgnoreCase));

        return match is null
            ? new DatabaseResolution(null, "The report database is not allowed.")
            : new DatabaseResolution(match, null);
    }

    private int GetMaxRows(int? requestedMaxRows)
    {
        var defaultMaxRows = Math.Max(1, _options.Value.ServerReportDefaultMaxRows);
        var limit = Math.Max(1, _options.Value.ServerReportMaxRowsLimit);
        var maxRows = requestedMaxRows.GetValueOrDefault(defaultMaxRows);
        return Math.Clamp(maxRows, 1, limit);
    }

    private static string? ValidateSql(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return "The report query is empty.";
        }

        var trimmed = sql.Trim();
        if (!AllowedStartRegex().IsMatch(trimmed))
        {
            return "Only SELECT or WITH queries are allowed.";
        }

        var withoutTerminalSemicolon = trimmed.EndsWith(';')
            ? trimmed[..^1].TrimEnd()
            : trimmed;

        if (withoutTerminalSemicolon.Contains(';', StringComparison.Ordinal))
        {
            return "Multiple SQL statements are not allowed.";
        }

        if (BlockedSqlRegex().IsMatch(trimmed))
        {
            return "The report query contains a blocked SQL command.";
        }

        return null;
    }

    private static string FormatValue(object value)
        => value switch
        {
            DateTime dateTime => dateTime.ToString("u", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("u", CultureInfo.InvariantCulture),
            byte[] bytes => $"[binary {bytes.Length} bytes]",
            IFormattable formattable => formattable.ToString(null, CultureInfo.CurrentCulture),
            _ => value.ToString() ?? string.Empty
        };

    [GeneratedRegex(@"^\s*(select|with)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AllowedStartRegex();

    [GeneratedRegex(@"\b(insert|update|delete|drop|alter|truncate|exec|execute|merge|create|into|use)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockedSqlRegex();

    private readonly record struct DatabaseResolution(string? DatabaseName, string? ErrorMessage);
}

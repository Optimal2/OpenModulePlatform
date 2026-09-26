using System.Data;
using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.ModuleDefinitions;

/// <summary>
/// Runs the <c>runtimeMaintenance</c> steps that the applied module definitions declare for one
/// platform event, inside the caller's transaction. When no applied definition declares a step for
/// the event, nothing runs: there is no built-in fallback that knows a module's tables.
/// </summary>
/// <remarks>
/// Source-linked into HostAgent and Portal. Steps are re-validated when they are loaded, so a
/// definition row edited in the database after import is refused rather than executed. Each step
/// is a parameterized command, which SqlClient sends as sp_executesql with the event parameter
/// bound; step SQL never concatenates values.
/// </remarks>
internal static class ModuleRuntimeMaintenanceExecutor
{
    internal sealed record BlockingRows(string ModuleKey, string StepKey, int Count, string? Description);

    // Latest applied definition per module, the same ordering the compatibility checks use. The
    // LIKE filter keeps definitions without the section (the platform core definition is large)
    // off the wire; ReadSteps still decides from the parsed document.
    private const string LoadSql = @"
IF OBJECT_ID(N'omp.ModuleDefinitionDocuments', N'U') IS NOT NULL
BEGIN
    SELECT applied.ModuleKey,
           applied.DefinitionJson
    FROM
    (
        SELECT ModuleKey,
               DefinitionJson,
               ROW_NUMBER() OVER
               (
                   PARTITION BY ModuleKey
                   ORDER BY AppliedUtc DESC, UpdatedUtc DESC, ModuleDefinitionDocumentId DESC
               ) AS rn
        FROM omp.ModuleDefinitionDocuments
        WHERE IsApplied = 1
    ) applied
    WHERE applied.rn = 1
      AND applied.DefinitionJson LIKE N'%""runtimeMaintenance""%'
    ORDER BY applied.ModuleKey;
END;";

    internal static async Task<IReadOnlyList<ModuleRuntimeMaintenance.Step>> LoadStepsAsync(
        SqlConnection conn,
        SqlTransaction? tx,
        string eventName,
        CancellationToken ct)
    {
        if (!ModuleRuntimeMaintenance.Events.ContainsKey(eventName))
            throw new ArgumentOutOfRangeException(nameof(eventName), eventName, "Unknown runtime maintenance event.");

        var documents = new List<string>();
        await using (var cmd = new SqlCommand(LoadSql, conn, tx))
        await using (var rdr = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rdr.ReadAsync(ct))
            {
                documents.Add(rdr.GetString(1));
            }
        }

        return documents
            .SelectMany(ModuleRuntimeMaintenance.ReadSteps)
            .Where(step => step.Event == eventName)
            .OrderBy(static step => step.ModuleKey, StringComparer.Ordinal)
            .ThenBy(static step => step.Order)
            .ThenBy(static step => step.Key, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Runs every declared step for a mutating event; returns how many steps ran.</summary>
    internal static async Task<int> RunAsync(
        SqlConnection conn,
        SqlTransaction? tx,
        string eventName,
        object parameterValue,
        CancellationToken ct)
    {
        var contract = ModuleRuntimeMaintenance.Events[eventName];
        if (contract.Execution != ModuleRuntimeMaintenance.IdempotentExecution)
            throw new InvalidOperationException($"Event '{eventName}' is read-only; use {nameof(CountBlockingRowsAsync)}.");

        var steps = await LoadStepsAsync(conn, tx, eventName, ct);
        foreach (var step in steps)
        {
            await using var cmd = CreateCommand(conn, tx, step, contract, parameterValue);
            try
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (SqlException ex)
            {
                throw StepFailed(step, ex);
            }
        }

        return steps.Count;
    }

    /// <summary>
    /// Runs the declared <c>app-instance-blocking-count</c> steps and returns the modules that
    /// still own rows which cannot be unlinked from the app instance. A step reports its count in
    /// a <c>BlockingCount</c> column (or the first column) of its first row, and may name the rows
    /// in an optional <c>Description</c> column. A step that returns no row reports zero.
    /// </summary>
    internal static async Task<IReadOnlyList<BlockingRows>> CountBlockingRowsAsync(
        SqlConnection conn,
        SqlTransaction? tx,
        Guid appInstanceId,
        CancellationToken ct)
    {
        var contract = ModuleRuntimeMaintenance.Events[ModuleRuntimeMaintenance.AppInstanceBlockingCount];
        var steps = await LoadStepsAsync(conn, tx, contract.Name, ct);
        var blocking = new List<BlockingRows>();
        foreach (var step in steps)
        {
            await using var cmd = CreateCommand(conn, tx, step, contract, appInstanceId);
            try
            {
                await using var rdr = await cmd.ExecuteReaderAsync(ct);
                do
                {
                    if (!await rdr.ReadAsync(ct))
                    {
                        continue;
                    }

                    var countOrdinal = FindOrdinal(rdr, "BlockingCount") ?? 0;
                    var count = rdr.IsDBNull(countOrdinal) ? 0 : Convert.ToInt32(rdr.GetValue(countOrdinal), System.Globalization.CultureInfo.InvariantCulture);
                    var descriptionOrdinal = FindOrdinal(rdr, "Description");
                    var description = descriptionOrdinal is { } ordinal && !rdr.IsDBNull(ordinal)
                        ? Convert.ToString(rdr.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture)
                        : null;
                    if (count > 0)
                    {
                        blocking.Add(new BlockingRows(step.ModuleKey, step.Key, count, string.IsNullOrWhiteSpace(description) ? null : description.Trim()));
                    }

                    break;
                }
                while (await rdr.NextResultAsync(ct));
            }
            catch (SqlException ex)
            {
                throw StepFailed(step, ex);
            }
        }

        return blocking;
    }

    private static SqlCommand CreateCommand(
        SqlConnection conn,
        SqlTransaction? tx,
        ModuleRuntimeMaintenance.Step step,
        ModuleRuntimeMaintenance.EventContract contract,
        object parameterValue)
    {
        var cmd = new SqlCommand(step.Sql, conn, tx);
        var type = contract.ParameterSqlType switch
        {
            "uniqueidentifier" => SqlDbType.UniqueIdentifier,
            "int" => SqlDbType.Int,
            _ => throw new InvalidOperationException($"Unsupported runtime maintenance parameter type '{contract.ParameterSqlType}'."),
        };
        cmd.Parameters.Add(new SqlParameter(contract.ParameterName, type) { Value = parameterValue });
        return cmd;
    }

    private static int? FindOrdinal(SqlDataReader rdr, string name)
    {
        for (var i = 0; i < rdr.FieldCount; i++)
        {
            if (string.Equals(rdr.GetName(i), name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return null;
    }

    private static InvalidOperationException StepFailed(ModuleRuntimeMaintenance.Step step, SqlException ex)
        => new(
            $"{ModuleRuntimeMaintenance.RuleId}: Runtime maintenance step '{step.Key}' of module '{step.ModuleKey}' failed for event '{step.Event}': {ex.Message}",
            ex);
}

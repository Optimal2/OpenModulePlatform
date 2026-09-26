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
/// definition row edited in the database after import is refused rather than executed. Each
/// applied definition is validated on its own: a corrupt document is reported as a failure of
/// that module, next to every other failing module, and the event is refused fail-closed before
/// any step runs, so the platform change it belongs to is aborted. Each step
/// is a parameterized command, which SqlClient sends as sp_executesql with the event parameter
/// bound; step SQL never concatenates values.
/// </remarks>
internal static class ModuleRuntimeMaintenanceExecutor
{
    internal sealed record BlockingRows(string ModuleKey, string StepKey, int Count, string? Description);

    // Latest applied definition per module, the same ordering the compatibility checks use, with
    // the module's registration in omp.Modules. A registered key that differs from the applied
    // key only in letter case counts as another claimant: both derive the same schema, and a
    // case-insensitive collation would join the two. There is deliberately no text pre-filter on the
    // JSON: System.Text.Json reads a property name spelled with a JSON unicode escape for one of
    // its letters as the section, so a LIKE filter on the literal name would silently skip steps
    // the import gate validated. The parsed
    // document alone decides whether a module declares steps.
    private const string LoadSql = @"
IF OBJECT_ID(N'omp.ModuleDefinitionDocuments', N'U') IS NOT NULL
BEGIN
    SELECT applied.ModuleKey,
           applied.DefinitionJson,
           registered.SchemaName AS RegisteredSchemaName,
           (
               SELECT COUNT(*)
               FROM omp.Modules other
               WHERE CONVERT(varbinary(400), other.ModuleKey) <> CONVERT(varbinary(400), applied.ModuleKey)
                 AND (other.SchemaName = registered.SchemaName
                      OR other.ModuleKey = registered.SchemaName
                      OR UPPER(other.ModuleKey) = UPPER(applied.ModuleKey))
           ) AS OtherClaimants
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
    LEFT JOIN omp.Modules registered
        ON registered.ModuleKey = applied.ModuleKey
    WHERE applied.rn = 1
    ORDER BY applied.ModuleKey;
END;";

    internal sealed record AppliedDefinition(string ModuleKey, string DefinitionJson, string? RegisteredSchemaName, int OtherClaimants);

    /// <summary>A module whose applied definition could not be bound, and why.</summary>
    internal sealed record ModuleFailure(string ModuleKey, string Reason);

    internal sealed record BoundEvent(IReadOnlyList<ModuleRuntimeMaintenance.Step> Steps, IReadOnlyList<ModuleFailure> Failures);

    internal static async Task<IReadOnlyList<ModuleRuntimeMaintenance.Step>> LoadStepsAsync(
        SqlConnection conn,
        SqlTransaction? tx,
        string eventName,
        CancellationToken ct)
    {
        if (!ModuleRuntimeMaintenance.Events.ContainsKey(eventName))
            throw new ArgumentOutOfRangeException(nameof(eventName), eventName, "Unknown runtime maintenance event.");

        var documents = new List<AppliedDefinition>();
        await using (var cmd = new SqlCommand(LoadSql, conn, tx))
        await using (var rdr = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rdr.ReadAsync(ct))
            {
                documents.Add(new AppliedDefinition(
                    rdr.GetString(0),
                    rdr.GetString(1),
                    rdr.IsDBNull(2) ? null : rdr.GetString(2),
                    rdr.GetInt32(3)));
            }
        }

        var bound = Bind(documents, eventName);
        if (bound.Failures.Count != 0)
        {
            throw new InvalidOperationException(
                $"{ModuleRuntimeMaintenance.RuleId}: Event '{eventName}' was refused; runtime maintenance failed for "
                + $"{bound.Failures.Count} module(s) and no step ran: "
                + string.Join(" | ", bound.Failures.Select(static failure => $"[{failure.ModuleKey}] {failure.Reason}")));
        }

        return bound.Steps;
    }

    /// <summary>
    /// Validates and binds every applied definition on its own. A definition that cannot be read,
    /// validated or bound -- whatever the exception -- becomes a <see cref="ModuleFailure"/> of
    /// that module and never hides another module's failure. Nothing module-specific is known
    /// about a failed document, so it fails every event, not only the ones it might declare.
    /// </summary>
    internal static BoundEvent Bind(IEnumerable<AppliedDefinition> definitions, string eventName)
    {
        var steps = new List<ModuleRuntimeMaintenance.Step>();
        var failures = new List<ModuleFailure>();
        foreach (var definition in definitions)
        {
            try
            {
                steps.AddRange(BoundSteps(definition).Where(step => step.Event == eventName));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(new ModuleFailure(definition.ModuleKey, ex.Message));
            }
        }

        return new BoundEvent(
            steps
                .OrderBy(static step => step.ModuleKey, StringComparer.Ordinal)
                .ThenBy(static step => step.Order)
                .ThenBy(static step => step.Key, StringComparer.Ordinal)
                .ToArray(),
            failures);
    }

    /// <summary>
    /// Re-validates one stored definition and binds its steps to the platform's registration of
    /// the module: the document's moduleKey must be the row's module key spelled exactly the same,
    /// omp.Modules must register the derived schema for that module, and no other module may
    /// register or be named as that schema or differ from its key only in letter case. Any mismatch refuses the whole event rather than running a step against a schema
    /// the module does not own.
    /// </summary>
    private static IReadOnlyList<ModuleRuntimeMaintenance.Step> BoundSteps(AppliedDefinition definition)
    {
        var steps = ModuleRuntimeMaintenance.ReadSteps(definition.DefinitionJson);
        if (steps.Count == 0)
        {
            return steps;
        }

        var schema = steps[0].SchemaName;
        string? problem = null;
        if (!string.Equals(steps[0].ModuleKey, definition.ModuleKey, StringComparison.Ordinal))
            problem = $"the stored document declares moduleKey '{steps[0].ModuleKey}'";
        else if (definition.RegisteredSchemaName is null)
            problem = "the module is not registered in omp.Modules";
        else if (!string.Equals(definition.RegisteredSchemaName, schema, StringComparison.OrdinalIgnoreCase))
            problem = $"omp.Modules registers schema '{definition.RegisteredSchemaName}', not the derived schema '{schema}'";
        else if (definition.OtherClaimants != 0)
            problem = $"another module in omp.Modules claims schema '{schema}'";

        if (problem is not null)
        {
            throw new InvalidOperationException(
                $"{ModuleRuntimeMaintenance.RuleId}: Module '{definition.ModuleKey}' runtime maintenance was blocked: {problem}.");
        }

        return steps;
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

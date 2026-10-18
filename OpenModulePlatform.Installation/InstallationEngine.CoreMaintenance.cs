using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.Installation;

public static partial class InstallationEngine
{
    internal static void ValidateExistingCoreTransition(ModuleDefinitionDocument definition, AppliedModuleDefinition? current)
    {
        if (current is null)
            throw new InvalidOperationException("Core maintenance requires an existing applied omp_core definition; use bootstrap for first installation.");
        if (CompareVersionText(definition.DefinitionVersion, current.DefinitionVersion) < 0)
            throw new InvalidOperationException("Core maintenance refuses a downgrade.");
        if (CompareVersionText(definition.DefinitionVersion, current.DefinitionVersion) == 0
            && !string.Equals(definition.DefinitionSha256, current.DefinitionSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Core maintenance refuses same-version definition hash drift; publish a new definition version.");
    }

    internal static void ValidateCoreMaintenanceSchema(bool hasHostScopeAndTrustedConstraint)
    {
        if (!hasHostScopeAndTrustedConstraint)
            throw new InvalidOperationException("Core maintenance could not verify HostScope and its trusted constraint. Do not stage artifacts.");
    }

    internal static void ValidateCoreMaintenancePayload(ModuleDefinitionDocument definition, string expectedVersion, SqlBootstrapOptions sql, string payloadRoot)
    {
        using var document = JsonDocument.Parse(definition.DefinitionJson);
        var root = document.RootElement;
        if (definition.ModuleKey != "omp_core" || definition.FormatVersion != 1
            || root.GetProperty("moduleKey").GetString() != "omp_core"
            || root.GetProperty("definitionVersion").GetString() != expectedVersion
            || root.GetProperty("formatVersion").ValueKind != JsonValueKind.Number
            || !root.GetProperty("formatVersion").TryGetInt32(out var formatVersion) || formatVersion != 1
            || root.GetProperty("definitionType").GetString() != "platform-core"
            || root.GetProperty("module").GetProperty("moduleType").GetString() != "PlatformCore"
            || root.GetProperty("module").GetProperty("schemaName").GetString() != "omp"
            || !Regex.IsMatch(expectedVersion, @"^\d+\.\d+\.\d+$")
            || definition.DefinitionVersion != expectedVersion || CompareVersionText(expectedVersion, "0.3.270") < 0)
            throw new InvalidOperationException("Core maintenance requires the exact expected platform-core identity and version (at least 0.3.270).");

        var scripts = ReadPortableSqlScripts(definition.DefinitionJson);
        if (scripts.Count == 0 || scripts.Select(s => s.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != scripts.Count
            || scripts.Count(s => s.Key == "setup-core-schema" && !IsValidationScript(s)) != 1
            || !scripts.Any(IsValidationScript))
            throw new InvalidOperationException("Core maintenance requires unique embedded SQL keys, setup-core-schema and validation SQL.");

        foreach (var script in scripts)
        {
            var text = ResolvePortableSqlText(script)!;
            if (script.Sha256 is null || !Regex.IsMatch(script.Sha256, @"^[0-9a-fA-F]{64}$")
                || !string.Equals(script.Sha256, ComputeTextSha256(text), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Core SQL '{script.Key}' content does not match its declared SHA-256.");
            if (!IsValidationScript(script) && script.Execution != "idempotent")
                throw new InvalidOperationException($"Core SQL '{script.Key}' must be idempotent.");
            var prepared = PreprocessSql(text, sql, script.Path ?? script.Source ?? script.Key, payloadRoot);
            var error = IsValidationScript(script) ? ValidateReadOnlyModuleDefinitionSql(prepared) : ValidateSafeModuleDefinitionSql(prepared);
            if (error is not null)
                throw new InvalidOperationException($"Core SQL '{script.Key}' was blocked: {error}");
        }
    }

    private static async Task AssertSingleAppliedCoreAsync(SqlConnection connection, int timeout)
    {
        // Do not let TOP(1) hide duplicate applied rows or a case-variant identity.
        const string text = """
SELECT ModuleKey FROM omp.ModuleDefinitionDocuments
WHERE LOWER(ModuleKey) = N'omp_core' AND IsApplied = 1;
""";
        await using var command = new SqlCommand(text, connection) { CommandTimeout = timeout };
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || reader.GetString(0) != "omp_core" || await reader.ReadAsync())
            throw new InvalidOperationException("Core maintenance requires exactly one canonical applied omp_core identity.");
    }

    private static async Task AssertCoreDefinitionHashAsync(SqlConnection connection, ModuleDefinitionDocument definition, int timeout)
    {
        const string text = """
SELECT ModuleKey, DefinitionSha256 FROM omp.ModuleDefinitionDocuments
WHERE LOWER(ModuleKey) = N'omp_core' AND DefinitionVersion = @version;
""";
        await using var command = new SqlCommand(text, connection) { CommandTimeout = timeout };
        command.Parameters.AddWithValue("@version", definition.DefinitionVersion);
        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync()
            && (reader.GetString(0) != "omp_core" || reader.IsDBNull(1)
                || !string.Equals(reader.GetString(1), definition.DefinitionSha256, StringComparison.OrdinalIgnoreCase)
                || await reader.ReadAsync()))
            throw new InvalidOperationException("Core maintenance refuses an existing target-version identity/hash conflict.");
    }

    private static async Task VerifyCoreMaintenanceAsync(SqlConnection connection, ModuleDefinitionDocument definition, SqlBootstrapOptions sql, string payloadRoot)
    {
        var timeout = sql.CommandTimeoutSeconds;
        await AssertSingleAppliedCoreAsync(connection, timeout);
        var current = await QueryAppliedModuleDefinitionAsync(connection, "omp_core", timeout);
        if (current?.DefinitionVersion != definition.DefinitionVersion
            || !string.Equals(current.DefinitionSha256, definition.DefinitionSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Core maintenance could not verify the applied version and hash. Do not stage artifacts.");

        const string text = """
SELECT CASE WHEN COL_LENGTH(N'omp.ConfigOverlayDocuments', N'HostScope') IS NOT NULL
 AND EXISTS (SELECT 1 FROM sys.check_constraints
     WHERE parent_object_id = OBJECT_ID(N'omp.ConfigOverlayDocuments')
       AND name = N'CK_omp_ConfigOverlayDocuments_HostScope' AND is_disabled = 0 AND is_not_trusted = 0)
 THEN 1 ELSE 0 END;
""";
        await using var command = new SqlCommand(text, connection) { CommandTimeout = timeout };
        ValidateCoreMaintenanceSchema(Convert.ToInt32(await command.ExecuteScalarAsync()) == 1);

        var scripts = ReadPortableSqlScripts(definition.DefinitionJson);
        if (await HasUnexecutedModuleDefinitionSqlAsync(connection, current!.ModuleDefinitionDocumentId, scripts.Where(s => !IsValidationScript(s)), timeout))
            throw new InvalidOperationException("Core maintenance has incomplete SQL execution history. Do not stage artifacts.");
        foreach (var script in scripts.Where(IsValidationScript))
        {
            var prepared = PreprocessSql(ResolvePortableSqlText(script)!, sql, script.Path ?? script.Source ?? script.Key, payloadRoot);
            if (!(await ExecuteModuleDefinitionValidationSqlAsync(connection, prepared, timeout)).IsHealthy)
                throw new InvalidOperationException("Core maintenance validation remains unhealthy. Do not stage artifacts.");
        }
    }
}

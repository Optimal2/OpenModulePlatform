using System.Text.Json;
using OpenModulePlatform.Installation;
using static OpenModulePlatform.Installation.InstallationEngine;

namespace OpenModulePlatform.Bootstrapper;

internal static partial class Program
{
    internal static async Task<int> RunPlatformCoreMaintenanceAsync(
        CliOptions cli,
        Func<string, bool>? confirm = null,
        Func<SqlBootstrapOptions, string, IReadOnlyList<(string DefinitionPath, ModuleDefinitionDocument Definition)>, Task>? import = null)
    {
        if (cli.ShowHelp)
        {
            WriteUsage();
            return 0;
        }

        var configPath = Path.GetFullPath(cli.ConfigPath);
        var payloadRoot = Path.GetFullPath(cli.PayloadRoot);
        var configJson = await File.ReadAllTextAsync(configPath);
        using var configDocument = JsonDocument.Parse(configJson);
        RejectDuplicateProperties(configDocument.RootElement);
        // No default SQL target is permitted for this maintenance verb.
        var sqlJson = configDocument.RootElement.GetProperty("sql");
        foreach (var name in new[] { "server", "database" })
            if (string.IsNullOrWhiteSpace(sqlJson.GetProperty(name).GetString()))
                throw new InvalidOperationException($"Core maintenance requires explicit sql.{name}.");
        var config = JsonSerializer.Deserialize<BootstrapConfig>(configJson, JsonOptions)
            ?? throw new InvalidOperationException("Core maintenance config is empty.");
        if (!config.Sql.Enabled || config.Sql.CommandTimeoutSeconds <= 0)
            throw new InvalidOperationException("Core maintenance requires enabled SQL and a positive command timeout.");
        _ = BuildConnectionString(config.Sql, config.Sql.Database);

        var definitionsRoot = ResolvePackageModuleDefinitionsRoot(payloadRoot);
        var candidates = new List<(string DefinitionPath, ModuleDefinitionDocument Definition)>();
        // Read every identity so a renamed duplicate or malformed file cannot be silently ignored.
        foreach (var path in Directory.EnumerateFiles(definitionsRoot, "*.json", SearchOption.TopDirectoryOnly))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            RejectDuplicateProperties(document.RootElement);
            var definition = await ReadModuleDefinitionAsync(path);
            if (definition.ModuleKey.Equals("omp_core", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).Equals("omp_core.module-definition.json", StringComparison.OrdinalIgnoreCase))
                candidates.Add((path, definition));
        }
        if (candidates.Count != 1 || Path.GetFileName(candidates[0].DefinitionPath) != "omp_core.module-definition.json")
            throw new InvalidOperationException("Core maintenance requires exactly one canonical omp_core.module-definition.json in data/global/module-definitions.");
        var selected = candidates[0];
        ValidateCoreMaintenancePayload(selected.Definition, cli.ExpectedCoreVersion, config.Sql, payloadRoot);

        Console.WriteLine("OpenModulePlatform platform-core maintenance (existing database only)");
        Console.WriteLine($"Config: {configPath}");
        Console.WriteLine($"SQL target: {config.Sql.Server}/{config.Sql.Database}");
        Console.WriteLine($"Payload: {payloadRoot}");
        Console.WriteLine($"Definition: {selected.DefinitionPath}");
        Console.WriteLine($"Identity: omp_core {selected.Definition.DefinitionVersion}; normalized SHA-256 {selected.Definition.DefinitionSha256}");
        Console.WriteLine("Action: guarded core definition import and compiled platform migration; validate/repair core SQL.");
        if (cli.PlanOnly)
        {
            Console.WriteLine("Offline plan only. Database identity, installed state and SQL permissions have NOT been checked. Do not stage artifacts on this evidence.");
            return 0;
        }
        if (!cli.Yes && !(confirm ?? Confirm)("Apply platform-core maintenance to this SQL target?"))
            return 2;

        if (import is not null)
            await import(config.Sql, payloadRoot, [selected]);
        else
            await ImportSelectedModuleDefinitionsAsync(config.Sql, payloadRoot, [selected],
                onlyNewerOrChanged: true, trustSameVersion: false, requireExistingCore: true);
        Console.WriteLine("Core maintenance completed and verified. The refreshed core identity must remain unchanged before artifact stage.");
        return 0;
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                    throw new InvalidOperationException("Core maintenance refuses ambiguous duplicate JSON properties.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }
}

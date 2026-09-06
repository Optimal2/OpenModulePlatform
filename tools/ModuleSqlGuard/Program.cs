using System.Text.Json;
using OpenModulePlatform.HostAgent.Runtime.Services;
using OpenModulePlatform.ModuleDefinitions;

var results = new List<Diagnostic>();
var checkedScripts = 0;
var paths = new List<string>();
var json = false;
string module = "<unspecified>";
try
{
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--json": json = true; break;
            case "--module": module = RequireValue(args, ref i); break;
            case "--repository":
                var root = Path.GetFullPath(RequireValue(args, ref i));
                using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "omp-components.json"))))
                    foreach (var definition in manifest.RootElement.GetProperty("moduleDefinitions").EnumerateArray())
                        paths.Add(Path.Combine(root, definition.GetProperty("path").GetString()!));
                break;
            default: paths.Add(Path.GetFullPath(args[i])); break;
        }
    }
    if (paths.Count == 0) throw new InvalidOperationException("No module SQL inputs selected.");
    foreach (var path in paths)
    {
        try
        {
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                using var definition = JsonDocument.Parse(File.ReadAllText(path));
                var definitionModule = definition.RootElement.GetProperty("moduleKey").GetString();
                if (string.IsNullOrWhiteSpace(definitionModule)) throw new InvalidOperationException("Missing module key.");
                if (!definition.RootElement.TryGetProperty("sqlScripts", out var scripts)) continue;
                foreach (var script in scripts.EnumerateArray())
                {
                    var key = Text(script, "key") ?? "<unspecified script>";
                    try
                    {
                        Check(ModuleDefinitionSqlOwnership.Decode(Text(script, "inlineSql"), Text(script, "content"), Text(script, "contentEncoding")), path, definitionModule, key);
                    }
                    catch (Exception ex) when (IsExpectedValidationFailure(ex))
                    {
                        results.Add(new(path, definitionModule, key, 1, 1, ModuleDefinitionSqlOwnership.RuleId, "<unresolved table>", ex.Message));
                    }
                }
            }
            else
            {
                // Shared text handling preserves source positions; Analyze normalizes GO.
                var sql = ModuleDefinitionSqlText.RemovePortableDatabaseHeader(File.ReadAllText(path));
                Check(sql, path, module, Path.GetFileName(path));
            }
        }
        catch (Exception ex) when (IsExpectedValidationFailure(ex))
        {
            results.Add(new(path, module, "<unresolved script>", 1, 1, ModuleDefinitionSqlOwnership.RuleId, "<unresolved table>", ex.Message));
        }
    }
}
catch (Exception ex) when (IsExpectedValidationFailure(ex))
{
    results.Add(new("<input>", module, "<unresolved script>", 1, 1, ModuleDefinitionSqlOwnership.RuleId, "<unresolved table>", ex.Message));
}

if (json)
    Console.WriteLine(JsonSerializer.Serialize(new { checkedScripts, diagnostics = results }));
else
{
    foreach (var result in results)
        Console.WriteLine($"{result.File}:{result.Line}:{result.Column}: module '{result.Module}', script '{result.Script}': {result.RuleId}: {result.Table}: {result.Message}");
    Console.WriteLine($"Checked {checkedScripts} script(s); {results.Count} violation(s).");
}
return results.Count == 0 ? 0 : 1;

void Check(string sql, string file, string moduleKey, string scriptKey)
{
    checkedScripts++;
    var ownership = ModuleDefinitionSqlOwnership.Analyze(sql);
    foreach (var violation in ownership)
        results.Add(new(file, moduleKey, scriptKey, violation.Line, violation.Column, ModuleDefinitionSqlOwnership.RuleId, violation.Table, violation.Reason));
    if (ownership.Count == 0 && OmpHostArtifactRepository.ValidateSafeModuleDefinitionSql(sql) is { } error)
        results.Add(new(file, moduleKey, scriptKey, 1, 1, "OMP-MODULE-SQL-GUARD", "<see diagnostic>", error));
}

static string? Text(JsonElement element, string property)
    => element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

// The failures the guard reports as diagnostics instead of crashing: unreadable input,
// malformed JSON or SQL payloads, a manifest or definition missing a required property,
// and bad command-line usage. One predicate for all three catch sites so the set cannot
// drift between them; anything else is a real bug and propagates.
static bool IsExpectedValidationFailure(Exception ex)
    => ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException;

// An option given as the last argument has no value; say so instead of indexing past the array.
static string RequireValue(string[] args, ref int index)
    => index + 1 < args.Length
        ? args[++index]
        : throw new InvalidOperationException($"Option '{args[index]}' requires a value.");

internal sealed record Diagnostic(string File, string Module, string Script, int Line, int Column, string RuleId, string Table, string Message);

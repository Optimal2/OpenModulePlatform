using System.Text.Json;
using System.Text.RegularExpressions;
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
            case "--module": module = args[++i]; break;
            case "--repository":
                var root = Path.GetFullPath(args[++i]);
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
                    catch (Exception ex) when (ex is InvalidOperationException or JsonException)
                    {
                        results.Add(new(path, definitionModule, key, 1, 1, ModuleDefinitionSqlOwnership.RuleId, "<unresolved table>", ex.Message));
                    }
                }
            }
            else
            {
                // Match the portable embedding transform, preserving source line numbers.
                var sql = Regex.Replace(File.ReadAllText(path),
                    @"(?im)^\s*USE\s+\[OpenModulePlatform\]\s*;\s*\r?\n\s*GO\s*(?:--.*)?\s*(?:\r?\n)?",
                    match => new string(match.Value.Select(c => c is '\r' or '\n' ? c : ' ').ToArray()));
                Check(sql, path, module, Path.GetFileName(path));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            results.Add(new(path, module, "<unresolved script>", 1, 1, ModuleDefinitionSqlOwnership.RuleId, "<unresolved table>", ex.Message));
        }
    }
}
catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or IndexOutOfRangeException or KeyNotFoundException)
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

internal sealed record Diagnostic(string File, string Module, string Script, int Line, int Column, string RuleId, string Table, string Message);

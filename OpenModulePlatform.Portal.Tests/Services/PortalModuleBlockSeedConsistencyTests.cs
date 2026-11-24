using System.Text.Json;
using System.Text.RegularExpressions;
using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Keeps the Portal module catalog values identical whether an import writes the
/// module block or runs the initialization seed afterwards.
/// </summary>
public sealed class PortalModuleBlockSeedConsistencyTests
{
    private const RegexOptions SqlRegexOptions = RegexOptions.IgnoreCase
        | RegexOptions.Singleline
        | RegexOptions.CultureInvariant;

    [Fact]
    public void ModuleBlock_MatchesSeedUpdateAndInsert()
    {
        using var definition = JsonDocument.Parse(OmpRepositoryFiles.ReadRepositoryTextFile(
            "OpenModulePlatform.Portal", "omp_portal.module-definition.json"));
        var module = definition.RootElement.GetProperty("module");
        var expected = new ModuleCatalogValues(
            module.GetProperty("displayName").GetString()
                ?? throw new InvalidOperationException("The module block has no displayName."),
            module.GetProperty("moduleType").GetString()
                ?? throw new InvalidOperationException("The module block has no moduleType."),
            module.GetProperty("sortOrder").GetInt32());

        var sql = OmpRepositoryFiles.ReadRepositoryTextFile(
            "OpenModulePlatform.Portal", "sql", "2-initialize-omp-portal.sql");

        AssertSeedValues("UPDATE", expected, ReadUpdateValues(sql));
        AssertSeedValues("INSERT", expected, ReadInsertValues(sql));
    }

    private static ModuleCatalogValues ReadUpdateValues(string sql)
    {
        var update = Regex.Match(
            sql,
            @"\bUPDATE\s+omp\.Modules\s+SET\s+(?<values>.*?)\bWHERE\s+ModuleKey\s*=\s*N'omp_portal'\s*;",
            SqlRegexOptions);
        Assert.True(update.Success, "The seed's omp_portal module UPDATE was not found.");

        var values = update.Groups["values"].Value;
        return new ModuleCatalogValues(
            ReadUpdateString(values, "DisplayName"),
            ReadUpdateString(values, "ModuleType"),
            ReadUpdateInt(values, "SortOrder"));
    }

    private static ModuleCatalogValues ReadInsertValues(string sql)
    {
        const string textValue = @"N'(?<value>(?:''|[^'])*)'";
        var insert = Regex.Match(
            sql,
            @"\bINSERT\s+INTO\s+omp\.Modules\s*\(\s*ModuleKey\s*,\s*DisplayName\s*,\s*ModuleType\s*,\s*SchemaName\s*,\s*Description\s*,\s*IsEnabled\s*,\s*SortOrder\s*\)\s*"
                + @"VALUES\s*\(\s*N'omp_portal'\s*,\s*"
                + textValue.Replace("value", "displayName", StringComparison.Ordinal)
                + @"\s*,\s*"
                + textValue.Replace("value", "moduleType", StringComparison.Ordinal)
                + @"\s*,\s*N'omp_portal'\s*,\s*N'(?:''|[^'])*'\s*,\s*1\s*,\s*(?<sortOrder>\d+)\s*\)",
            SqlRegexOptions);
        Assert.True(insert.Success, "The seed's omp_portal module INSERT was not found in the expected column order.");

        return new ModuleCatalogValues(
            UnescapeSqlString(insert.Groups["displayName"].Value),
            UnescapeSqlString(insert.Groups["moduleType"].Value),
            int.Parse(insert.Groups["sortOrder"].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string ReadUpdateString(string assignments, string column)
    {
        var match = Regex.Match(
            assignments,
            $@"\b{column}\s*=\s*N'(?<value>(?:''|[^'])*)'",
            SqlRegexOptions);
        Assert.True(match.Success, $"The seed UPDATE does not assign {column}.");
        return UnescapeSqlString(match.Groups["value"].Value);
    }

    private static int ReadUpdateInt(string assignments, string column)
    {
        var match = Regex.Match(
            assignments,
            $@"\b{column}\s*=\s*(?<value>\d+)",
            SqlRegexOptions);
        Assert.True(match.Success, $"The seed UPDATE does not assign an integer {column}.");
        return int.Parse(match.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string UnescapeSqlString(string value)
        => value.Replace("''", "'", StringComparison.Ordinal);

    private static void AssertSeedValues(
        string operation,
        ModuleCatalogValues expected,
        ModuleCatalogValues actual)
    {
        AssertSeedValue(operation, "displayName", expected.DisplayName, actual.DisplayName);
        AssertSeedValue(operation, "moduleType", expected.ModuleType, actual.ModuleType);
        AssertSeedValue(operation, "sortOrder", expected.SortOrder, actual.SortOrder);
    }

    private static void AssertSeedValue<T>(string operation, string field, T expected, T actual)
    {
        Assert.True(
            EqualityComparer<T>.Default.Equals(expected, actual),
            $"Seed {operation} {field} drifted from the module block. Module block: '{expected}'; seed {operation}: '{actual}'.");
    }

    private sealed record ModuleCatalogValues(string DisplayName, string ModuleType, int SortOrder);
}

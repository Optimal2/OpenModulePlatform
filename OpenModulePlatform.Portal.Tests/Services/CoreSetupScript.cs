using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Applies the real core setup script (sql/1-setup-openmoduleplatform.sql)
/// batch by batch to a test database, so schema-bound tests stay tied to the
/// shipped schema file instead of a hand-maintained minimal DDL that can drift
/// away from production and silently stop catching schema-level regressions.
/// </summary>
internal static class CoreSetupScript
{
    public static async Task ApplyAsync(string connectionString)
    {
        var setupSql = OmpRepositoryFiles.ReadRepositoryTextFile("sql", "1-setup-openmoduleplatform.sql");

        // Strip the historical local development database switch, the same way
        // scripts/dev/embed-module-definition-sql.ps1 does, so the script runs
        // against the fixture database instead.
        var portableSql = Regex.Replace(
            setupSql,
            @"^\s*USE\s+\[OpenModulePlatform\]\s*;\s*\r?\n\s*GO\s*(?:--.*)?\s*(?:\r?\n)?",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        foreach (var cmd in SplitBatches(portableSql).Select(batch => new SqlCommand(batch, conn)))
        {
            await using (cmd)
            {
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }

    private static IEnumerable<string> SplitBatches(string sql)
    {
        return Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline)
            .Where(batch => !string.IsNullOrWhiteSpace(batch));
    }
}

using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class DashboardPreferencesSqlTests
{
    [Theory]
    [InlineData("1-setup-omp-portal.sql")]
    [InlineData("5-ensure-dashboard-widgets.sql")]
    public void ExpandedCanvasColumnDefault_MatchesTheServiceDefault(string scriptName)
    {
        var sql = OmpRepositoryFiles.ReadRepositoryTextFile(
            "OpenModulePlatform.Portal", "sql", scriptName);

        var declarations = sql.Split('\n')
            .Where(line => line.Contains("expanded_canvas bit NOT NULL", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(2, declarations.Length);
        Assert.All(declarations, line => Assert.Contains(
            "DF_omp_portal_user_dashboard_preferences_expanded_canvas DEFAULT(1)",
            line,
            StringComparison.Ordinal));
    }
}

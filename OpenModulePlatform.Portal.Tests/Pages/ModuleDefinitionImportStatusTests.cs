using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Localization;
using OpenModulePlatform.Portal.Pages.Admin;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;

namespace OpenModulePlatform.Portal.Tests.Pages;

/// <summary>
/// Campaign importvagen-kor-inte-seed-skripten, phase 3, task A: the legacy (non-universal)
/// import status line must render the same DefinitionSqlOutcome sentence the universal
/// package import already renders. Before this change the line said "Executed N SQL repair
/// script(s)." only when N &gt; 0 and stayed completely silent at zero -- the exact silence
/// the campaign closed on the universal path.
/// </summary>
public sealed class ModuleDefinitionImportStatusTests
{
    [Fact]
    public void BuildImportStatus_ZeroExecuted_RendersIncompleteOutcome()
    {
        const string outcome =
            "Definition SQL scripts declared: 2; NONE were executed. "
            + "Script states without a successful execution record: seed_data: Not recorded. "
            + "Treat this import as incomplete until the runnable scripts have run.";
        var result = CreateResult(sqlRepairCount: 0, definitionSqlOutcome: outcome);

        var status = CreateModel().BuildImportStatus(result);

        Assert.Contains(outcome, status, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildImportStatus_ZeroExecuted_RendersDeliberateSkipReason()
    {
        const string outcome =
            "Definition SQL scripts declared: 2; none executed: SQL repairs were disabled for this import.";
        var result = CreateResult(sqlRepairCount: 0, definitionSqlOutcome: outcome);

        var status = CreateModel().BuildImportStatus(result);

        Assert.Contains(outcome, status, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildImportStatus_Executed_RendersOutcomeInsteadOfCountOnlyLine()
    {
        const string outcome = "Definition SQL scripts executed: 3.";
        var result = CreateResult(sqlRepairCount: 3, definitionSqlOutcome: outcome);

        var status = CreateModel().BuildImportStatus(result);

        Assert.Contains(outcome, status, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildImportStatus_NullOutcome_KeepsLegacyCountLine()
    {
        // Null outcome means the definition declares no executable scripts (or the SQL
        // failure text already carries the outcome). The pre-existing count line is the
        // fallback so the legacy text never regresses for that case.
        var result = CreateResult(sqlRepairCount: 2, definitionSqlOutcome: null);

        var status = CreateModel().BuildImportStatus(result);

        Assert.Contains("Executed 2 SQL repair script(s).", status, StringComparison.Ordinal);
    }

    private static PortableModulePackageImportResult CreateResult(int sqlRepairCount, string? definitionSqlOutcome)
        => new(
            "omp_auth",
            "0.3.152",
            ModuleDefinitionDocumentId: 1,
            Applied: true,
            sqlRepairCount,
            Artifacts: [])
        {
            DefinitionSqlOutcome = definitionSqlOutcome
        };

    private static ModuleDefinitionsModel CreateModel()
    {
        var services = new ServiceCollection()
            .AddSingleton<IStringLocalizer<PortalResource>>(new PassThroughStringLocalizer())
            .BuildServiceProvider();
        return new ModuleDefinitionsModel(
            Microsoft.Extensions.Options.Options.Create(new WebAppOptions()),
            rbac: null!,
            repo: null!,
            packages: null!)
        {
            PageContext = new Microsoft.AspNetCore.Mvc.RazorPages.PageContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = services }
            }
        };
    }

    private sealed class PassThroughStringLocalizer : IStringLocalizer<PortalResource>
    {
        public LocalizedString this[string name]
            => new(name, name);

        public LocalizedString this[string name, params object[] arguments]
            => new(name, string.Format(CultureInfo.InvariantCulture, name, arguments));

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
            => [];
    }
}

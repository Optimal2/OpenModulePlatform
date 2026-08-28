using System.Globalization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Localization;
using OpenModulePlatform.Portal.Pages.Admin;
using OpenModulePlatform.Web.Shared.Options;

namespace OpenModulePlatform.Portal.Tests.Pages;

/// <summary>
/// Campaign importvagen-kor-inte-seed-skripten, phase 3: locks the suffix behavior of
/// <see cref="ModulePackageImportModel.FormatUniversalImportMessage"/> that phase 2
/// introduced. The base sentence localizes; a suffix starting with a KNOWN service-built
/// stem ("Definition SQL", "Schema drift healed", "Schema repair INCOMPLETE") passes
/// through verbatim; any other tail must fall back to the whole-message P() lookup
/// (fail-closed, the pre-phase-2 behavior) instead of being silently dropped.
/// </summary>
public sealed class ModulePackageImportMessageFormatTests
{
    private const string BaseMessage =
        "Module omp_auth 0.3.152; artifacts imported or replaced: 3; failed artifacts: 0.";

    [Fact]
    public void Format_WithoutSuffix_LocalizesBaseSentenceOnly()
    {
        var model = CreateModel();

        var result = model.FormatUniversalImportMessage(BaseMessage);

        // The marking localizer proves the base sentence went through the P() lookup.
        Assert.Equal("[loc] " + BaseMessage, result);
    }

    [Fact]
    public void Format_WithDefinitionSqlSuffix_PassesSuffixThroughVerbatim()
    {
        var model = CreateModel();
        const string suffix =
            " Definition SQL scripts declared: 2; NONE were executed. "
            + "Script states without a successful execution record: seed_data: Not recorded. "
            + "Treat this import as incomplete until the runnable scripts have run.";

        var result = model.FormatUniversalImportMessage(BaseMessage + suffix);

        // Base localized, suffix appended raw -- it is service-built and has no resx entry.
        Assert.Equal("[loc] " + BaseMessage + suffix, result);
    }

    [Fact]
    public void Format_WithSchemaDriftSuffix_PassesSuffixThroughVerbatim()
    {
        var model = CreateModel();
        const string suffix =
            " Schema drift healed: re-executed 1 script(s) for module 'omp_auth'. "
            + "Objects that were missing and are now present: omp_auth.Sessions.";

        var result = model.FormatUniversalImportMessage(BaseMessage + suffix);

        Assert.Equal("[loc] " + BaseMessage + suffix, result);
    }

    [Fact]
    public void Format_WithUnknownSuffixStart_FallsBackToWholeMessageLookup()
    {
        var model = CreateModel();
        const string message = BaseMessage + " Some future tail the parser does not know.";

        var result = model.FormatUniversalImportMessage(message);

        // Fail-closed: the tail is NOT silently dropped. The regex declines the match and
        // the ENTIRE message goes through the whole-string P() lookup, exactly as every
        // unrecognized message did before phase 2. The marking prefix on the full string
        // proves no partial reformatting happened.
        Assert.Equal("[loc] " + message, result);
    }

    private static ModulePackageImportModel CreateModel()
        => new(
            Microsoft.Extensions.Options.Options.Create(new WebAppOptions()),
            rbac: null!,
            repo: null!,
            packages: null!,
            widgets: null!,
            configObjects: null!,
            deploymentLocks: null!,
            cache: null!,
            portalLocalizer: new MarkingStringLocalizer());

    /// <summary>
    /// Pass-through localizer that prefixes every lookup, so a test can tell a string
    /// that went through P() apart from one that was appended verbatim.
    /// </summary>
    private sealed class MarkingStringLocalizer : IStringLocalizer<PortalResource>
    {
        public LocalizedString this[string name]
            => new(name, "[loc] " + name);

        public LocalizedString this[string name, params object[] arguments]
            => new(name, "[loc] " + string.Format(CultureInfo.InvariantCulture, name, arguments));

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
            => [];
    }
}

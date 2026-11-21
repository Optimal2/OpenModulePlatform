using System.Text.Json;
using System.Text.RegularExpressions;
using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// The iFrame validation script must require exactly the iframe URL rows the module definition
/// declares as required. The example targets (ids 2 and 3) are excludedSeedData, yet the
/// validation script used to require them as well, so an installation without the examples
/// failed its health check.
/// </summary>
public sealed class IframeValidationRequiredUrlsTests
{
    [Fact]
    public void ValidationScript_RequiresOnlyTheDeclaredUrlIds()
    {
        using var definition = JsonDocument.Parse(OmpRepositoryFiles.ReadRepositoryTextFile(
            "OpenModulePlatform.Web.iFrameWebAppModule", "iframe_webapp.module-definition.json"));
        var declared = definition.RootElement
            .GetProperty("integrity")
            .GetProperty("requiredModuleRows")
            .GetProperty("urls")
            .EnumerateArray()
            .Select(static url => url.GetProperty("id").GetInt32())
            .Order()
            .ToArray();

        var validationSql = OmpRepositoryFiles.ReadRepositoryTextFile(
            "OpenModulePlatform.Web.iFrameWebAppModule", "Sql", "0-validate-iframe-webapp.sql");
        var required = Regex.Matches(validationSql, @"FROM\s+omp_iframe\.urls\s+WHERE\s+id\s*=\s*(\d+)", RegexOptions.IgnoreCase)
            .Select(static match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Order()
            .ToArray();

        Assert.NotEmpty(declared);
        Assert.Equal(declared, required);
    }
}

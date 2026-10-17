using Microsoft.SqlServer.TransactSql.ScriptDom;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Services;
using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class ConfigOverlaySchemaContractTests
{
    [Fact]
    public void RuntimeUpgrade_EnforcesTheSetupScopeCheck()
    {
        // No SQL connection: inspect the exact batch used by both Ensure overloads.
        var sql = OmpHostArtifactRepository.ConfigOverlayHostScopeUpgradeSql.Replace("''", "'");
        Assert.Contains("WITH CHECK", sql, StringComparison.Ordinal);
        Assert.Contains("CK_omp_ConfigOverlayDocuments_HostScope", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK(HostScope IN (N'Host', N'AllHosts'))", sql, StringComparison.Ordinal);
        Assert.Contains("sys.check_constraints", sql, StringComparison.Ordinal);
        Assert.Contains("WITH CHECK CHECK CONSTRAINT", sql, StringComparison.Ordinal);
        var setup = OmpRepositoryFiles.ReadRepositoryTextFile("sql", "1-setup-openmoduleplatform.sql");
        Assert.Contains("CHECK(HostScope IN (N'Host', N'AllHosts'))", setup, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeUpgrade_IsAValidSqlBatch()
    {
        using var reader = new StringReader(OmpHostArtifactRepository.ConfigOverlayHostScopeUpgradeSql);
        new TSql160Parser(initialQuotedIdentifiers: true).Parse(reader, out var errors);
        Assert.Empty(errors);
    }

    [Fact]
    public void OldSchemaFixture_AllHostsSaveFailsWithUpgradeGuidance()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ConfigOverlayHostScopeNames.RequireWritableSchema(ConfigOverlayHostScope.AllHosts, hasHostScopeColumn: false));
        Assert.Contains("omp.ConfigOverlayDocuments.HostScope", exception.Message, StringComparison.Ordinal);
        Assert.Contains("platform schema upgrade", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ConfigOverlayHostScope.Host, false)]
    [InlineData(ConfigOverlayHostScope.Host, true)]
    [InlineData(ConfigOverlayHostScope.AllHosts, true)]
    public void SupportedSchemaFixtures_CanSave(ConfigOverlayHostScope scope, bool hasHostScopeColumn)
        => ConfigOverlayHostScopeNames.RequireWritableSchema(scope, hasHostScopeColumn);

    [Theory]
    [InlineData("OpenModulePlatform.Portal", "OmpAdminRepository.ConfigOverlays.cs")]
    [InlineData("OpenModulePlatform.HostAgent.Runtime", "OmpHostArtifactRepository.cs")]
    public void BothSavePaths_GuardOldSchemaBeforeIdenticalLookupOrWrites(string project, string file)
    {
        // Wiring guard complements the executable schema fixtures without
        // connecting to or altering any database.
        var source = OmpRepositoryFiles.ReadRepositoryTextFile(project, "Services", file);
        var save = source[source.IndexOf("SaveImportedConfigOverlayAsync(", StringComparison.Ordinal)..];
        var guard = save.IndexOf("ConfigOverlayHostScopeNames.RequireWritableSchema(input.HostScope, hasHostScopeColumn);", StringComparison.Ordinal);
        Assert.True(guard > save.IndexOf("var hasHostScopeColumn = await ColumnExistsAsync", StringComparison.Ordinal));
        Assert.True(guard < save.IndexOf("await using (var find", StringComparison.Ordinal));
        Assert.True(guard < save.IndexOf("if (isIdentical)", StringComparison.Ordinal));
        Assert.True(guard < save.IndexOf("await DisableOtherEnabledConfigOverlayDocumentsAsync", StringComparison.Ordinal));
        Assert.True(guard < save.IndexOf("await InsertConfigOverlayDocumentAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void OldSchemaResolution_UsesExactHostFallbackWithoutReferencingMissingColumn()
    {
        var sql = OmpHostArtifactRepository.BuildConfigurationFilesSql(hasMergeModeColumn: false, hasHostScopeColumn: false);
        Assert.DoesNotContain("overlay.HostScope", sql, StringComparison.Ordinal);
        Assert.Contains("overlay.HostKey = @hostKey", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("N'AllHosts'", sql, StringComparison.Ordinal);
    }
}

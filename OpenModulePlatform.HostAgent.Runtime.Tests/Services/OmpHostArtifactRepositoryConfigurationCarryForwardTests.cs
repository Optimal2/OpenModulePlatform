using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// Proves the operator-edit preservation rules for artifact configuration files:
/// package imports store a pristine baseline in PackageFileContent, replacing
/// files on the same artifact keeps operator edits while the package file is
/// unchanged, and importing a new artifact version carries operator-edited
/// content forward from the previous version using the three-way rule
/// (previous baseline vs previous content vs new package content).
/// </summary>
public sealed class OmpHostArtifactRepositoryConfigurationCarryForwardTests : IDisposable
{
    private const int PreviousArtifactId = 100;
    private const int NewArtifactId = 200;
    private const string ConfigPath = "odv.site.config.js";
    private const string PackagedContent = "window.odv = { theme: 'default' };";
    private const string OperatorEditedContent = "window.odv = { theme: 'customer-blue' };";
    private const string ChangedPackagedContent = "window.odv = { theme: 'default', v2: true };";

    private readonly OmpHostArtifactRepositoryTestDatabase _database;
    private readonly OmpHostArtifactRepository _repository;

    public OmpHostArtifactRepositoryConfigurationCarryForwardTests()
    {
        _database = new OmpHostArtifactRepositoryTestDatabase();
        try
        {
            _database.CreateConfigurationFileResolutionTables();
            _database.InsertArtifactWithApp(PreviousArtifactId, "web-app", "2.4.59", "test-module", "test-app");
            _repository = new OmpHostArtifactRepository(_database.CreateFactory());
        }
        catch
        {
            // A throwing constructor means xUnit never calls Dispose(); dispose the
            // fixture here or its database leaks on every failing run.
            _database.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task ReplaceConfigurationFiles_StoresPackageBaseline()
    {
        await ReplaceAsync(PreviousArtifactId, (ConfigPath, PackagedContent));

        var row = Assert.Single(_database.GetArtifactConfigurationFiles(PreviousArtifactId));
        Assert.Equal(PackagedContent, row.FileContent);
        Assert.Equal(PackagedContent, row.PackageFileContent);
        Assert.True(row.IsEnabled);
    }

    [Fact]
    public async Task ReplaceConfigurationFiles_SameArtifactReimport_KeepsOperatorEditWhenPackageFileUnchanged()
    {
        await ReplaceAsync(PreviousArtifactId, (ConfigPath, PackagedContent));
        _database.SetArtifactConfigurationFileContent(PreviousArtifactId, ConfigPath, OperatorEditedContent);

        await ReplaceAsync(PreviousArtifactId, (ConfigPath, PackagedContent));

        var row = Assert.Single(_database.GetArtifactConfigurationFiles(PreviousArtifactId));
        Assert.Equal(OperatorEditedContent, row.FileContent);
        Assert.Equal(PackagedContent, row.PackageFileContent);
    }

    [Fact]
    public async Task ReplaceConfigurationFiles_SameArtifactReimport_PackageChangeWinsOverOperatorEdit()
    {
        await ReplaceAsync(PreviousArtifactId, (ConfigPath, PackagedContent));
        _database.SetArtifactConfigurationFileContent(PreviousArtifactId, ConfigPath, OperatorEditedContent);

        await ReplaceAsync(PreviousArtifactId, (ConfigPath, ChangedPackagedContent));

        var row = Assert.Single(_database.GetArtifactConfigurationFiles(PreviousArtifactId));
        Assert.Equal(ChangedPackagedContent, row.FileContent);
        Assert.Equal(ChangedPackagedContent, row.PackageFileContent);
    }

    [Fact]
    public async Task ReplaceConfigurationFiles_DeletesRowsMissingFromPackage()
    {
        await ReplaceAsync(
            PreviousArtifactId,
            (ConfigPath, PackagedContent),
            ("legacy.settings.json", "{ \"old\": true }"));

        await ReplaceAsync(PreviousArtifactId, (ConfigPath, PackagedContent));

        var row = Assert.Single(_database.GetArtifactConfigurationFiles(PreviousArtifactId));
        Assert.Equal(ConfigPath, row.RelativePath);
    }

    [Fact]
    public async Task CarryForward_PreservesOperatorEditWhenPackageFileIsUnchanged()
    {
        await ReplaceAsync(PreviousArtifactId, (ConfigPath, PackagedContent));
        _database.SetArtifactConfigurationFileContent(PreviousArtifactId, ConfigPath, OperatorEditedContent);
        InsertNewArtifact();
        await ReplaceAsync(NewArtifactId, (ConfigPath, PackagedContent));

        var result = await _repository.CarryForwardArtifactConfigurationFilesAsync(NewArtifactId, CancellationToken.None);

        Assert.Equal("2.4.59", result.SourceVersion);
        var item = Assert.Single(result.Items);
        Assert.Equal(ConfigPath, item.RelativePath);
        Assert.Equal(ArtifactConfigurationCarryForwardOutcome.Preserved, item.Outcome);

        var row = Assert.Single(_database.GetArtifactConfigurationFiles(NewArtifactId));
        Assert.Equal(OperatorEditedContent, row.FileContent);
        Assert.Equal(PackagedContent, row.PackageFileContent);
    }

    [Fact]
    public async Task CarryForward_PreservesOperatorDisabledState()
    {
        await ReplaceAsync(PreviousArtifactId, (ConfigPath, PackagedContent));
        _database.SetArtifactConfigurationFileContent(PreviousArtifactId, ConfigPath, PackagedContent, isEnabled: false);
        InsertNewArtifact();
        await ReplaceAsync(NewArtifactId, (ConfigPath, PackagedContent));

        var result = await _repository.CarryForwardArtifactConfigurationFilesAsync(NewArtifactId, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(ArtifactConfigurationCarryForwardOutcome.Preserved, item.Outcome);
        var row = Assert.Single(_database.GetArtifactConfigurationFiles(NewArtifactId));
        Assert.False(row.IsEnabled);
    }

    [Fact]
    public async Task CarryForward_ReportsConflictWhenPackageFileChangedOverOperatorEdit()
    {
        await ReplaceAsync(PreviousArtifactId, (ConfigPath, PackagedContent));
        _database.SetArtifactConfigurationFileContent(PreviousArtifactId, ConfigPath, OperatorEditedContent);
        InsertNewArtifact();
        await ReplaceAsync(NewArtifactId, (ConfigPath, ChangedPackagedContent));

        var result = await _repository.CarryForwardArtifactConfigurationFilesAsync(NewArtifactId, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(ArtifactConfigurationCarryForwardOutcome.Conflict, item.Outcome);

        // The package file wins; the operator must merge manually.
        var row = Assert.Single(_database.GetArtifactConfigurationFiles(NewArtifactId));
        Assert.Equal(ChangedPackagedContent, row.FileContent);
    }

    /// <summary>
    /// A previous row with NO package baseline is carried forward when the target row is
    /// still pristine package content.
    /// </summary>
    /// <remarks>
    /// This used to report Conflict and let the package default win. Measured in a
    /// customer test environment 2026-08-23: a configured OmpAuth:Oidc block sat on the
    /// previous artifact while every newer version carried the package default, because
    /// the operator's row had no baseline and could therefore never be Preserved. Worse,
    /// once one version held that default it became the carry-forward source for the
    /// next, so the loss compounded and never healed on its own.
    ///
    /// Carrying it forward cannot overwrite anything: the target is byte-for-byte what
    /// its package delivered and is enabled. A target that already carries an operator
    /// edit is a different case and stays Conflict -- see
    /// <see cref="CarryForward_LegacyRowWithoutBaseline_DoesNotOverwriteAnEditedTarget"/>.
    /// </remarks>
    [Fact]
    public async Task CarryForward_LegacyRowWithoutBaseline_IsPreservedOverPackageDefault()
    {
        await ReplaceAsync(PreviousArtifactId, (ConfigPath, OperatorEditedContent));
        _database.ClearArtifactConfigurationFileBaseline(PreviousArtifactId, ConfigPath);
        InsertNewArtifact();
        await ReplaceAsync(NewArtifactId, (ConfigPath, PackagedContent));

        var result = await _repository.CarryForwardArtifactConfigurationFilesAsync(NewArtifactId, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(ArtifactConfigurationCarryForwardOutcome.PreservedWithoutBaseline, item.Outcome);
        var row = Assert.Single(_database.GetArtifactConfigurationFiles(NewArtifactId));
        Assert.Equal(OperatorEditedContent, row.FileContent);
    }

    /// <summary>
    /// The safety half of the rule above: a baseline-less source must NOT overwrite a
    /// target the operator has already edited.
    /// </summary>
    /// <remarks>
    /// Also pins what the report does here, which is nothing. An independent review
    /// (qwen, 2026-08-23) proved empirically that this pairing produces an EMPTY item
    /// list: the WHERE clause filters it out before classification, so ELSE N'Conflict'
    /// is unreachable for a baseline-less source. The earlier version of this test only
    /// asserted the absence of Preserved and would have passed just as happily if the
    /// row had been reported as Conflict - which is what three comments in the code
    /// claimed was happening. Asserting the emptiness makes the silence deliberate and
    /// visible: if someone later starts reporting this case, this test says so.
    /// </remarks>
    [Fact]
    public async Task CarryForward_LegacyRowWithoutBaseline_DoesNotOverwriteAnEditedTarget()
    {
        await ReplaceAsync(PreviousArtifactId, (ConfigPath, OperatorEditedContent));
        _database.ClearArtifactConfigurationFileBaseline(PreviousArtifactId, ConfigPath);
        InsertNewArtifact();
        await ReplaceAsync(NewArtifactId, (ConfigPath, PackagedContent));
        // The operator edits the NEW version after it was imported.
        _database.SetArtifactConfigurationFileContent(NewArtifactId, ConfigPath, ChangedPackagedContent);

        var result = await _repository.CarryForwardArtifactConfigurationFilesAsync(NewArtifactId, CancellationToken.None);

        var row = Assert.Single(_database.GetArtifactConfigurationFiles(NewArtifactId));
        Assert.Equal(ChangedPackagedContent, row.FileContent);

        // Nothing is carried forward AND nothing is reported - the pairing never
        // reaches classification. Not Conflict, not anything.
        Assert.Empty(result.Items);
        Assert.Null(result.BuildImportMessage());
    }

    /// <summary>
    /// The compounding case, end to end: version A holds the operator's content, B is
    /// imported and must inherit it, and C imported after B must still have it.
    /// </summary>
    /// <remarks>
    /// This is the shape of the real failure. With the old rule B silently took the
    /// package default and then became the source for C, so the operator's content was
    /// unreachable from that point on -- exactly what "it disappeared again" looked like.
    /// </remarks>
    [Fact]
    public async Task CarryForward_LegacyRowWithoutBaseline_SurvivesTwoConsecutiveImports()
    {
        const int ThirdArtifactId = 300;

        // Version A: the operator's content, with no package baseline.
        await ReplaceAsync(PreviousArtifactId, (ConfigPath, OperatorEditedContent));
        _database.ClearArtifactConfigurationFileBaseline(PreviousArtifactId, ConfigPath);

        // Version B is imported and must inherit it.
        InsertNewArtifact();
        await ReplaceAsync(NewArtifactId, (ConfigPath, PackagedContent));
        await _repository.CarryForwardArtifactConfigurationFilesAsync(NewArtifactId, CancellationToken.None);

        var afterB = Assert.Single(_database.GetArtifactConfigurationFiles(NewArtifactId));
        Assert.Equal(OperatorEditedContent, afterB.FileContent);

        // Version C is imported after B. B is now the newest source, so if B had taken
        // the package default the operator's content would be unreachable from here on.
        // That is exactly how the real installation lost its OmpAuth:Oidc block.
        _database.InsertArtifact(ThirdArtifactId, "web-app", "2.4.62", DateTime.UtcNow.AddMinutes(10));
        await ReplaceAsync(ThirdArtifactId, (ConfigPath, PackagedContent));
        await _repository.CarryForwardArtifactConfigurationFilesAsync(ThirdArtifactId, CancellationToken.None);

        var afterC = Assert.Single(_database.GetArtifactConfigurationFiles(ThirdArtifactId));
        Assert.Equal(OperatorEditedContent, afterC.FileContent);
    }

    /// <summary>
    /// The cost of carrying baseline-less rows forward: a deliberate package change to a
    /// row nobody ever edited does NOT reach the new version.
    /// </summary>
    /// <remarks>
    /// Raised by an independent review (glm, 2026-08-23). The PackageFileContent column
    /// was added 2026-08-12, so every configuration row older than that carries NULL
    /// without a human having touched it - plain old package defaults. They are
    /// indistinguishable from operator-created rows, so this is a real trade, not a bug
    /// to fix later: losing a customer's OmpAuth:Oidc block is worse than a package
    /// default arriving one version late, and the latter is visible in the import report
    /// while the former was silent.
    ///
    /// What this test locks is that the trade stays HONEST: the row is carried forward,
    /// it is reported under its own outcome rather than claimed as an operator edit, and
    /// the message tells the operator the package change did not take effect.
    /// </remarks>
    [Fact]
    public async Task CarryForward_NeverEditedLegacyRow_BlocksPackageChangeButSaysSo()
    {
        // A legacy row: plain package content, baseline stripped the way the 2026-08-12
        // migration left every pre-existing row.
        await ReplaceAsync(PreviousArtifactId, (ConfigPath, PackagedContent));
        _database.ClearArtifactConfigurationFileBaseline(PreviousArtifactId, ConfigPath);

        // The new package deliberately changes the file.
        InsertNewArtifact();
        await ReplaceAsync(NewArtifactId, (ConfigPath, ChangedPackagedContent));

        var result = await _repository.CarryForwardArtifactConfigurationFilesAsync(NewArtifactId, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(ArtifactConfigurationCarryForwardOutcome.PreservedWithoutBaseline, item.Outcome);

        // The package change did not reach the new version - that is the cost.
        var row = Assert.Single(_database.GetArtifactConfigurationFiles(NewArtifactId));
        Assert.Equal(PackagedContent, row.FileContent);

        // It must never be reported as an operator edit, and the operator must be told
        // the package change did not take effect.
        var message = result.BuildImportMessage();
        Assert.NotNull(message);
        Assert.Contains("no package baseline", message);
        Assert.Contains("did NOT take effect", message);
        Assert.DoesNotContain($"Preserved 1 operator-edited", message);
    }

    [Fact]
    public async Task CarryForward_UneditedPreviousVersion_NormalPackageChangeIsSilent()
    {
        await ReplaceAsync(PreviousArtifactId, (ConfigPath, PackagedContent));
        InsertNewArtifact();
        await ReplaceAsync(NewArtifactId, (ConfigPath, ChangedPackagedContent));

        var result = await _repository.CarryForwardArtifactConfigurationFilesAsync(NewArtifactId, CancellationToken.None);

        Assert.Empty(result.Items);
        var row = Assert.Single(_database.GetArtifactConfigurationFiles(NewArtifactId));
        Assert.Equal(ChangedPackagedContent, row.FileContent);
    }

    [Fact]
    public async Task CarryForward_ReportsOperatorEditedFileMissingFromNewPackage()
    {
        await ReplaceAsync(
            PreviousArtifactId,
            (ConfigPath, PackagedContent),
            ("extra.config.json", "{ \"packaged\": true }"));
        _database.SetArtifactConfigurationFileContent(PreviousArtifactId, "extra.config.json", "{ \"edited\": true }");
        InsertNewArtifact();
        await ReplaceAsync(NewArtifactId, (ConfigPath, PackagedContent));

        var result = await _repository.CarryForwardArtifactConfigurationFilesAsync(NewArtifactId, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("extra.config.json", item.RelativePath);
        Assert.Equal(ArtifactConfigurationCarryForwardOutcome.MissingInPackage, item.Outcome);
    }

    [Fact]
    public async Task CarryForward_WithoutPreviousConfigurationRows_ReturnsEmpty()
    {
        // The previous artifact exists but has no configuration file rows.
        InsertNewArtifact();
        await ReplaceAsync(NewArtifactId, (ConfigPath, PackagedContent));

        var result = await _repository.CarryForwardArtifactConfigurationFilesAsync(NewArtifactId, CancellationToken.None);

        Assert.Null(result.SourceVersion);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task CarryForward_DoesNotOverwriteOperatorEditedTargetRow()
    {
        await ReplaceAsync(PreviousArtifactId, (ConfigPath, PackagedContent));
        _database.SetArtifactConfigurationFileContent(PreviousArtifactId, ConfigPath, OperatorEditedContent);
        InsertNewArtifact();
        await ReplaceAsync(NewArtifactId, (ConfigPath, PackagedContent));
        const string newerOperatorContent = "window.odv = { theme: 'customer-green' };";
        _database.SetArtifactConfigurationFileContent(NewArtifactId, ConfigPath, newerOperatorContent);

        // A repeated registration pass (for example a Bootstrapper refresh) must
        // not pull the older version's edit over the newer operator state.
        var result = await _repository.CarryForwardArtifactConfigurationFilesAsync(NewArtifactId, CancellationToken.None);

        Assert.Empty(result.Items);
        var row = Assert.Single(_database.GetArtifactConfigurationFiles(NewArtifactId));
        Assert.Equal(newerOperatorContent, row.FileContent);
    }

    private void InsertNewArtifact()
        => _database.InsertArtifact(NewArtifactId, "web-app", "2.4.61", DateTime.UtcNow.AddMinutes(5));

    private Task<int> ReplaceAsync(int artifactId, params (string RelativePath, string FileContent)[] files)
        => _repository.ReplaceArtifactConfigurationFilesAsync(
            artifactId,
            files.Select(static file => new ArtifactPackageConfigurationFile(file.RelativePath, file.FileContent)).ToList(),
            CancellationToken.None);
}

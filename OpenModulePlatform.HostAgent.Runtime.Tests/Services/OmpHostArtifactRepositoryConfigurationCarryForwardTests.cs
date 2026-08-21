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

    [Fact]
    public async Task CarryForward_LegacyRowWithoutBaseline_ReportsConflictInsteadOfSilentLoss()
    {
        await ReplaceAsync(PreviousArtifactId, (ConfigPath, OperatorEditedContent));
        _database.ClearArtifactConfigurationFileBaseline(PreviousArtifactId, ConfigPath);
        InsertNewArtifact();
        await ReplaceAsync(NewArtifactId, (ConfigPath, PackagedContent));

        var result = await _repository.CarryForwardArtifactConfigurationFilesAsync(NewArtifactId, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(ArtifactConfigurationCarryForwardOutcome.Conflict, item.Outcome);
        var row = Assert.Single(_database.GetArtifactConfigurationFiles(NewArtifactId));
        Assert.Equal(PackagedContent, row.FileContent);
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

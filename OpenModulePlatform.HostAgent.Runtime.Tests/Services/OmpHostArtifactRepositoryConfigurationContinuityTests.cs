using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// Pins the configuration-continuity rules for artifact configuration rows:
///
/// 1. Source selection. Copy and carry-forward must take configuration from the
///    artifact the slot's pointers actually referenced before the import
///    (app instance / template row / last successful deployment state), not from
///    whichever enabled artifact happened to be created most recently. Without
///    pointers, the fallback searches all previous versions for rows carrying an
///    operator delta and takes the newest per relative path.
/// 2. Pointer move. Moving an app-instance pointer to an artifact that lacks a
///    configuration row the source artifact has (or that holds a pristine package
///    baseline where the source carries an operator edit) must carry the row over
///    before the move.
/// 3. Retention. An artifact whose configuration rows carry operator deltas may
///    only be deleted when the same relative path with byte-identical content
///    survives on a newer, preserved version in the same slot.
/// </summary>
public sealed class OmpHostArtifactRepositoryConfigurationContinuityTests : IDisposable
{
    private const string AppSettingsPath = "appsettings.json";
    private const string PackagedV1 = "{ \"OmpAuth\": { \"CookieName\": \".omp\" } }";
    private const string OperatorEdited = "{ \"OmpAuth\": { \"CookieName\": \".omp\", \"Oidc\": { \"Authority\": \"https://login.example\" } } }";
    private const string PackagedV2 = "{ \"OmpAuth\": { \"CookieName\": \".omp\" }, \"Feature\": \"v2\" }";

    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly OmpHostArtifactRepositoryTestDatabase _database;
    private readonly OmpHostArtifactRepository _repository;

    public OmpHostArtifactRepositoryConfigurationContinuityTests()
    {
        _database = new OmpHostArtifactRepositoryTestDatabase();
        try
        {
            _database.CreateConfigurationFileResolutionTables();
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

    /// <summary>
    /// The measured production shape: the app instance points at version 2.0 whose
    /// appsettings.json carries an operator edit, an older version 1.5 was
    /// re-imported later (later CreatedUtc) with pristine rows, and version 2.1 is
    /// registered without configuration files. The copy must come from the
    /// pointed-to artifact, not from the most recently created one.
    /// </summary>
    [Fact]
    public async Task CopyConfigurationFiles_UsesArtifactPointedToByAppInstance_NotNewestCreated()
    {
        _database.InsertArtifactWithApp(100, "web-app", "2.0.0", "test-module", "test-app", "web", T0);
        _database.InsertArtifact(101, "web-app", "1.5.0", T2, "web");
        _database.InsertArtifact(200, "web-app", "2.1.0", T1, "web");
        _database.InsertArtifactConfigurationFile(100, AppSettingsPath, OperatorEdited, PackagedV1);
        _database.InsertArtifactConfigurationFile(101, AppSettingsPath, PackagedV1, PackagedV1);
        _database.InsertAppInstanceForApp(appId: 1, artifactId: 100);

        var copyResult = await _repository.CopyConfigurationFilesFromContinuitySourceAsync(
            200, CancellationToken.None);

        Assert.Equal(1, copyResult.CopiedCount);
        Assert.Equal(100, copyResult.SourceArtifactId);
        Assert.Equal("2.0.0", copyResult.SourceVersion);
        var row = Assert.Single(_database.GetArtifactConfigurationFiles(200));
        Assert.Equal(OperatorEdited, row.FileContent);
    }

    /// <summary>
    /// Without any pointer the copy falls back to the newest operator delta per
    /// relative path across ALL previous versions: version 1.0 carries the edit,
    /// version 2.0 is pristine, so 2.0 must not win just by being newer.
    /// </summary>
    [Fact]
    public async Task CopyConfigurationFiles_WithoutPointer_TakesNewestOperatorDeltaPerPath()
    {
        _database.InsertArtifactWithApp(100, "web-app", "1.0.0", "test-module", "test-app", "web", T0);
        _database.InsertArtifact(101, "web-app", "2.0.0", T1, "web");
        _database.InsertArtifact(200, "web-app", "3.0.0", T2, "web");
        _database.InsertArtifactConfigurationFile(100, AppSettingsPath, OperatorEdited, PackagedV1);
        _database.InsertArtifactConfigurationFile(101, AppSettingsPath, PackagedV2, PackagedV2);

        var copyResult = await _repository.CopyConfigurationFilesFromContinuitySourceAsync(
            200, CancellationToken.None);

        Assert.Equal(1, copyResult.CopiedCount);
        Assert.Null(copyResult.SourceArtifactId);
        Assert.Equal("1.0.0", copyResult.SourceVersion);
        var row = Assert.Single(_database.GetArtifactConfigurationFiles(200));
        Assert.Equal(OperatorEdited, row.FileContent);
    }

    /// <summary>
    /// Carry-forward follows the same source rule as the copy path: the
    /// pointed-to artifact wins over a more recently created sibling.
    /// </summary>
    [Fact]
    public async Task CarryForward_UsesArtifactPointedToByAppInstance_NotNewestCreated()
    {
        const string otherEdit = "{ \"OmpAuth\": { \"CookieName\": \".other\" } }";
        _database.InsertArtifactWithApp(100, "web-app", "2.0.0", "test-module", "test-app", "web", T0);
        _database.InsertArtifact(101, "web-app", "1.5.0", T2, "web");
        _database.InsertArtifact(200, "web-app", "2.1.0", T1, "web");
        _database.InsertArtifactConfigurationFile(100, AppSettingsPath, OperatorEdited, PackagedV1);
        _database.InsertArtifactConfigurationFile(101, AppSettingsPath, otherEdit, PackagedV1);
        _database.InsertArtifactConfigurationFile(200, AppSettingsPath, PackagedV1, PackagedV1);
        _database.InsertAppInstanceForApp(appId: 1, artifactId: 100);

        var result = await _repository.CarryForwardArtifactConfigurationFilesAsync(200, CancellationToken.None);

        Assert.Equal("2.0.0", result.SourceVersion);
        var row = Assert.Single(_database.GetArtifactConfigurationFiles(200));
        Assert.Equal(OperatorEdited, row.FileContent);
    }

    /// <summary>
    /// Pointer move, case 1: the target artifact has no row at all for a relative
    /// path the pointed-away-from artifact carries. The row must be copied before
    /// the pointer moves.
    /// </summary>
    [Fact]
    public async Task ApplyImportedArtifact_PointerMove_CopiesRowsMissingOnTarget()
    {
        _database.InsertArtifactWithApp(100, "web-app", "2.0.0", "test-module", "test-app");
        _database.InsertArtifact(200, "web-app", "2.1.0", T1);
        _database.InsertArtifactConfigurationFile(100, AppSettingsPath, OperatorEdited, PackagedV1);
        var appInstanceId = _database.InsertAppInstanceForApp(appId: 1, artifactId: 100);

        var applied = await _repository.ApplyImportedArtifactToMatchingApplicationsAsync(200, CancellationToken.None);

        Assert.Equal(1, applied.AppInstanceRowsUpdated);
        Assert.Equal(200, _database.GetAppInstanceArtifactId(appInstanceId));
        var row = Assert.Single(_database.GetArtifactConfigurationFiles(200));
        Assert.Equal(AppSettingsPath, row.RelativePath);
        Assert.Equal(OperatorEdited, row.FileContent);
    }

    /// <summary>
    /// Pointer move, case 2: the target artifact holds a pristine package
    /// baseline while the pointed-away-from artifact carries an operator edit.
    /// The edit must be carried over before the pointer moves.
    /// </summary>
    [Fact]
    public async Task ApplyImportedArtifact_PointerMove_CarriesOperatorDeltaOverPristineTargetRow()
    {
        _database.InsertArtifactWithApp(100, "web-app", "2.0.0", "test-module", "test-app");
        _database.InsertArtifact(200, "web-app", "2.1.0", T1);
        _database.InsertArtifactConfigurationFile(100, AppSettingsPath, OperatorEdited, PackagedV1);
        _database.InsertArtifactConfigurationFile(200, AppSettingsPath, PackagedV2, PackagedV2);
        var appInstanceId = _database.InsertAppInstanceForApp(appId: 1, artifactId: 100);

        var applied = await _repository.ApplyImportedArtifactToMatchingApplicationsAsync(200, CancellationToken.None);

        Assert.Equal(1, applied.AppInstanceRowsUpdated);
        Assert.Equal(200, _database.GetAppInstanceArtifactId(appInstanceId));
        var row = Assert.Single(_database.GetArtifactConfigurationFiles(200));
        Assert.Equal(OperatorEdited, row.FileContent);
        Assert.Equal(PackagedV2, row.PackageFileContent);
    }

    /// <summary>
    /// Pointer move must NOT clobber a target row the operator already edited on
    /// the new version itself.
    /// </summary>
    [Fact]
    public async Task ApplyImportedArtifact_PointerMove_LeavesEditedTargetRowUntouched()
    {
        const string targetEdit = "{ \"OmpAuth\": { \"CookieName\": \".omp\", \"Oidc\": { \"Authority\": \"https://new.example\" } } }";
        _database.InsertArtifactWithApp(100, "web-app", "2.0.0", "test-module", "test-app");
        _database.InsertArtifact(200, "web-app", "2.1.0", T1);
        _database.InsertArtifactConfigurationFile(100, AppSettingsPath, OperatorEdited, PackagedV1);
        _database.InsertArtifactConfigurationFile(200, AppSettingsPath, targetEdit, PackagedV2);
        _database.InsertAppInstanceForApp(appId: 1, artifactId: 100);

        await _repository.ApplyImportedArtifactToMatchingApplicationsAsync(200, CancellationToken.None);

        var row = Assert.Single(_database.GetArtifactConfigurationFiles(200));
        Assert.Equal(targetEdit, row.FileContent);
    }

    /// <summary>
    /// Retention: the old version's appsettings.json carries an operator edit that
    /// exists on no newer version, so the artifact must survive the cleanup even
    /// though it ranks beyond the keep limit and has no other references.
    /// </summary>
    [Fact]
    public async Task RetentionCleanup_KeepsArtifactWhoseOperatorDeltaExistsOnNoNewerVersion()
    {
        _database.InsertArtifactWithApp(100, "web-app", "1.0.0", "test-module", "test-app");
        _database.InsertArtifact(101, "web-app", "2.0.0", T1);
        _database.InsertArtifactConfigurationFile(100, AppSettingsPath, OperatorEdited, PackagedV1);
        _database.InsertArtifactConfigurationFile(101, AppSettingsPath, PackagedV2, PackagedV2);

        var result = await _repository.ExecuteArtifactRetentionCleanupAsync(1, "test", CancellationToken.None);

        Assert.DoesNotContain(result.DeletedArtifacts, deleted => deleted.ArtifactId == 100);
        Assert.True(_database.ArtifactExists(100));
        var row = Assert.Single(_database.GetArtifactConfigurationFiles(100));
        Assert.Equal(OperatorEdited, row.FileContent);
    }

    /// <summary>
    /// Retention, the other half: when the operator-edited content survives
    /// byte-identically on a newer preserved version, the old artifact carries no
    /// unique configuration and is deleted as before.
    /// </summary>
    [Fact]
    public async Task RetentionCleanup_DeletesArtifactWhoseOperatorDeltaSurvivesByteIdenticalOnNewerKeptVersion()
    {
        _database.InsertArtifactWithApp(100, "web-app", "1.0.0", "test-module", "test-app");
        _database.InsertArtifact(101, "web-app", "2.0.0", T1);
        _database.InsertArtifactConfigurationFile(100, AppSettingsPath, OperatorEdited, PackagedV1);
        _database.InsertArtifactConfigurationFile(101, AppSettingsPath, OperatorEdited, PackagedV1);

        var result = await _repository.ExecuteArtifactRetentionCleanupAsync(1, "test", CancellationToken.None);

        Assert.Contains(result.DeletedArtifacts, deleted => deleted.ArtifactId == 100);
        Assert.False(_database.ArtifactExists(100));
        Assert.True(_database.ArtifactExists(101));
    }
}

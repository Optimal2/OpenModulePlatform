using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Proves that omp.Artifacts rejects duplicate artifact identities
/// (AppId, Version, PackageType, TargetName) at the database level, even when
/// the insert bypasses the application-level check-then-insert flow. The
/// fixture provisions the database by executing the real core setup script, so
/// these tests go red if the unique index is removed from
/// sql/1-setup-openmoduleplatform.sql.
/// </summary>
public sealed class OmpArtifactIdentityUniquenessTests : IClassFixture<ArtifactIdentityUniquenessTestFixture>
{
    private readonly ArtifactIdentityUniquenessTestFixture _fixture;

    public OmpArtifactIdentityUniquenessTests(ArtifactIdentityUniquenessTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DuplicateIdentityInsert_IsRejectedByUniqueIndex()
    {
        var moduleId = await _fixture.InsertModuleAsync("artifact-identity-uniqueness");
        var appId = await _fixture.InsertAppAsync(moduleId, "artifact-identity-uniqueness-app");

        await _fixture.InsertArtifactAsync(
            appId,
            "1.0.0",
            "web-app",
            "test-target",
            "web/artifact-identity-uniqueness-app/1.0.0",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        // Direct duplicate insert via raw SQL, bypassing application code.
        var ex = await Assert.ThrowsAnyAsync<SqlException>(() => _fixture.InsertArtifactAsync(
            appId,
            "1.0.0",
            "web-app",
            "test-target",
            "web/artifact-identity-uniqueness-app/1.0.0-duplicate",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));

        Assert.True(
            ex.Number is 2601 or 2627,
            $"Expected a unique-index/unique-constraint violation (2601 or 2627), got SqlException {ex.Number}: {ex.Message}");
    }

    [Fact]
    public async Task DuplicateNullTargetIdentityInsert_IsRejectedByUniqueIndex()
    {
        var moduleId = await _fixture.InsertModuleAsync("artifact-identity-uniqueness-null-target");
        var appId = await _fixture.InsertAppAsync(moduleId, "artifact-identity-uniqueness-null-target-app");

        await _fixture.InsertArtifactAsync(
            appId,
            "2.0.0",
            "worker",
            targetName: null,
            "worker/artifact-identity-uniqueness-null-target-app/2.0.0",
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc");

        // SQL Server treats NULL key values as equal in unique indexes, so a
        // duplicate NULL-target identity must also be rejected.
        var ex = await Assert.ThrowsAnyAsync<SqlException>(() => _fixture.InsertArtifactAsync(
            appId,
            "2.0.0",
            "worker",
            targetName: null,
            "worker/artifact-identity-uniqueness-null-target-app/2.0.0-duplicate",
            "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"));

        Assert.True(
            ex.Number is 2601 or 2627,
            $"Expected a unique-index/unique-constraint violation (2601 or 2627), got SqlException {ex.Number}: {ex.Message}");
    }
}

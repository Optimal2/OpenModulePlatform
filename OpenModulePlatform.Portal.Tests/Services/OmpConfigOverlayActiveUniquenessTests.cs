using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Proves that omp.ConfigOverlayDocuments allows at most one enabled document
/// per (OverlayKey, HostKey) at the database level, even when the insert
/// bypasses the application-level keep-history save paths. The fixture
/// provisions the database by executing the real core setup script, so these
/// tests go red if the filtered unique index is removed from
/// sql/1-setup-openmoduleplatform.sql.
/// </summary>
[Collection(ConfigOverlayActiveUniquenessCollection.CollectionName)]
public sealed class OmpConfigOverlayActiveUniquenessTests
{
    private readonly ConfigOverlayActiveUniquenessTestFixture _fixture;

    public OmpConfigOverlayActiveUniquenessTests(ConfigOverlayActiveUniquenessTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SecondEnabledDocumentForSameKeyAndHost_IsRejectedByUniqueIndex()
    {
        const string overlayKey = "overlay-active-uniqueness";
        const string hostKey = "overlay-active-uniqueness-host";

        await _fixture.InsertDocumentAsync(overlayKey, hostKey, "1.0.0", isEnabled: true);

        // Direct second enabled insert via raw SQL with a new version,
        // bypassing the application-level disable-then-insert flow.
        var ex = await Assert.ThrowsAnyAsync<SqlException>(() => _fixture.InsertDocumentAsync(
            overlayKey,
            hostKey,
            "1.0.1",
            isEnabled: true));

        Assert.True(
            ex.Number is 2601 or 2627,
            $"Expected a unique-index/unique-constraint violation (2601 or 2627), got SqlException {ex.Number}: {ex.Message}");

        Assert.Equal(1, await _fixture.CountDocumentsAsync(overlayKey, hostKey));
    }

    [Fact]
    public async Task DisabledDuplicateDocumentForSameKeyAndHost_IsAllowedAsHistory()
    {
        const string overlayKey = "overlay-active-uniqueness-history";
        const string hostKey = "overlay-active-uniqueness-history-host";

        await _fixture.InsertDocumentAsync(overlayKey, hostKey, "1.0.0", isEnabled: true);

        // Disabled historical rows for the same key and host must remain
        // insertable; only a second enabled row is rejected.
        await _fixture.InsertDocumentAsync(overlayKey, hostKey, "1.0.1", isEnabled: false);
        await _fixture.InsertDocumentAsync(overlayKey, hostKey, "1.0.2", isEnabled: false);

        Assert.Equal(3, await _fixture.CountDocumentsAsync(overlayKey, hostKey));
    }

    [Fact]
    public async Task EnabledDocumentForSameKeyOnDifferentHost_IsAllowed()
    {
        const string overlayKey = "overlay-active-uniqueness-per-host";

        await _fixture.InsertDocumentAsync(overlayKey, "overlay-active-uniqueness-host-a", "1.0.0", isEnabled: true);
        await _fixture.InsertDocumentAsync(overlayKey, "overlay-active-uniqueness-host-b", "1.0.0", isEnabled: true);

        Assert.Equal(1, await _fixture.CountDocumentsAsync(overlayKey, "overlay-active-uniqueness-host-a"));
        Assert.Equal(1, await _fixture.CountDocumentsAsync(overlayKey, "overlay-active-uniqueness-host-b"));
    }
}

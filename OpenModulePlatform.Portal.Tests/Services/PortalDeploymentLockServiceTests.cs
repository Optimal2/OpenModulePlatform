using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// The Portal's half of the deployment lock, which shares one file with HostAgent.
/// </summary>
/// <remarks>
/// R12-A2 and R12-A4 were fixed in HostAgentDeploymentLockLease while this copy -- the other
/// writer of the very same file at the Portal's content root -- was out of scope for that
/// change. Both defects were present here in a stronger form: acquisition never used the
/// atomic claim at all, and renewal never checked ownership at all.
/// </remarks>
public sealed class PortalDeploymentLockServiceTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Join(Path.GetTempPath(), $"omp-portal-lock-{Guid.NewGuid():N}"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException ex)
        {
            // DirectoryNotFoundException is an IOException, so this also covers the tests
            // that never got as far as creating the root.
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private PortalDeploymentLockService CreateService()
        => new(new StubHostEnvironment(_root), NullLogger<PortalDeploymentLockService>.Instance);

    [Fact]
    public async Task Acquire_WhenUnlocked_WritesTheLockFile()
    {
        await using var lease = await CreateService().AcquireUniversalImportLockAsync("tester", CancellationToken.None);

        var status = DeploymentLockFile.ReadStatus(_root, DateTimeOffset.UtcNow);
        Assert.True(status.IsLocked);
        Assert.Equal("omp_portal", status.Document!.ApplicationKey);
        Assert.Equal("tester", status.Document.Owner);
    }

    [Fact]
    public async Task Acquire_WhenLockIsHeld_Throws()
    {
        var now = DateTimeOffset.UtcNow;
        await DeploymentLockFile.WriteAsync(
            _root,
            DeploymentLockFile.Create("hostagent-lock", "omp_portal", "HostAgent", "deploying", now, now.AddMinutes(5)),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateService().AcquireUniversalImportLockAsync("tester", CancellationToken.None));

        Assert.Contains("hostagent-lock", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Acquire_WhenExistingLockExpired_ClearsItAndClaims()
    {
        var now = DateTimeOffset.UtcNow;
        await DeploymentLockFile.WriteAsync(
            _root,
            DeploymentLockFile.Create("stale-lock-id", "omp_portal", "owner", "crashed import", now.AddMinutes(-30), now.AddMinutes(-10)),
            CancellationToken.None);

        await using var lease = await CreateService().AcquireUniversalImportLockAsync("tester", CancellationToken.None);

        var status = DeploymentLockFile.ReadStatus(_root, DateTimeOffset.UtcNow);
        Assert.True(status.IsLocked);
        Assert.NotEqual("stale-lock-id", status.Document!.LockId);
    }

    [Fact]
    public async Task Dispose_RemovesTheLockFile_WhenStillOwned()
    {
        var lease = await CreateService().AcquireUniversalImportLockAsync("tester", CancellationToken.None);
        await lease.DisposeAsync();

        Assert.False(File.Exists(DeploymentLockFile.GetPath(_root)));
    }

    /// <summary>
    /// The expired-lock cleanup never removes a claim that is no longer the expired one.
    /// </summary>
    /// <remarks>
    /// R12-A2. Acquisition previously read the status and then called WriteAsync, which ends
    /// in File.Move(overwrite: true): whatever the read had concluded microseconds earlier,
    /// the write landed. The state below is exactly what a competing HostAgent leaves behind
    /// when it wins the race for the same expired file -- a different LockId with a future
    /// expiry -- and it must survive so that CreateNew can then tell this caller it lost.
    /// </remarks>
    [Fact]
    public async Task TryClearExpiredLock_LeavesAFreshCompetitorClaimAlone()
    {
        var now = DateTimeOffset.UtcNow;
        await DeploymentLockFile.WriteAsync(
            _root,
            DeploymentLockFile.Create("competitor-lock-id", "omp_portal", "HostAgent", "deploying", now, now.AddMinutes(5)),
            CancellationToken.None);

        PortalDeploymentLockService.TryClearExpiredLock(_root, "stale-lock-id", NullLogger.Instance);

        Assert.True(File.Exists(DeploymentLockFile.GetPath(_root)));
        Assert.Equal("competitor-lock-id", DeploymentLockFile.ReadStatus(_root, DateTimeOffset.UtcNow).Document!.LockId);
    }

    [Fact]
    public async Task TryClearExpiredLock_RemovesTheExpiredDocumentItObserved()
    {
        var now = DateTimeOffset.UtcNow;
        await DeploymentLockFile.WriteAsync(
            _root,
            DeploymentLockFile.Create("stale-lock-id", "omp_portal", "owner", "reason", now.AddMinutes(-30), now.AddMinutes(-10)),
            CancellationToken.None);

        PortalDeploymentLockService.TryClearExpiredLock(_root, "stale-lock-id", NullLogger.Instance);

        Assert.False(File.Exists(DeploymentLockFile.GetPath(_root)));
    }

    // The expected verdict travels as a string because the enum is internal to the Portal
    // assembly and this test method has to stay public for xunit to discover it.
    [Theory]
    [InlineData("mine", false, "Held")]
    [InlineData("theirs", false, "Lost")]
    // An expired document that still names us is our own lease running late, not a takeover.
    [InlineData("mine", true, "Held")]
    [InlineData("theirs", true, "Lost")]
    public void ClassifyRenewalOwnership_WithReadableDocument_IsAuthoritative(
        string lockId,
        bool expired,
        string expected)
    {
        var now = DateTimeOffset.UtcNow;
        var document = DeploymentLockFile.Create(
            lockId,
            "omp_portal",
            "owner",
            "reason",
            now,
            expired ? now.AddMinutes(-1) : now.AddMinutes(5));
        var status = expired
            ? DeploymentLockStatus.Expired("path", document)
            : DeploymentLockStatus.Locked("path", document, null);

        Assert.Equal(
            expected,
            PortalDeploymentLockLease.ClassifyRenewalOwnership(status, "mine").ToString());
    }

    [Fact]
    public void ClassifyRenewalOwnership_WithUnreadableLockFile_IsIndeterminate()
    {
        var status = DeploymentLockStatus.Locked("path", null, "Deployment lock file could not be read: boom.");

        Assert.Equal(
            PortalDeploymentLockLease.RenewalOwnership.Indeterminate,
            PortalDeploymentLockLease.ClassifyRenewalOwnership(status, "mine"));
    }

    [Fact]
    public void ClassifyRenewalOwnership_WithNoLockFile_IsHeldSoTheLeaseIsReasserted()
    {
        Assert.Equal(
            PortalDeploymentLockLease.RenewalOwnership.Held,
            PortalDeploymentLockLease.ClassifyRenewalOwnership(DeploymentLockStatus.NotLocked("path"), "mine"));
    }

    /// <summary>
    /// Renewal stops, and stops writing, when another claimant demonstrably owns the lock.
    /// </summary>
    /// <remarks>
    /// This is the behaviour the Portal lease did not have at all: the renewal loop wrote the
    /// lock file every 30 seconds without ever reading it. An import that outlived its
    /// five-minute lease therefore took ownership back from the HostAgent deployment that had
    /// legitimately claimed the expired lock, on the very next tick, while that deployment was
    /// replacing the Portal's files. Sabotage-checked: removing the ownership check makes this
    /// test fail on the LockId assertion.
    /// </remarks>
    [Fact]
    public async Task Renewal_StopsAndDoesNotOverwrite_WhenAnotherClaimantOwnsTheLock()
    {
        var lease = await CreateService().AcquireUniversalImportLockAsync(
            "tester",
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMilliseconds(100),
            maxConsecutiveIndeterminateReads: 100,
            CancellationToken.None);

        var now = DateTimeOffset.UtcNow;
        var other = DeploymentLockFile.Create("hostagent-lock-id", "omp_portal", "HostAgent", "deploying", now, now.AddMinutes(5));
        await DeploymentLockFile.WriteAsync(_root, other, CancellationToken.None);

        await Task.Delay(500);

        var status = DeploymentLockFile.ReadStatus(_root, DateTimeOffset.UtcNow);
        Assert.Equal("hostagent-lock-id", status.Document!.LockId);
        Assert.Equal(other.UpdatedUtc, status.Document.UpdatedUtc);

        await lease.DisposeAsync();

        // Dispose must not remove a lock file this lease no longer owns either.
        Assert.True(File.Exists(DeploymentLockFile.GetPath(_root)));
    }

    /// <summary>
    /// Renewal survives an unreadable lock file and resumes once it can be read again.
    /// </summary>
    /// <remarks>
    /// R12-A4. ReadStatus fails closed, so a corrupt or momentarily locked file arrives as
    /// "locked by nobody identifiable". Treating that as a change of owner would end renewal
    /// on one transient fault and let the lease expire silently under a running import.
    /// </remarks>
    [Fact]
    public async Task Renewal_SurvivesATransientUnreadableLockFile()
    {
        var lease = await CreateService().AcquireUniversalImportLockAsync(
            "tester",
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMilliseconds(100),
            maxConsecutiveIndeterminateReads: 100,
            CancellationToken.None);

        var path = DeploymentLockFile.GetPath(_root);
        var originalJson = ReadWithRetry(path);
        var originalUpdatedUtc = DeploymentLockFile.ReadStatus(_root, DateTimeOffset.UtcNow).Document!.UpdatedUtc;

        WriteWithRetry(path, "{ this is not valid json");
        await Task.Delay(350);
        WriteWithRetry(path, originalJson);

        var renewed = await WaitUntilAsync(() =>
        {
            var status = DeploymentLockFile.ReadStatus(_root, DateTimeOffset.UtcNow);
            return status.Document is not null && status.Document.UpdatedUtc > originalUpdatedUtc;
        });

        await lease.DisposeAsync();

        Assert.True(renewed, "Renewal did not resume after the lock file became readable again.");
    }

    /// <summary>
    /// Renewal gives up after the configured run of unreadable reads rather than renewing a
    /// lock it can never confirm it owns.
    /// </summary>
    /// <remarks>
    /// The tolerance added for R12-A4 must not turn into "renew regardless" (metod section
    /// 4.5), so the bound itself is exercised here: the file stays corrupt, so no write can
    /// ever land, and the lease must stop instead of looping forever.
    /// </remarks>
    [Fact]
    public async Task Renewal_GivesUpAfterTheBoundedRunOfUnreadableReads()
    {
        var lease = await CreateService().AcquireUniversalImportLockAsync(
            "tester",
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMilliseconds(50),
            maxConsecutiveIndeterminateReads: 2,
            CancellationToken.None);

        var path = DeploymentLockFile.GetPath(_root);
        WriteWithRetry(path, "{ this is not valid json");

        await Task.Delay(400);

        // Renewal has stopped, so the corrupt content is still exactly what the test wrote.
        Assert.Equal("{ this is not valid json", ReadWithRetry(path));

        await lease.DisposeAsync();
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    /// <summary>
    /// The renewal loop reads the same file the test writes, and the sharing modes collide,
    /// so both directions retry rather than fail the test for a reason it is not about.
    /// </summary>
    private static string ReadWithRetry(string path)
        => RetryFileOperation(() => File.ReadAllText(path));

    private static void WriteWithRetry(string path, string content)
        => RetryFileOperation<object?>(() =>
        {
            File.WriteAllText(path, content);
            return null;
        });

    private static T RetryFileOperation<T>(Func<T> operation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                return operation();
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(10);
            }
        }
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public StubHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
        }

        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = "OpenModulePlatform.Portal.Tests";

        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

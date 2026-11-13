using System.Reflection;
using OpenModulePlatform.Web.ContextGuardModule.Models;
using OpenModulePlatform.Web.ContextGuardModule.Stores;

namespace OpenModulePlatform.Web.ContextGuardModule.Tests;

public sealed class InMemoryHandoffStoreTests
{
    private const int PendingCapacity = 10;
    private const int TombstoneCapacity = 10;
    private readonly ManualTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    private readonly InMemoryHandoffStore _store;

    public InMemoryHandoffStoreTests() => _store = new InMemoryHandoffStore(_time);

    [Fact]
    public void Redeem_WhenCalledTwice_ReturnsPayloadOnlyOnce()
    {
        var created = Create();

        var first = _store.Redeem(created.Key!, TombstoneCapacity);
        var second = _store.Redeem(created.Key!, TombstoneCapacity);

        Assert.Equal(HandoffRedemptionStatus.Success, first.Status);
        Assert.Equal("allowed-value", first.Payload!.Fields.Single().Value);
        Assert.Equal(HandoffRedemptionStatus.AlreadyRedeemed, second.Status);
        Assert.Null(second.Payload);
    }

    [Fact]
    public async Task Redeem_WhenCalledConcurrently_YieldsExactlyOneSuccess()
    {
        var created = Create();
        var outcomes = new HandoffRedemptionStatus[16];
        await Parallel.ForAsync(0, outcomes.Length, async (i, _) =>
        {
            await Task.Yield();
            outcomes[i] = _store.Redeem(created.Key!, TombstoneCapacity).Status;
        });

        Assert.Equal(1, outcomes.Count(status => status == HandoffRedemptionStatus.Success));
        Assert.All(outcomes.Where(status => status != HandoffRedemptionStatus.Success),
            status => Assert.Equal(HandoffRedemptionStatus.AlreadyRedeemed, status));
    }

    [Fact]
    public void Redeem_AfterTtl_ReturnsExpiredWithoutPayload()
    {
        var created = Create();
        _time.Advance(TimeSpan.FromSeconds(61));

        var result = _store.Redeem(created.Key!, TombstoneCapacity);

        Assert.Equal(HandoffRedemptionStatus.Expired, result.Status);
        Assert.Null(result.Payload);
    }

    [Fact]
    public void Redeem_WhenNeverRedeemedKeyExpiresAfterPrune_ReturnsExpired()
    {
        var first = Create();
        _time.Advance(TimeSpan.FromSeconds(61));

        // A second create runs the prune pass, turning the expired pending
        // entry into a payload-free tombstone. Redeeming it must still report
        // Expired, not AlreadyRedeemed.
        Create();

        var result = _store.Redeem(first.Key!, TombstoneCapacity);

        Assert.Equal(HandoffRedemptionStatus.Expired, result.Status);
        Assert.Null(result.Payload);
    }

    [Fact]
    public void Redeem_WhenRedeemedKeyIsReusedAfterTtl_ReturnsAlreadyRedeemed()
    {
        var created = Create();
        Assert.Equal(HandoffRedemptionStatus.Success, _store.Redeem(created.Key!, TombstoneCapacity).Status);
        _time.Advance(TimeSpan.FromSeconds(61));

        var result = _store.Redeem(created.Key!, TombstoneCapacity);

        Assert.Equal(HandoffRedemptionStatus.AlreadyRedeemed, result.Status);
        Assert.Null(result.Payload);
    }

    [Fact]
    public void Redeem_WhenExpiredTombstoneIsPruned_ReturnsUnknown()
    {
        var created = Create();
        _time.Advance(TimeSpan.FromSeconds(61));
        Create();
        Assert.Equal(HandoffRedemptionStatus.Expired, _store.Redeem(created.Key!, TombstoneCapacity).Status);

        _time.Advance(TimeSpan.FromMinutes(5));
        Create();

        Assert.Equal(HandoffRedemptionStatus.Unknown, _store.Redeem(created.Key!, TombstoneCapacity).Status);
    }

    [Fact]
    public void Create_WhenCapacityIsFull_ReturnsCapacityExceeded()
    {
        var first = Create(pendingCapacity: 1);
        var second = Create(pendingCapacity: 1);

        Assert.Equal(HandoffCreateStatus.Created, first.Status);
        Assert.Equal(HandoffCreateStatus.CapacityExceeded, second.Status);
        Assert.Null(second.Key);
    }

    [Fact]
    public void Create_WhenAllPendingAreRedeemed_TombstonesDoNotConsumeCapacity()
    {
        const int capacity = 3;
        var keys = new List<string>();
        for (var i = 0; i < capacity; i++)
        {
            keys.Add(Create(pendingCapacity: capacity).Key!);
        }

        foreach (var key in keys)
        {
            Assert.Equal(HandoffRedemptionStatus.Success, _store.Redeem(key, TombstoneCapacity).Status);
        }

        for (var i = 0; i < capacity; i++)
        {
            var created = Create(pendingCapacity: capacity);
            Assert.Equal(HandoffCreateStatus.Created, created.Status);
            Assert.NotNull(created.Key);
        }
    }

    [Fact]
    public void Create_WhenEndedTombstonesFillOldCapacity_AllowsNewPending()
    {
        var first = Create(pendingCapacity: 1, tombstoneCapacity: 10);
        Assert.Equal(1, _store.EndByCorrelation("profile", "hash", 10));

        var second = Create(pendingCapacity: 1, tombstoneCapacity: 10);

        Assert.Equal(HandoffCreateStatus.Created, second.Status);
        Assert.NotNull(second.Key);
        Assert.Equal(HandoffRedemptionStatus.Ended, _store.Redeem(first.Key!, 10).Status);
    }

    [Fact]
    public void Tombstones_WhenCapIsExceeded_OldestIsEvictedFirst()
    {
        const int tombstones = 2;
        var first = Create(tombstoneCapacity: tombstones);
        _time.Advance(TimeSpan.FromSeconds(1));
        var second = Create(tombstoneCapacity: tombstones);
        _time.Advance(TimeSpan.FromSeconds(1));
        var third = Create(tombstoneCapacity: tombstones);

        Assert.Equal(HandoffRedemptionStatus.Success, _store.Redeem(first.Key!, tombstones).Status);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HandoffRedemptionStatus.Success, _store.Redeem(second.Key!, tombstones).Status);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HandoffRedemptionStatus.Success, _store.Redeem(third.Key!, tombstones).Status);

        Assert.Equal(HandoffRedemptionStatus.Unknown, _store.Redeem(first.Key!, tombstones).Status);
        Assert.Equal(HandoffRedemptionStatus.AlreadyRedeemed, _store.Redeem(second.Key!, tombstones).Status);
        Assert.Equal(HandoffRedemptionStatus.AlreadyRedeemed, _store.Redeem(third.Key!, tombstones).Status);
    }

    [Fact]
    public void EndByCorrelation_WhenHandoffIsPending_ClearsPayloadAndPreventsRedemption()
    {
        var created = Create();

        var ended = _store.EndByCorrelation("profile", "hash", TombstoneCapacity);
        var redemption = _store.Redeem(created.Key!, TombstoneCapacity);

        Assert.Equal(1, ended);
        Assert.Equal(HandoffRedemptionStatus.Ended, redemption.Status);
        Assert.Null(redemption.Payload);
    }

    [Fact]
    public void CreateAndEnd_WhenRepeated_BoundsTotalEntries()
    {
        const int pendingCapacity = 2;
        const int tombstoneCapacity = 4;

        for (var index = 0; index < 20; index++)
        {
            var created = _store.Create("profile", $"hash-{index}", Fields(), 0, TimeSpan.FromSeconds(60), pendingCapacity, tombstoneCapacity);

            Assert.Equal(HandoffCreateStatus.Created, created.Status);
            Assert.Equal(1, _store.EndByCorrelation("profile", $"hash-{index}", tombstoneCapacity));
            Assert.True(EntryCount() <= pendingCapacity + tombstoneCapacity, $"Entry count {EntryCount()} exceeded {pendingCapacity + tombstoneCapacity}.");
        }
    }

    private int EntryCount()
    {
        var field = typeof(InMemoryHandoffStore).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var entries = Assert.IsAssignableFrom<System.Collections.ICollection>(field.GetValue(_store));
        return entries.Count;
    }
    private HandoffCreateResult Create(int pendingCapacity = PendingCapacity, int tombstoneCapacity = TombstoneCapacity)
        => _store.Create("profile", "hash", Fields(), 0, TimeSpan.FromSeconds(60), pendingCapacity, tombstoneCapacity);

    private static ContextGuardField[] Fields() => [new("allowed", "allowed-value")];
}

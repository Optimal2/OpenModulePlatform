using System.Reflection;
using OpenModulePlatform.Web.ContextGuardModule.Models;
using OpenModulePlatform.Web.ContextGuardModule.Stores;

namespace OpenModulePlatform.Web.ContextGuardModule.Tests;

public sealed class InMemoryHandoffStoreTests
{
    private readonly ManualTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    private readonly InMemoryHandoffStore _store;

    public InMemoryHandoffStoreTests() => _store = new InMemoryHandoffStore(_time);

    [Fact]
    public void Redeem_WhenCalledTwice_ReturnsPayloadOnlyOnce()
    {
        var created = _store.Create("profile", "hash", Fields(), 0, TimeSpan.FromSeconds(60), 10);

        var first = _store.Redeem(created.Key!);
        var second = _store.Redeem(created.Key!);

        Assert.Equal(HandoffRedemptionStatus.Success, first.Status);
        Assert.Equal("allowed-value", first.Payload!.Fields.Single().Value);
        Assert.Equal(HandoffRedemptionStatus.AlreadyRedeemed, second.Status);
        Assert.Null(second.Payload);
    }

    [Fact]
    public void Redeem_AfterTtl_ReturnsExpiredWithoutPayload()
    {
        var created = _store.Create("profile", "hash", Fields(), 0, TimeSpan.FromSeconds(60), 10);
        _time.Advance(TimeSpan.FromSeconds(61));

        var result = _store.Redeem(created.Key!);

        Assert.Equal(HandoffRedemptionStatus.Expired, result.Status);
        Assert.Null(result.Payload);
    }

    [Fact]
    public void Redeem_WhenRedeemedKeyIsReusedAfterTtl_ReturnsAlreadyRedeemed()
    {
        var created = _store.Create("profile", "hash", Fields(), 0, TimeSpan.FromSeconds(60), 10);
        Assert.Equal(HandoffRedemptionStatus.Success, _store.Redeem(created.Key!).Status);
        _time.Advance(TimeSpan.FromSeconds(61));

        var result = _store.Redeem(created.Key!);

        Assert.Equal(HandoffRedemptionStatus.AlreadyRedeemed, result.Status);
        Assert.Null(result.Payload);
    }

    [Fact]
    public void Create_WhenCapacityIsFull_ReturnsCapacityExceeded()
    {
        var first = _store.Create("profile", "hash-1", Fields(), 0, TimeSpan.FromSeconds(60), 1);
        var second = _store.Create("profile", "hash-2", Fields(), 0, TimeSpan.FromSeconds(60), 1);

        Assert.Equal(HandoffCreateStatus.Created, first.Status);
        Assert.Equal(HandoffCreateStatus.CapacityExceeded, second.Status);
        Assert.Null(second.Key);
    }

    [Fact]
    public void EndByCorrelation_WhenHandoffIsPending_ClearsPayloadAndPreventsRedemption()
    {
        var created = _store.Create("profile", "hash", Fields(), 0, TimeSpan.FromSeconds(60), 1);

        var ended = _store.EndByCorrelation("profile", "hash");
        var redemption = _store.Redeem(created.Key!);

        Assert.Equal(1, ended);
        Assert.Equal(HandoffRedemptionStatus.Ended, redemption.Status);
        Assert.Null(redemption.Payload);
    }

    [Fact]
    public void Create_WhenEachHandoffIsRedeemedBeforeNext_DoesNotCountTombstonesAsPending()
    {
        const int capacity = 2;

        for (var index = 0; index < capacity * 4; index++)
        {
            var created = _store.Create("profile", $"hash-{index}", Fields(), 0, TimeSpan.FromSeconds(60), capacity);

            Assert.Equal(HandoffCreateStatus.Created, created.Status);
            Assert.Equal(HandoffRedemptionStatus.Success, _store.Redeem(created.Key!).Status);
        }
    }

    [Fact]
    public void CreateAndEnd_WhenRepeated_BoundsTotalEntries()
    {
        const int capacity = 2;

        for (var index = 0; index < 20; index++)
        {
            var created = _store.Create("profile", $"hash-{index}", Fields(), 0, TimeSpan.FromSeconds(60), capacity);

            Assert.Equal(HandoffCreateStatus.Created, created.Status);
            Assert.Equal(1, _store.EndByCorrelation("profile", $"hash-{index}"));
            Assert.True(EntryCount() <= capacity * 3, $"Entry count {EntryCount()} exceeded {capacity * 3}.");
        }
    }

    [Fact]
    public void Create_WhenPendingHandoffsFillCapacity_ReturnsCapacityExceeded()
    {
        const int capacity = 3;

        for (var index = 0; index < capacity; index++)
        {
            var created = _store.Create("profile", $"hash-{index}", Fields(), 0, TimeSpan.FromSeconds(60), capacity);
            Assert.Equal(HandoffCreateStatus.Created, created.Status);
        }

        var next = _store.Create("profile", "hash-next", Fields(), 0, TimeSpan.FromSeconds(60), capacity);

        Assert.Equal(HandoffCreateStatus.CapacityExceeded, next.Status);
        Assert.Null(next.Key);
    }

    [Fact]
    public void Redeem_WhenNeverRedeemedKeyExpiresAndAnotherHandoffIsCreated_RemainsExpired()
    {
        var expired = _store.Create("profile", "hash-expired", Fields(), 0, TimeSpan.FromSeconds(60), 10);
        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HandoffCreateStatus.Created, _store.Create("profile", "hash-next", Fields(), 0, TimeSpan.FromSeconds(60), 10).Status);

        var firstVisit = _store.Redeem(expired.Key!);
        var secondVisit = _store.Redeem(expired.Key!);

        Assert.Equal(HandoffRedemptionStatus.Expired, firstVisit.Status);
        Assert.Equal(HandoffRedemptionStatus.Expired, secondVisit.Status);
    }

    [Fact]
    public void Redeem_WhenNeverRedeemedKeyExpires_RemainsExpiredOnSecondVisit()
    {
        var created = _store.Create("profile", "hash", Fields(), 0, TimeSpan.FromSeconds(60), 10);
        _time.Advance(TimeSpan.FromSeconds(61));

        var firstVisit = _store.Redeem(created.Key!);
        var secondVisit = _store.Redeem(created.Key!);

        Assert.Equal(HandoffRedemptionStatus.Expired, firstVisit.Status);
        Assert.Equal(HandoffRedemptionStatus.Expired, secondVisit.Status);
    }

    private int EntryCount()
    {
        var field = typeof(InMemoryHandoffStore).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var entries = Assert.IsAssignableFrom<System.Collections.ICollection>(field.GetValue(_store));
        return entries.Count;
    }

    private static ContextGuardField[] Fields() => [new("allowed", "allowed-value")];
}

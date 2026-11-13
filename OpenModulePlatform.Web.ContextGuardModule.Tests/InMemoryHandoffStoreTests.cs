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

    private static ContextGuardField[] Fields() => [new("allowed", "allowed-value")];
}

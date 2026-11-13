using OpenModulePlatform.Web.ContextGuardModule.Models;
using OpenModulePlatform.Web.ContextGuardModule.Options;
using OpenModulePlatform.Web.ContextGuardModule.Stores;

namespace OpenModulePlatform.Web.ContextGuardModule.Tests;

public sealed class InMemoryContextStoreTests
{
    private readonly ManualTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    private readonly InMemoryContextStore _store;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public InMemoryContextStoreTests() => _store = new InMemoryContextStore(_time);

    [Fact]
    public void Create_WithUserScope_SupersedesPreviousContextAndRefusesItsHeartbeat()
    {
        var first = Create();
        var second = Create();

        Assert.Equal(ContextLifecycle.Superseded, _store.Heartbeat(first.ContextId, 7, Timeout).State);
        Assert.Equal(ContextLifecycle.Current, _store.Heartbeat(second.ContextId, 7, Timeout).State);
    }

    [Fact]
    public void Create_WithUserAndClientScope_DoesNotSupersedeAnotherClient()
    {
        var first = _store.Create(7, "client-a", "profile", "hash", ContextGuardScopes.UserAndClient, Timeout);
        var second = _store.Create(7, "client-b", "profile", "hash", ContextGuardScopes.UserAndClient, Timeout);

        Assert.Equal(ContextLifecycle.Current, _store.Heartbeat(first.ContextId, 7, Timeout).State);
        Assert.Equal(ContextLifecycle.Current, _store.Heartbeat(second.ContextId, 7, Timeout).State);
    }

    [Fact]
    public void Heartbeat_AfterTimeout_EndsContext()
    {
        var context = Create();
        _time.Advance(Timeout + TimeSpan.FromSeconds(1));

        Assert.Equal(ContextLifecycle.Ended, _store.Heartbeat(context.ContextId, 7, Timeout).State);
    }

    [Fact]
    public void Heartbeat_AfterTimeoutBeforeNextMaintenance_EndsContext()
    {
        var context = Create();
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ContextLifecycle.Current, _store.Heartbeat(context.ContextId, 7, Timeout).State);

        _time.Advance(TimeSpan.FromSeconds(29));
        _store.Create(8, "", "profile", "other-hash", ContextGuardScopes.User, Timeout);
        _time.Advance(TimeSpan.FromSeconds(2));

        var result = _store.Heartbeat(context.ContextId, 7, Timeout);

        Assert.Equal(ContextLifecycle.Ended, result.State);
        Assert.NotNull(result.Context);
    }

    [Fact]
    public void GetState_ForAnotherUser_DoesNotReturnContext()
    {
        var context = Create();

        var result = _store.GetState(context.ContextId, 8, Timeout);

        Assert.Equal(ContextLifecycle.Ended, result.State);
        Assert.Null(result.Context);
    }

    [Fact]
    public void EndByCorrelation_EndsEveryMatchingCurrentContext()
    {
        var first = _store.Create(7, "client-a", "profile", "hash", ContextGuardScopes.UserAndClient, Timeout);
        var second = _store.Create(8, "client-b", "profile", "hash", ContextGuardScopes.UserAndClient, Timeout);

        var count = _store.EndByCorrelation("profile", "hash");

        Assert.Equal(2, count);
        Assert.Equal(ContextLifecycle.Ended, _store.GetState(first.ContextId, 7, Timeout).State);
        Assert.Equal(ContextLifecycle.Ended, _store.GetState(second.ContextId, 8, Timeout).State);
    }

    [Fact]
    public void SupersededContext_IsPrunedAfterRetentionAndStillReturnsEnded()
    {
        var first = Create();
        Create();

        var retained = _store.GetState(first.ContextId, 7, Timeout);
        Assert.Equal(ContextLifecycle.Superseded, retained.State);
        Assert.NotNull(retained.Context);

        _time.Advance(TimeSpan.FromTicks((Timeout.Ticks * 3) + 1));

        var pruned = _store.Heartbeat(first.ContextId, 7, Timeout);
        Assert.Equal(ContextLifecycle.Ended, pruned.State);
        Assert.Null(pruned.Context);
    }

    private GuardContext Create() => _store.Create(7, "", "profile", "hash", ContextGuardScopes.User, Timeout);
}

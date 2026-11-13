using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.ContextGuardModule.Models;
using OpenModulePlatform.Web.ContextGuardModule.Options;
using OpenModulePlatform.Web.ContextGuardModule.Services;
using OpenModulePlatform.Web.ContextGuardModule.Stores;

namespace OpenModulePlatform.Web.ContextGuardModule.Tests;

public sealed class ContextGuardServiceTests
{
    [Fact]
    public void ReceiveRedeemAndEnd_WithSensitiveFields_NeverWritesFieldValuesToLogs()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var logs = new CapturedLogger();
        var options = new ContextGuardOptions
        {
            Profiles = new Dictionary<string, ContextGuardProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["profile"] = new()
                {
                    TargetUrl = "https://target.example/result",
                    AllowedFieldNames = ["correlation", "secretField"],
                    CorrelationFieldName = "correlation"
                }
            }
        };
        var service = new ContextGuardService(
            new InMemoryHandoffStore(time),
            new InMemoryContextStore(time),
            new FakeOptionsMonitor(options),
            logs);
        const string correlationSecret = "correlation-value-must-not-leak";
        const string fieldSecret = "field-value-must-not-leak";
        var fields = new[]
        {
            new ContextGuardField("correlation", correlationSecret),
            new ContextGuardField("secretField", fieldSecret)
        };

        var created = service.Receive("profile", fields, 0);
        var redeemed = service.Redeem(created.Key!, 42, "");
        var ended = service.End("profile", correlationSecret);

        Assert.Equal(HandoffRedemptionStatus.Success, redeemed.Status);
        Assert.Equal(1, ended);
        Assert.Equal(ContextLifecycle.Ended,
            service.GetState(redeemed.Redeemed!.Context.ContextId, 42).State);
        Assert.NotEmpty(logs.Messages);
        Assert.DoesNotContain(logs.Messages, message => message.Contains(correlationSecret, StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, message => message.Contains(fieldSecret, StringComparison.Ordinal));
    }

    [Fact]
    public void TryGetProfile_WithUnknownUnsafeKey_LogsOnlyBoundedMarkerAndLength()
    {
        var logs = new CapturedLogger();
        var service = new ContextGuardService(
            new InMemoryHandoffStore(TimeProvider.System),
            new InMemoryContextStore(TimeProvider.System),
            new FakeOptionsMonitor(new ContextGuardOptions()),
            logs);
        var unsafeKey = "unknown\r\n" + new string('x', 5000);

        var found = service.TryGetProfile(unsafeKey, out _);

        Assert.False(found);
        var message = Assert.Single(logs.Messages);
        Assert.DoesNotContain(unsafeKey, message, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", message, StringComparison.Ordinal);
        Assert.Contains("ProfileKey=<invalid>", message, StringComparison.Ordinal);
        Assert.Contains($"ProfileKeyLength={unsafeKey.Length}", message, StringComparison.Ordinal);
    }

    private sealed class FakeOptionsMonitor(ContextGuardOptions value) : IOptionsMonitor<ContextGuardOptions>
    {
        public ContextGuardOptions CurrentValue => value;
        public ContextGuardOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<ContextGuardOptions, string?> listener) => null;
    }

    private sealed class CapturedLogger : ILogger<ContextGuardService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenModulePlatform.Web.Shared.ActivityLog;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class ActivityLogDiagnosticTests
{
    [Fact]
    public async Task MissingUserAndFailedWrite_SanitizeEveryDiagnosticField()
    {
        const string input = "value\r\nforged\t\u0085\u2028\u2029";
        var logger = new CaptureLogger();
        // Missing configuration fails before opening any database connection.
        var writer = new ActivityLogWriter(new SqlConnectionFactory(new ConfigurationBuilder().Build()),
            new ActivityLogOptions { SchemaName = "sample", ModuleKey = input, AppKey = input }, logger);

        await writer.WriteAsync(new ActivityEntry { Event = input, Summary = input }, ompUserId: null);

        Assert.Equal(new[] { LogLevel.Warning, LogLevel.Error }, logger.Entries.Select(e => e.Level));
        Assert.All(logger.Entries, entry =>
        {
            Assert.Null(entry.Exception);
            Assert.Contains("value  forged    ", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(entry.Message, c => char.IsControl(c) || c is '\u2028' or '\u2029');
            Assert.All(entry.Values.OfType<string>(), value =>
                Assert.DoesNotContain(value, c => char.IsControl(c) || c is '\u2028' or '\u2029'));
        });
        Assert.Contains("Missing connection string", logger.Entries[1].Message, StringComparison.Ordinal);
    }

    private sealed class CaptureLogger : ILogger<ActivityLogWriter>
    {
        public List<(LogLevel Level, string Message, Exception? Exception, object?[] Values)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception,
                ((IEnumerable<KeyValuePair<string, object?>>)state!).Select(p => p.Value).ToArray()));
    }
}

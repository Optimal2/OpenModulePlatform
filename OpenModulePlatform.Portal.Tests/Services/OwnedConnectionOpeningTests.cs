using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class OwnedConnectionOpeningTests
{
    [Fact]
    public async Task FailedOpenDisposesConnectionAndPreservesException()
    {
        var expected = new InvalidOperationException("Injected open failure.");
        var connection = new FakeConnection(expected);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => OwnedConnectionOpening.OpenAsync(connection));

        Assert.Same(expected, actual);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task SuccessfulOpenTransfersLiveConnectionToCaller()
    {
        var connection = new FakeConnection(null);

        var opened = await OwnedConnectionOpening.OpenAsync(connection);

        Assert.Same(connection, opened);
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(0, connection.DisposeCount);
        await connection.DisposeAsync();
        Assert.Equal(1, connection.DisposeCount);
    }

    private sealed class FakeConnection(Exception? openFailure) : DbConnection
    {
        private ConnectionState _state = ConnectionState.Closed;

        public int DisposeCount { get; private set; }
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "Fake";
        public override string DataSource => "Fake";
        public override string ServerVersion => "1.0";
        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() => _state = ConnectionState.Closed;
        public override void Open() => OpenAsync(CancellationToken.None).GetAwaiter().GetResult();

        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            if (openFailure is not null)
                return Task.FromException(openFailure);
            _state = ConnectionState.Open;
            return Task.CompletedTask;
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
                _state = ConnectionState.Closed;
            }
            base.Dispose(disposing);
        }
    }
}

// File: OpenModulePlatform.Web.Shared/Telemetry/OmpDbRoundTripScope.cs
using Microsoft.Data.SqlClient;
using System.Data;

namespace OpenModulePlatform.Web.Shared.Telemetry;

/// <summary>
/// Counts the database round trips made while an operation runs, across every service that
/// takes part in it.
/// </summary>
/// <remarks>
/// <para>
/// R12-E3. The topbar reported <c>topbar.db.calls</c> by incrementing a field next to each
/// <c>SqlConnection</c> it opened itself. That number was wrong twice over: it counted
/// <em>connections</em> rather than statements sent to the server, and it only saw the
/// connections opened inside <c>PortalTopBarService</c> -- while the build also asks
/// RbacService, OmpBrandingService, OmpConfigurationService, NotificationService,
/// MessageService and BannerService to do their own database work. R4-E10 is the standing
/// question this figure is supposed to decide, so a figure that measures the wrong thing
/// decides it wrongly.
/// </para>
/// <para>
/// The scope is ambient (<see cref="AsyncLocal{T}"/>) so it spans the whole operation
/// regardless of which class does the work, and the count comes from the driver's own
/// <c>ServerRoundtrips</c> statistic rather than from a hand-maintained counter that the
/// next added query would silently forget to increment. Measured on this platform's SQL
/// Server: three commands on one pooled connection report exactly three round trips, and
/// the statistic resets when the connection is opened, so a reused pooled connection never
/// double counts.
/// </para>
/// <para>
/// Statistics gathering is only switched on for connections created while a scope is
/// active, so the cost is confined to the operation being measured.
/// </para>
/// </remarks>
public sealed class OmpDbRoundTripScope : IDisposable
{
    private sealed class Counter
    {
        public int Value;
    }

    private static readonly AsyncLocal<Counter?> Ambient = new();

    private readonly Counter _counter;
    private readonly Counter? _previous;
    private bool _disposed;

    private OmpDbRoundTripScope()
    {
        _previous = Ambient.Value;
        _counter = new Counter();
        Ambient.Value = _counter;
    }

    /// <summary>Starts counting round trips for the current asynchronous flow.</summary>
    /// <remarks>
    /// A nested scope measures only its own inner operation; the platform has no nested
    /// measured operation today, and an inner scope that silently folded into an outer one
    /// would make both numbers harder to reason about than either is worth.
    /// </remarks>
    public static OmpDbRoundTripScope Begin() => new();

    /// <summary>Round trips observed so far in this scope.</summary>
    public int RoundTrips => Volatile.Read(ref _counter.Value);

    /// <summary>
    /// Makes <paramref name="connection"/> report its round trips to the active scope.
    /// Does nothing when no scope is active.
    /// </summary>
    public static void Instrument(SqlConnection? connection)
    {
        var counter = Ambient.Value;
        if (connection is null || counter is null)
        {
            return;
        }

        connection.StatisticsEnabled = true;

        // The counter is captured rather than looked up again in the handler: the
        // connection may well be closed after the scope has ended, and the round trips it
        // made still belong to the operation that opened it.
        connection.StateChange += (sender, args) =>
        {
            if (args.CurrentState is not (ConnectionState.Closed or ConnectionState.Broken)
                || sender is not SqlConnection closed)
            {
                return;
            }

            try
            {
                if (closed.RetrieveStatistics()["ServerRoundtrips"] is long roundTrips && roundTrips > 0)
                {
                    Interlocked.Add(ref counter.Value, (int)Math.Min(roundTrips, int.MaxValue));
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // This handler runs inside SqlConnection.Close(). Throwing from here would
                // turn a measurement into a failed request -- the one outcome telemetry
                // must never produce -- so the sample is dropped instead.
            }
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Ambient.Value = _previous;
    }
}

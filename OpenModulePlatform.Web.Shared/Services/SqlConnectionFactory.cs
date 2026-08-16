// File: OpenModulePlatform.Web.Shared/Services/SqlConnectionFactory.cs
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OpenModulePlatform.Web.Shared.Telemetry;

namespace OpenModulePlatform.Web.Shared.Services;

/// <summary>
/// Creates SQL connections for OMP web applications.
/// </summary>
/// <remarks>
/// The factory stays intentionally small so that repositories remain explicit about when
/// they open and close connections. It is, however, the one place every web application's
/// database access passes through, which makes it the right -- and only -- place to state
/// the timeouts and to let a measurement scope see the work.
/// </remarks>
public sealed class SqlConnectionFactory
{
    /// <summary>
    /// Seconds to wait for a connection before failing.
    /// </summary>
    /// <remarks>
    /// R12-E7. Nothing set this, so the driver default of 15 seconds applied: a page
    /// rendered while SQL Server was unreachable took a measured 15 seconds to produce its
    /// error, and every concurrent request held a thread for just as long. The same timeout
    /// also governs the wait for a free pooled connection, so under pool starvation the
    /// whole application degraded at 15-second granularity.
    ///
    /// Five seconds is chosen from the asymmetry: in the failure modes actually seen --
    /// server down, instance restarting, pool saturated -- a connection that is not
    /// available within five seconds is not available within fifteen either, and the page
    /// only gets slower for the user and more expensive for the server by waiting. An
    /// installation that genuinely needs longer sets Connect Timeout in the connection
    /// string, which is honoured below.
    /// </remarks>
    private const int DefaultConnectTimeoutSeconds = 5;

    /// <summary>
    /// Seconds a command may run before the driver aborts it.
    /// </summary>
    /// <remarks>
    /// R12-E7. This is the driver's own default value, stated explicitly so it has a name
    /// and a single place to change rather than being inherited invisibly from SqlCommand.
    /// It is deliberately not lowered: the Portal's artifact import and maintenance
    /// repositories share this factory with ordinary page rendering, and a shorter blanket
    /// limit would abort administrative work that is legitimately slow. Only the connect
    /// timeout above distinguishes "the server is not answering" from "this query is
    /// heavy", so that is the one that was shortened.
    /// </remarks>
    private const int DefaultCommandTimeoutSeconds = 30;

    private const string ConnectTimeoutKeyword = "Connect Timeout";
    private const string CommandTimeoutKeyword = "Command Timeout";

    /// <summary>A configured connection string and the same string with the defaults applied.</summary>
    /// <remarks>
    /// One object rather than two fields: the factory is a singleton, so two threads writing
    /// two fields could leave a caller holding the source string of one and the effective
    /// string of another. The pair is written in one reference assignment instead.
    /// </remarks>
    private sealed record ConnectionStringPair(string Source, string Effective);

    private readonly IConfiguration _configuration;
    private ConnectionStringPair? _cached;

    public SqlConnectionFactory(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public SqlConnection Create()
    {
        return CreateConnection(ApplyDefaults(GetConnectionString()));
    }

    public SqlConnection CreateForDatabase(string databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            return Create();
        }

        var builder = new SqlConnectionStringBuilder(GetConnectionString())
        {
            InitialCatalog = databaseName.Trim()
        };

        ApplyDefaults(builder);
        return CreateConnection(builder.ConnectionString);
    }

    private static SqlConnection CreateConnection(string connectionString)
    {
        var connection = new SqlConnection(connectionString);

        // Only has an effect while a measurement scope is active, which is where the
        // round trips of a whole operation are counted across services (R12-E3).
        OmpDbRoundTripScope.Instrument(connection);
        return connection;
    }

    /// <summary>
    /// Applies the platform timeouts to a connection string, leaving any value the operator
    /// set explicitly untouched.
    /// </summary>
    private string ApplyDefaults(string connectionString)
    {
        // The configured string does not change during the process lifetime, so the parse
        // and rebuild happen once rather than on every connection.
        var cached = _cached;
        if (cached is not null && string.Equals(cached.Source, connectionString, StringComparison.Ordinal))
        {
            return cached.Effective;
        }

        var builder = new SqlConnectionStringBuilder(connectionString);
        ApplyDefaults(builder);

        _cached = new ConnectionStringPair(connectionString, builder.ConnectionString);
        return _cached.Effective;
    }

    private static void ApplyDefaults(SqlConnectionStringBuilder builder)
    {
        // ShouldSerialize reports whether the keyword was present in the configured string,
        // which is what separates "the operator chose 15 seconds" from "nobody chose
        // anything and the driver default applied". Verified against Microsoft.Data
        // .SqlClient 7.0.2: false for an absent keyword, true for one written out.
        if (!builder.ShouldSerialize(ConnectTimeoutKeyword))
        {
            builder.ConnectTimeout = DefaultConnectTimeoutSeconds;
        }

        if (!builder.ShouldSerialize(CommandTimeoutKeyword))
        {
            builder.CommandTimeout = DefaultCommandTimeoutSeconds;
        }
    }

    private string GetConnectionString()
    {
        var connectionString = _configuration.GetConnectionString("OmpDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Missing connection string: ConnectionStrings:OmpDb");
        }

        return connectionString;
    }
}

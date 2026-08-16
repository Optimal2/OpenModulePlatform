using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    /// <summary>
    /// Seconds to wait for a connection before failing.
    /// </summary>
    /// <remarks>
    /// R12-E7's sibling. The Web.Shared factory was given this treatment and HostAgent's own
    /// copy -- same class name, same job, different assembly -- was left on the driver default
    /// of 15 seconds (§4.1). HostAgent is not a web page, so the argument is a different one:
    /// the convergence cycle runs every RefreshSeconds (30 by default) and opens several
    /// connections per cycle, so an unreachable SQL Server made a single cycle outlast its own
    /// interval, and the deployment-lock lease renews on a 30-second timer of its own. A
    /// connect attempt that has not succeeded in five seconds has not failed for a reason that
    /// six more seconds would fix; the cycle logs the failure and retries in 30 seconds
    /// anyway, so a too-short wait is self-correcting where a too-long one is not.
    ///
    /// Measured on LINUS-LAPTOP against the deployed connection string (Data Source=localhost,
    /// Integrated Security): 103 ms cold, under 3 ms warm. Installations with a remote or slow
    /// instance raise it by writing Connect Timeout in the connection string, which is honoured
    /// below and needs no code change.
    /// </remarks>
    private const int DefaultConnectTimeoutSeconds = 5;

    /// <summary>
    /// Seconds a command may run before the driver aborts it.
    /// </summary>
    /// <remarks>
    /// R12-E7's sibling. This is the driver's own default, stated explicitly so it has a name
    /// and one place to change rather than being inherited invisibly from SqlCommand -- setting
    /// it changes no behaviour today, which is the point of setting it.
    ///
    /// It is deliberately not lowered. The callers were checked before choosing: the three
    /// genuinely long operations in OmpHostArtifactRepository -- artifact retention cleanup and
    /// the two module-definition SQL batch runners -- already override this per command with
    /// CommandTimeout = 3600, and a per-command value wins over the connection string, so they
    /// are unaffected either way. Everything else is short catalogue and state SQL where 30
    /// seconds is already far beyond normal, and a blanket cut would only convert a slow
    /// database into failed convergence cycles.
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

    /// <summary>
    /// Returns the configured connection string exactly as the operator wrote it, without the
    /// timeouts applied.
    /// </summary>
    /// <remarks>
    /// This deliberately does not go through <see cref="ApplyDefaults"/>, and the distinction
    /// matters: the value returned here is not only used to open connections. It travels
    /// through OmpHostArtifactRepository.GetConfiguredConnectionString into
    /// WebAppDeploymentService, ServiceAppDeploymentService and HostAgentSelfUpgradeService,
    /// which write it into every deployed application's appsettings.json as
    /// ConnectionStrings:OmpDb. Applying HostAgent's timeouts here would silently rewrite the
    /// connection string of every web app and service app on the host, and would then look to
    /// the Web.Shared factory like a value the operator had chosen explicitly -- suppressing
    /// its own defaults. HostAgent's timeouts are HostAgent's, and are applied in
    /// <see cref="Create"/> alone.
    /// </remarks>
    public string GetConnectionString()
    {
        var connectionString = _configuration.GetConnectionString("OmpDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:OmpDb is not configured.");
        }

        return connectionString;
    }

    public SqlConnection Create()
    {
        return new SqlConnection(ApplyDefaults(GetConnectionString()));
    }

    /// <summary>
    /// Applies the platform timeouts to a connection string, leaving any value the operator
    /// set explicitly untouched.
    /// </summary>
    private string ApplyDefaults(string connectionString)
    {
        // The configured string does not change during the process lifetime, so the parse and
        // rebuild happen once rather than on every connection.
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

    internal static void ApplyDefaults(SqlConnectionStringBuilder builder)
    {
        // ShouldSerialize reports whether the keyword was present in the configured string,
        // which is what separates "the operator chose 15 seconds" from "nobody chose anything
        // and the driver default applied". Verified against Microsoft.Data.SqlClient 7.0.2:
        // false for an absent keyword, true for one written out.
        if (!builder.ShouldSerialize(ConnectTimeoutKeyword))
        {
            builder.ConnectTimeout = DefaultConnectTimeoutSeconds;
        }

        if (!builder.ShouldSerialize(CommandTimeoutKeyword))
        {
            builder.CommandTimeout = DefaultCommandTimeoutSeconds;
        }
    }
}

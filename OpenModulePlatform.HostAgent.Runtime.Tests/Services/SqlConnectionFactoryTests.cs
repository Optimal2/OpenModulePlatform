using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// R12-E7's sibling: HostAgent's own copy of the connection factory stated neither timeout.
/// </summary>
public sealed class SqlConnectionFactoryTests
{
    private const string BareConnectionString = "Data Source=localhost;Initial Catalog=OpenModulePlatform;Integrated Security=True";

    private static SqlConnectionFactory CreateFactory(string connectionString)
        => new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OmpDb"] = connectionString
            })
            .Build());

    /// <summary>
    /// Without this, the driver's 15-second connect timeout applied and an unreachable SQL
    /// Server stalled a convergence cycle past its own 30-second refresh interval.
    /// </summary>
    [Fact]
    public void Create_AppliesTheConnectTimeout_WhenTheConnectionStringDoesNotStateOne()
    {
        using var connection = CreateFactory(BareConnectionString).Create();

        Assert.Equal(5, connection.ConnectionTimeout);
    }

    /// <summary>
    /// 30 seconds is the driver's own default, so this changes no behaviour -- it gives the
    /// value a name and one place to change. The assertion is what stops a later edit from
    /// lowering it silently and aborting legitimate work.
    /// </summary>
    [Fact]
    public void Create_AppliesTheCommandTimeout_WhenTheConnectionStringDoesNotStateOne()
    {
        using var connection = CreateFactory(BareConnectionString).Create();

        Assert.Equal(30, connection.CommandTimeout);
    }

    /// <summary>
    /// An installation with a remote or slow instance raises the timeouts in the connection
    /// string, and that must not be overwritten by the platform default.
    /// </summary>
    [Theory]
    [InlineData(30)]
    [InlineData(2)]
    public void Create_KeepsAnOperatorSuppliedConnectTimeout(int configured)
    {
        using var connection = CreateFactory($"{BareConnectionString};Connect Timeout={configured}").Create();

        Assert.Equal(configured, connection.ConnectionTimeout);
    }

    [Theory]
    [InlineData(600)]
    [InlineData(5)]
    public void Create_KeepsAnOperatorSuppliedCommandTimeout(int configured)
    {
        using var connection = CreateFactory($"{BareConnectionString};Command Timeout={configured}").Create();

        Assert.Equal(configured, connection.CommandTimeout);
    }

    /// <summary>
    /// A per-command timeout still wins over the connection string, which is why the three
    /// long-running repository operations that set CommandTimeout = 3600 are unaffected.
    /// </summary>
    [Fact]
    public void CommandTimeoutOnTheCommandOverridesTheConnectionString()
    {
        using var connection = CreateFactory(BareConnectionString).Create();
        using var command = new SqlCommand("SELECT 1", connection) { CommandTimeout = 3600 };

        Assert.Equal(3600, command.CommandTimeout);
    }

    /// <summary>
    /// A command that states nothing inherits the connection's value rather than the
    /// driver-wide constant, which is the mechanism the defaults above rely on.
    /// </summary>
    [Fact]
    public void CommandWithoutAnExplicitTimeoutInheritsTheConnectionValue()
    {
        using var connection = CreateFactory($"{BareConnectionString};Command Timeout=123").Create();
        using var command = new SqlCommand("SELECT 1", connection);

        Assert.Equal(123, command.CommandTimeout);
    }

    /// <summary>
    /// GetConnectionString must return the operator's string untouched.
    /// </summary>
    /// <remarks>
    /// It is not only used to open connections: OmpHostArtifactRepository
    /// .GetConfiguredConnectionString hands it to WebAppDeploymentService,
    /// ServiceAppDeploymentService and HostAgentSelfUpgradeService, which write it into every
    /// deployed application's appsettings.json. Applying HostAgent's timeouts here would
    /// rewrite the connection string of every web app and service app on the host, and would
    /// look to the Web.Shared factory like an operator choice that suppresses its own
    /// defaults. Sabotage-checked: routing GetConnectionString through ApplyDefaults makes
    /// this test fail.
    /// </remarks>
    [Fact]
    public void GetConnectionString_ReturnsTheConfiguredStringVerbatim()
    {
        Assert.Equal(BareConnectionString, CreateFactory(BareConnectionString).GetConnectionString());
    }

    [Fact]
    public void GetConnectionString_WhenNotConfigured_Throws()
    {
        var factory = new SqlConnectionFactory(new ConfigurationBuilder().Build());

        Assert.Throws<InvalidOperationException>(() => factory.GetConnectionString());
    }

    /// <summary>
    /// The effective string is cached, so repeated calls must stay identical rather than
    /// racing two threads onto different values.
    /// </summary>
    [Fact]
    public void Create_IsStableAcrossCalls()
    {
        var factory = CreateFactory(BareConnectionString);

        using var first = factory.Create();
        using var second = factory.Create();

        Assert.Equal(first.ConnectionString, second.ConnectionString);
        Assert.Equal(5, second.ConnectionTimeout);
        Assert.Equal(30, second.CommandTimeout);
    }
}

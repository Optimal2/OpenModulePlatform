using Microsoft.Extensions.Configuration;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// The Web.Shared connection factory's timeouts (R12-E7).
/// </summary>
/// <remarks>
/// The fix landed without tests, so nothing stopped a later edit from putting the 15-second
/// driver default back or from lowering the command timeout far enough to abort the Portal's
/// artifact import. The same assertions exist for HostAgent's own copy of this factory in
/// OpenModulePlatform.HostAgent.Runtime.Tests; if the two ever disagree, one of them was
/// changed alone.
/// </remarks>
public sealed class WebSharedSqlConnectionFactoryTests
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
    /// Measured before the fix: 15 seconds to render an error page while SQL Server was
    /// unreachable, with a thread held for each concurrent request.
    /// </summary>
    [Fact]
    public void Create_AppliesTheConnectTimeout_WhenTheConnectionStringDoesNotStateOne()
    {
        using var connection = CreateFactory(BareConnectionString).Create();

        Assert.Equal(5, connection.ConnectionTimeout);
    }

    /// <summary>
    /// Stated explicitly rather than inherited, and deliberately left at the driver's own
    /// value: the Portal's artifact import and maintenance repositories share this factory
    /// with page rendering, so a shorter blanket limit would abort administrative work that is
    /// legitimately slow.
    /// </summary>
    [Fact]
    public void Create_AppliesTheCommandTimeout_WhenTheConnectionStringDoesNotStateOne()
    {
        using var connection = CreateFactory(BareConnectionString).Create();

        Assert.Equal(30, connection.CommandTimeout);
    }

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
    /// The per-database overload builds its connection string separately, so it has to apply
    /// the same defaults rather than inheriting them by accident.
    /// </summary>
    [Fact]
    public void CreateForDatabase_AppliesTheSameTimeouts()
    {
        using var connection = CreateFactory(BareConnectionString).CreateForDatabase("SomeOtherDatabase");

        Assert.Equal("SomeOtherDatabase", connection.Database);
        Assert.Equal(5, connection.ConnectionTimeout);
        Assert.Equal(30, connection.CommandTimeout);
    }

    [Fact]
    public void CreateForDatabase_WithBlankName_FallsBackToTheConfiguredDatabase()
    {
        using var connection = CreateFactory(BareConnectionString).CreateForDatabase("   ");

        Assert.Equal("OpenModulePlatform", connection.Database);
        Assert.Equal(5, connection.ConnectionTimeout);
    }

    [Fact]
    public void Create_WhenConnectionStringIsNotConfigured_Throws()
    {
        var factory = new SqlConnectionFactory(new ConfigurationBuilder().Build());

        Assert.Throws<InvalidOperationException>(() => factory.Create());
    }
}

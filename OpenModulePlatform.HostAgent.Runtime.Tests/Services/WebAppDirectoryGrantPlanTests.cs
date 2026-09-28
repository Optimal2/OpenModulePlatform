using Microsoft.Extensions.Logging;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class WebAppDirectoryGrantPlanTests
{
    private static readonly HostAgentIisAppPoolIdentitySettings AppPoolIdentity = new();

    [Fact]
    public void ChildApplication_GrantsItsAppPoolModifyOnLogsDirectoryOnly()
    {
        var siteRoot = Path.Join("C:", "omp", "portal");
        var target = Path.Join("C:", "omp", "webapps", "example");

        var grants = WebAppDeploymentService.PlanChildApplicationDirectoryGrants(
            siteRoot,
            "OpenModulePlatform",
            AppPoolIdentity,
            target,
            "OMP_Example",
            AppPoolIdentity);

        Assert.Collection(
            grants,
            root =>
            {
                Assert.Equal(siteRoot, root.Path);
                Assert.Equal([@"IIS AppPool\OpenModulePlatform"], root.AccountNames);
                Assert.Equal("M", root.Permission);
                Assert.True(root.Required);
            },
            logs =>
            {
                Assert.Equal(Path.Join(target, "logs"), logs.Path);
                Assert.Equal("OMP_Example", logs.AppPoolName);
                Assert.Equal([@"IIS AppPool\OMP_Example"], logs.AccountNames);
                Assert.Equal("M", logs.Permission);
                Assert.False(logs.Required);
            });
        Assert.DoesNotContain(grants, grant => string.Equals(grant.Path, target, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RootApplication_KeepsSiteRootModifyAndAddsLogsDirectory()
    {
        var target = Path.Join("C:", "omp", "portal");

        var grants = WebAppDeploymentService.PlanRootApplicationDirectoryGrants(
            target,
            "OpenModulePlatform",
            AppPoolIdentity);

        Assert.Collection(
            grants,
            root =>
            {
                Assert.Equal(target, root.Path);
                Assert.True(root.Required);
            },
            logs =>
            {
                Assert.Equal(Path.Join(target, "logs"), logs.Path);
                Assert.Equal([@"IIS AppPool\OpenModulePlatform"], logs.AccountNames);
                Assert.Equal("M", logs.Permission);
                Assert.False(logs.Required);
            });
    }

    [Theory]
    [InlineData(@"contoso\svc-example")]
    [InlineData(@"CONTOSO\svc-example")]
    public void ChildApplication_WithSpecificUserIdentity_TriesDistinctAccountsBeforeVirtualAccount(string userName)
    {
        var grants = WebAppDeploymentService.PlanChildApplicationDirectoryGrants(
            Path.Join("C:", "omp", "portal"),
            "OpenModulePlatform",
            AppPoolIdentity,
            Path.Join("C:", "omp", "webapps", "example"),
            "OMP_Example",
            new HostAgentIisAppPoolIdentitySettings { UserName = userName });

        Assert.Equal(
            [@"CONTOSO\svc-example", @"IIS AppPool\OMP_Example"],
            grants[1].AccountNames);
    }

    [Fact]
    public void ChildApplication_WithUpnIdentity_KeepsDistinctFallbackAccount()
    {
        var grants = WebAppDeploymentService.PlanChildApplicationDirectoryGrants(
            Path.Join("C:", "omp", "portal"), "OpenModulePlatform", AppPoolIdentity,
            Path.Join("C:", "omp", "webapps", "example"), "OMP_Example",
            new HostAgentIisAppPoolIdentitySettings { UserName = " svc-example@contoso.example " });

        Assert.Equal(
            [@"CONTOSO\svc-example", "svc-example@contoso.example", @"IIS AppPool\OMP_Example"],
            grants[1].AccountNames);
    }

    [Fact]
    public void LogDirectoryGrantTimeout_LogsWarningWithoutThrowing()
    {
        var tempRoot = Directory.CreateTempSubdirectory("omp-logs-timeout-");
        try
        {
            var logsPath = Path.Join(tempRoot.FullName, "logs");
            var logger = new CapturingLogger();
            var calls = 0;

            WebAppDeploymentService.TryEnsureAppPoolDirectoryGrant(
                new AppPoolDirectoryGrant(logsPath, "OMP_Example", [@"IIS AppPool\OMP_Example"], "M", Required: false),
                logger,
                (fileName, arguments) =>
                {
                    calls++;
                    Assert.Equal("icacls.exe", fileName);
                    Assert.Equal([logsPath, "/grant", @"IIS AppPool\OMP_Example:(OI)(CI)(M)", "/L"], arguments);
                    throw new TimeoutException("Simulated account lookup timeout.");
                });

            Assert.Equal(1, calls);
            Assert.True(Directory.Exists(logsPath));
            var entry = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Contains(logsPath, entry.Message);
            Assert.Contains(@"IIS AppPool\OMP_Example", entry.Message);
            Assert.Contains("Simulated account lookup timeout.", entry.Message);
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(0, LogLevel.Debug)]
    [InlineData(5, LogLevel.Warning)]
    public void OrdinaryLogDirectory_InvokesGrantAndReportsResult(int exitCode, LogLevel expectedLevel)
    {
        var tempRoot = Directory.CreateTempSubdirectory("omp-logs-result-");
        try
        {
            var logsPath = Path.Join(tempRoot.FullName, "logs");
            var logger = new CapturingLogger();
            var calls = 0;
            WebAppDeploymentService.TryEnsureAppPoolDirectoryGrant(
                new AppPoolDirectoryGrant(logsPath, "OMP_Example", [@"IIS AppPool\OMP_Example"], "M", Required: false),
                logger,
                (fileName, arguments) =>
                {
                    calls++;
                    Assert.True(Directory.Exists(logsPath));
                    Assert.Equal("icacls.exe", fileName);
                    Assert.Equal([logsPath, "/grant", @"IIS AppPool\OMP_Example:(OI)(CI)(M)", "/L"], arguments);
                    return new HostAgentProcessResult(exitCode, string.Empty, "Simulated access denial.");
                });

            Assert.Equal(1, calls);
            var entry = Assert.Single(logger.Entries);
            Assert.Equal(expectedLevel, entry.Level);
            Assert.Contains(logsPath, entry.Message);
            if (exitCode != 0)
            {
                Assert.Contains("Simulated access denial.", entry.Message);
            }
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [SkippableFact]
    public void LogDirectoryLink_LogsWarningWithoutInvokingGrant()
    {
        var tempRoot = Directory.CreateTempSubdirectory("omp-logs-link-");
        var logsPath = Path.Join(tempRoot.FullName, "logs");
        var linkCreated = false;
        try
        {
            var target = Directory.CreateDirectory(Path.Join(tempRoot.FullName, "target"));
            try
            {
                Directory.CreateSymbolicLink(logsPath, target.FullName);
                linkCreated = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Skip.If(true, $"Cannot create a directory link: {ex.Message}");
            }

            var logger = new CapturingLogger();
            var calls = 0;
            WebAppDeploymentService.TryEnsureAppPoolDirectoryGrant(
                new AppPoolDirectoryGrant(logsPath, "OMP_Example", [@"IIS AppPool\OMP_Example"], "M", Required: false),
                logger,
                (_, _) =>
                {
                    calls++;
                    return new HostAgentProcessResult(0, string.Empty, string.Empty);
                });

            Assert.Equal(0, calls);
            var entry = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Contains("reparse point", entry.Message);
            Assert.Contains(logsPath, entry.Message);
            Assert.Contains(@"IIS AppPool\OMP_Example", entry.Message);
            Assert.True(Directory.Exists(target.FullName));
        }
        finally
        {
            if (linkCreated)
            {
                Directory.Delete(logsPath);
            }

            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void LogDirectoryGrantFailure_LogsWarningWithPathAndIdentity()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "omp-logs-grant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            // A file where the application directory should be makes the logs directory
            // impossible to create, which is the failure the warning must surface.
            var blockingFile = Path.Join(tempRoot, "example");
            File.WriteAllText(blockingFile, string.Empty);
            var logsPath = Path.Join(blockingFile, "logs");
            var logger = new CapturingLogger();

            WebAppDeploymentService.TryEnsureAppPoolDirectoryGrant(
                new AppPoolDirectoryGrant(logsPath, "OMP_Example", [@"IIS AppPool\OMP_Example"], "M", Required: false),
                logger);

            var entry = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Contains(logsPath, entry.Message);
            Assert.Contains(@"IIS AppPool\OMP_Example", entry.Message);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}

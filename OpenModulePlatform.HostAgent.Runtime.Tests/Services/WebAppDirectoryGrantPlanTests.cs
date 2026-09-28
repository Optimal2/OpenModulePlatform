using System.Diagnostics;
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
            AppPoolIdentity,
            Path.Join("C:", "omp", "webapps"));

        Assert.Collection(
            grants,
            root =>
            {
                Assert.Equal(siteRoot, root.Path);
                Assert.Equal([@"IIS AppPool\OpenModulePlatform"], root.AccountNames);
                Assert.Equal("M", root.Permission);
                Assert.True(root.Required);
                Assert.Equal(Path.GetFullPath(siteRoot), root.RootPath);
            },
            logs =>
            {
                Assert.Equal(Path.Join(target, "logs"), logs.Path);
                Assert.Equal("OMP_Example", logs.AppPoolName);
                Assert.Equal([@"IIS AppPool\OMP_Example"], logs.AccountNames);
                Assert.Equal("M", logs.Permission);
                Assert.False(logs.Required);
                Assert.Equal(Path.GetFullPath(Path.Join("C:", "omp", "webapps")), logs.RootPath);
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
            AppPoolIdentity,
            Path.Join("C:", "omp", "webapps"));

        Assert.Collection(
            grants,
            root =>
            {
                Assert.Equal(target, root.Path);
                Assert.True(root.Required);
                Assert.Equal(Path.GetFullPath(target), root.RootPath);
            },
            logs =>
            {
                Assert.Equal(Path.Join(target, "logs"), logs.Path);
                Assert.Equal([@"IIS AppPool\OpenModulePlatform"], logs.AccountNames);
                Assert.Equal("M", logs.Permission);
                Assert.False(logs.Required);
                Assert.Equal(Path.GetFullPath(target), logs.RootPath);
            });
    }

    [Theory]
    [InlineData("webapps", "webapps/example", "webapps")]
    [InlineData("webapps/", "webapps/example", "webapps")]
    [InlineData("webapps", "webapps", "webapps")]
    [InlineData("webapps", "webapps-other/example", "webapps-other/example")]
    [InlineData("webapps", "portal", "portal")]
    [InlineData("", "portal", "portal")]
    public void RootApplication_SelectsConfiguredBoundaryOrExplicitApplicationRoot(
        string configuredRoot, string applicationPath, string expectedRoot)
    {
        var basePath = Path.GetFullPath(Path.GetTempPath());
        var grants = WebAppDeploymentService.PlanRootApplicationDirectoryGrants(
            Path.Join(basePath, applicationPath), "OMP_Example", AppPoolIdentity,
            configuredRoot.Length == 0 ? string.Empty : Path.Join(basePath, configuredRoot));

        Assert.All(grants, grant => Assert.Equal(Path.GetFullPath(Path.Join(basePath, expectedRoot)), grant.RootPath));
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
            new HostAgentIisAppPoolIdentitySettings { UserName = userName },
            Path.Join("C:", "omp", "webapps"));

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
            new HostAgentIisAppPoolIdentitySettings { UserName = " svc-example@contoso.example " },
            Path.Join("C:", "omp", "webapps"));

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
                new AppPoolDirectoryGrant(logsPath, "OMP_Example", [@"IIS AppPool\OMP_Example"], "M", Required: false, tempRoot.FullName),
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
                new AppPoolDirectoryGrant(logsPath, "OMP_Example", [@"IIS AppPool\OMP_Example"], "M", Required: false, tempRoot.FullName),
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
                new AppPoolDirectoryGrant(logsPath, "OMP_Example", [@"IIS AppPool\OMP_Example"], "M", Required: false, tempRoot.FullName),
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
                new AppPoolDirectoryGrant(logsPath, "OMP_Example", [@"IIS AppPool\OMP_Example"], "M", Required: false, tempRoot),
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

    [SkippableTheory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void DirectoryGrant_RejectsJunctionInPath(bool required, bool linkIntermediateDirectory, bool directoryExists)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "NTFS junctions require Windows.");
        var tempRoot = Directory.CreateTempSubdirectory("omp-grant-junction-");
        var webAppsRoot = Path.Join(tempRoot.FullName, "webapps");
        var linkPath = linkIntermediateDirectory ? Path.Join(webAppsRoot, "group") : Path.Join(webAppsRoot, "group", "example");
        var targetPath = Path.Join(tempRoot.FullName, "target");
        var grantPath = Path.Join(webAppsRoot, "group", "example", required ? "content" : "logs");
        var redirectedPath = linkIntermediateDirectory
            ? Path.Join(targetPath, "example", required ? "content" : "logs")
            : Path.Join(targetPath, required ? "content" : "logs");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            Directory.CreateDirectory(targetPath);
            if (directoryExists)
            {
                Directory.CreateDirectory(redirectedPath);
            }

            CreateJunction(linkPath, targetPath);
            var grant = new AppPoolDirectoryGrant(
                grantPath, "OMP_Example", [@"IIS AppPool\OMP_Example"], "M", Required: required, webAppsRoot);
            var logger = new CapturingLogger();
            var calls = 0;
            HostAgentProcessResult RunProcess(string fileName, IReadOnlyList<string> arguments)
            {
                calls++;
                return new HostAgentProcessResult(0, string.Empty, string.Empty);
            }

            if (required)
            {
                var error = Assert.Throws<IOException>(() =>
                    WebAppDeploymentService.EnsureRequiredAppPoolDirectoryGrant(grant, logger, RunProcess));
                Assert.Contains("reparse point", error.Message);
                Assert.Contains(linkPath, error.Message);
                Assert.Empty(logger.Entries);
            }
            else
            {
                WebAppDeploymentService.TryEnsureAppPoolDirectoryGrant(grant, logger, RunProcess);
                var entry = Assert.Single(logger.Entries);
                Assert.Equal(LogLevel.Warning, entry.Level);
                Assert.Contains("reparse point", entry.Message);
                Assert.Contains(linkPath, entry.Message);
                Assert.Contains(grantPath, entry.Message);
                Assert.Contains(@"IIS AppPool\OMP_Example", entry.Message);
                Assert.Contains("file logging may fail silently", entry.Message);
            }

            Assert.Equal(0, calls);
            Assert.Equal(directoryExists, Directory.Exists(redirectedPath));
        }
        finally
        {
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath);
            }

            tempRoot.Delete(recursive: true);
        }
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DirectoryGrants_AllowJunctionAtOrAboveConfiguredRoot(bool junctionAtRoot, bool directoryExists)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "NTFS junctions require Windows.");
        var tempRoot = Directory.CreateTempSubdirectory("omp-grant-ancestor-");
        var linkPath = Path.Join(tempRoot.FullName, "linked-parent");
        var targetPath = Path.Join(tempRoot.FullName, "target");
        try
        {
            Directory.CreateDirectory(targetPath);
            CreateJunction(linkPath, targetPath);
            var webAppsRoot = junctionAtRoot ? linkPath : Path.Join(linkPath, "webapps");
            Directory.CreateDirectory(webAppsRoot);
            var applicationPath = Path.Join(webAppsRoot, "example");
            if (directoryExists)
            {
                Directory.CreateDirectory(Path.Join(applicationPath, "logs"));
            }

            var grants = WebAppDeploymentService.PlanRootApplicationDirectoryGrants(
                applicationPath, "OMP_Example", AppPoolIdentity, webAppsRoot);
            var logger = new CapturingLogger();
            var calls = new List<string>();
            HostAgentProcessResult RunProcess(string fileName, IReadOnlyList<string> arguments)
            {
                Assert.Equal("icacls.exe", fileName);
                Assert.True(Directory.Exists(arguments[0]));
                Assert.Equal([arguments[0], "/grant", @"IIS AppPool\OMP_Example:(OI)(CI)(M)", "/L"], arguments);
                calls.Add(arguments[0]);
                return new HostAgentProcessResult(0, string.Empty, string.Empty);
            }

            WebAppDeploymentService.EnsureRequiredAppPoolDirectoryGrant(grants[0], logger, RunProcess);
            WebAppDeploymentService.TryEnsureAppPoolDirectoryGrant(grants[1], logger, RunProcess);

            Assert.Equal([applicationPath, Path.Join(applicationPath, "logs")], calls);
            Assert.Equal(2, logger.Entries.Count);
            Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Debug, entry.Level));
            var physicalRoot = junctionAtRoot ? targetPath : Path.Join(targetPath, "webapps");
            Assert.True(Directory.Exists(Path.Join(physicalRoot, "example", "logs")));
        }
        finally
        {
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath);
            }

            tempRoot.Delete(recursive: true);
        }
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void DirectoryGrant_RejectsJunctionAtGrantDirectory(bool required)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "NTFS junctions require Windows.");
        var tempRoot = Directory.CreateTempSubdirectory("omp-grant-leaf-");
        var webAppsRoot = Path.Join(tempRoot.FullName, "webapps");
        var applicationPath = Path.Join(webAppsRoot, "example");
        var linkPath = required ? applicationPath : Path.Join(applicationPath, "logs");
        var targetPath = Path.Join(tempRoot.FullName, "target");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            Directory.CreateDirectory(targetPath);
            CreateJunction(linkPath, targetPath);
            var grants = WebAppDeploymentService.PlanRootApplicationDirectoryGrants(
                applicationPath, "OMP_Example", AppPoolIdentity, webAppsRoot);
            var logger = new CapturingLogger();
            var calls = 0;
            HostAgentProcessResult RunProcess(string fileName, IReadOnlyList<string> arguments)
            {
                calls++;
                return new HostAgentProcessResult(0, string.Empty, string.Empty);
            }

            if (required)
            {
                var error = Assert.Throws<IOException>(() =>
                    WebAppDeploymentService.EnsureRequiredAppPoolDirectoryGrant(grants[0], logger, RunProcess));
                Assert.Contains("reparse point", error.Message);
                Assert.Contains(linkPath, error.Message);
            }
            else
            {
                WebAppDeploymentService.TryEnsureAppPoolDirectoryGrant(grants[1], logger, RunProcess);
                var entry = Assert.Single(logger.Entries);
                Assert.Equal(LogLevel.Warning, entry.Level);
                Assert.Contains("reparse point", entry.Message);
                Assert.Contains(linkPath, entry.Message);
            }

            Assert.Equal(0, calls);
            Assert.Empty(Directory.EnumerateFileSystemEntries(targetPath));
        }
        finally
        {
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath);
            }

            tempRoot.Delete(recursive: true);
        }
    }

    private static void CreateJunction(string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "/c", "mklink", "/J", linkPath, targetPath })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        try
        {
            Assert.True(process.WaitForExit(10_000), "Junction creation timed out.");
            Assert.Equal(0, process.ExitCode);
            Assert.True(File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint));
        }
        finally
        {
            // Stop creation before the caller's finally removes the link, even on timeout.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
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

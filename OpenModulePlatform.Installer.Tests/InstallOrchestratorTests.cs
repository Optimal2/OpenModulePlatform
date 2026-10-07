using OpenModulePlatform.Installation;
using OpenModulePlatform.Installer.Core;
using OpenModulePlatform.Installer.Install;

namespace OpenModulePlatform.Installer.Tests;

/// <summary>Records every call; the dry-run assertions verify it stays silent.</summary>
internal sealed class RecordingInstallActions : IInstallActions
{
    public List<string> Calls { get; } = [];

    public bool PasswordValid { get; set; } = true;

    public bool LogonRightGranted { get; set; } = true;

    public int HostingBundleExitCode { get; set; }

    public bool HostAgentActive { get; set; } = true;

    public (bool Ok, string Detail) PortalResult { get; set; } = (true, "ok");

    public void InstallIisFeatures(IReadOnlyList<string> featureNames, bool isServerOs)
        => Calls.Add("features:" + string.Join(",", featureNames));

    public int RunHostingBundle(string installerPath, bool repair)
    {
        Calls.Add("bundle:" + (repair ? "repair" : "install") + ":" + Path.GetFileName(installerPath));
        return HostingBundleExitCode;
    }

    public void RestartIis() => Calls.Add("iisreset");

    public bool EnsureServiceLogonRight(string accountName, out string detail)
    {
        Calls.Add("logonright:" + accountName);
        detail = LogonRightGranted ? "granted" : "denied";
        return LogonRightGranted;
    }

    public bool ValidateAccountPassword(string userName, string domain, string password, out string error)
    {
        Calls.Add("logonuser:" + (string.IsNullOrWhiteSpace(domain) ? userName : domain + "\\" + userName));
        error = PasswordValid ? string.Empty : "bad credentials";
        return PasswordValid;
    }

    public Task<bool> WaitForHostAgentActiveAsync(string server, string database, bool trustServerCertificate, string hostKey, string serviceName, DateTimeOffset notBefore, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Add("waithostagent:" + serviceName);
        return Task.FromResult(HostAgentActive);
    }

    public Task<(bool Ok, string Detail)> ProbePortalAsync(string portalUrl, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Add("portal:" + portalUrl);
        return Task.FromResult(PortalResult);
    }
}

internal sealed class ListInstallProgress : IInstallProgress
{
    public List<string> Lines { get; } = [];

    public void Info(string message) => Lines.Add(message);

    public void Error(string message) => Lines.Add("ERROR: " + message);
}

public class InstallOrchestratorTests
{
    /// <summary>A config whose shared install chain touches nothing external (no SQL, no HostAgent, an empty temp artifact store).</summary>
    private static BootstrapConfig NoOpChainConfig(string? serviceAccount = null)
        => new()
        {
            Sql = new SqlBootstrapOptions { Enabled = false },
            ArtifactStoreRoot = Path.Join(Path.GetTempPath(), "omp-installer-tests", Guid.NewGuid().ToString("N")),
            HostAgent = new HostAgentInstallOptions
            {
                Enabled = false,
                ServiceAccountName = serviceAccount ?? string.Empty
            }
        };

    private static PrerequisiteEvaluation GreenEvaluation(
        ServiceAccountInfo account,
        IReadOnlyList<string>? missingFeatures = null,
        bool bundleRequired = false,
        bool bundleRepair = false,
        string? bundlePath = null)
        => new(
            [],
            IsServerOs: true,
            missingFeatures ?? [],
            bundleRequired,
            bundleRepair,
            bundlePath,
            RequiredRuntimeMajor: 10,
            account);

    private static InstallRequest Request(
        BootstrapConfig config,
        PrerequisiteEvaluation evaluation,
        string password = "s3cret")
        => new(config, @"C:\pkg\hosts\prod\bootstrap.json", @"C:\pkg", evaluation, password);

    [Fact]
    public async Task Dry_run_never_calls_any_mutating_action()
    {
        var account = ServiceAccountClassifier.Classify(@"CONTOSO\svc-omp", "SERVER01");
        var evaluation = GreenEvaluation(
            account,
            missingFeatures: ["Web-Server"],
            bundleRequired: true,
            bundlePath: @"C:\pkg\prereqs\dotnet-hosting-10.0.3-win.exe");
        var actions = new RecordingInstallActions();
        var progress = new ListInstallProgress();

        await InstallOrchestrator.RunAsync(
            Request(NoOpChainConfig(@"CONTOSO\svc-omp"), evaluation),
            actions,
            progress,
            dryRun: true);

        Assert.Empty(actions.Calls);
        Assert.Contains(progress.Lines, line => line.Contains("Would install IIS features"));
        Assert.Contains(progress.Lines, line => line.Contains("Log on as a service"));
        Assert.Contains(progress.Lines, line => line.Contains("dotnet-hosting-10.0.3-win.exe"));
    }

    [Fact]
    public async Task Domain_account_gets_logon_right_before_anything_else_changes()
    {
        var account = ServiceAccountClassifier.Classify(@"CONTOSO\svc-omp", "SERVER01");
        var evaluation = GreenEvaluation(account, missingFeatures: ["Web-Server"]);
        var actions = new RecordingInstallActions();

        await InstallOrchestrator.RunAsync(
            Request(NoOpChainConfig(@"CONTOSO\svc-omp"), evaluation),
            actions,
            new ListInstallProgress(),
            dryRun: false);

        Assert.Equal(
            ["logonuser:CONTOSO\\svc-omp", "logonright:CONTOSO\\svc-omp", "features:Web-Server", "iisreset", "portal:http://localhost/"],
            actions.Calls);
    }

    [Fact]
    public async Task Built_in_account_skips_password_and_logon_right()
    {
        var account = ServiceAccountClassifier.Classify("NetworkService", "SERVER01");
        var actions = new RecordingInstallActions();

        await InstallOrchestrator.RunAsync(
            Request(NoOpChainConfig("NetworkService"), GreenEvaluation(account)),
            actions,
            new ListInstallProgress(),
            dryRun: false);

        Assert.DoesNotContain(actions.Calls, call => call.StartsWith("logonuser", StringComparison.Ordinal));
        Assert.DoesNotContain(actions.Calls, call => call.StartsWith("logonright", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hosting_bundle_runs_after_iis_features_and_before_iisreset()
    {
        var account = ServiceAccountClassifier.Classify(null, "SERVER01");
        var evaluation = GreenEvaluation(
            account,
            missingFeatures: ["Web-Server"],
            bundleRequired: true,
            bundlePath: @"C:\pkg\prereqs\dotnet-hosting-10.0.3-win.exe");
        var actions = new RecordingInstallActions();

        await InstallOrchestrator.RunAsync(
            Request(NoOpChainConfig(), evaluation),
            actions,
            new ListInstallProgress(),
            dryRun: false);

        Assert.Equal(
            ["features:Web-Server", "bundle:install:dotnet-hosting-10.0.3-win.exe", "iisreset", "portal:http://localhost/"],
            actions.Calls);
    }

    [Fact]
    public async Task Module_repair_uses_the_repair_flag()
    {
        var account = ServiceAccountClassifier.Classify(null, "SERVER01");
        var evaluation = GreenEvaluation(
            account,
            bundleRequired: true,
            bundleRepair: true,
            bundlePath: @"C:\pkg\prereqs\dotnet-hosting-10.0.3-win.exe");
        var actions = new RecordingInstallActions();

        await InstallOrchestrator.RunAsync(
            Request(NoOpChainConfig(), evaluation),
            actions,
            new ListInstallProgress(),
            dryRun: false);

        Assert.Contains("bundle:repair:dotnet-hosting-10.0.3-win.exe", actions.Calls);
    }

    [Fact]
    public async Task Restart_required_exit_code_3010_is_reported_not_a_failure()
    {
        var account = ServiceAccountClassifier.Classify(null, "SERVER01");
        var evaluation = GreenEvaluation(
            account,
            bundleRequired: true,
            bundlePath: @"C:\pkg\prereqs\dotnet-hosting-10.0.3-win.exe");
        var actions = new RecordingInstallActions { HostingBundleExitCode = 3010 };
        var progress = new ListInstallProgress();

        await InstallOrchestrator.RunAsync(
            Request(NoOpChainConfig(), evaluation),
            actions,
            progress,
            dryRun: false);

        Assert.Contains(progress.Lines, line => line.Contains("3010"));
        Assert.Contains(progress.Lines, line => line.Contains("Installation completed."));
    }

    [Fact]
    public async Task Bundle_failure_exit_code_fails_the_install()
    {
        var account = ServiceAccountClassifier.Classify(null, "SERVER01");
        var evaluation = GreenEvaluation(
            account,
            bundleRequired: true,
            bundlePath: @"C:\pkg\prereqs\dotnet-hosting-10.0.3-win.exe");
        var actions = new RecordingInstallActions { HostingBundleExitCode = 1603 };

        await Assert.ThrowsAsync<InvalidOperationException>(() => InstallOrchestrator.RunAsync(
            Request(NoOpChainConfig(), evaluation),
            actions,
            new ListInstallProgress(),
            dryRun: false));
        Assert.DoesNotContain(actions.Calls, call => call == "iisreset");
    }

    [Fact]
    public async Task Rejected_password_stops_before_any_mutation()
    {
        var account = ServiceAccountClassifier.Classify(@"CONTOSO\svc-omp", "SERVER01");
        var actions = new RecordingInstallActions { PasswordValid = false };

        await Assert.ThrowsAsync<InvalidOperationException>(() => InstallOrchestrator.RunAsync(
            Request(NoOpChainConfig(@"CONTOSO\svc-omp"), GreenEvaluation(account)),
            actions,
            new ListInstallProgress(),
            dryRun: false));

        Assert.Equal(["logonuser:CONTOSO\\svc-omp"], actions.Calls);
    }

    [Fact]
    public async Task Failed_logon_right_grant_stops_the_install()
    {
        var account = ServiceAccountClassifier.Classify(@"CONTOSO\svc-omp", "SERVER01");
        var actions = new RecordingInstallActions { LogonRightGranted = false };

        await Assert.ThrowsAsync<InvalidOperationException>(() => InstallOrchestrator.RunAsync(
            Request(NoOpChainConfig(@"CONTOSO\svc-omp"), GreenEvaluation(account)),
            actions,
            new ListInstallProgress(),
            dryRun: false));
    }

    [Fact]
    public async Task Portal_probe_failure_fails_the_install()
    {
        var account = ServiceAccountClassifier.Classify(null, "SERVER01");
        var actions = new RecordingInstallActions { PortalResult = (false, "connection refused") };

        await Assert.ThrowsAsync<InvalidOperationException>(() => InstallOrchestrator.RunAsync(
            Request(NoOpChainConfig(), GreenEvaluation(account)),
            actions,
            new ListInstallProgress(),
            dryRun: false));
    }

    [Fact]
    public async Task App_pool_password_fallback_is_applied_before_the_chain_runs()
    {
        var account = ServiceAccountClassifier.Classify(@"CONTOSO\svc-omp", "SERVER01");
        var config = NoOpChainConfig(@"CONTOSO\svc-omp");
        config.HostAgent.IisAppPoolUserName = @"CONTOSO\svc-omp";
        var actions = new RecordingInstallActions();

        await InstallOrchestrator.RunAsync(
            Request(config, GreenEvaluation(account)),
            actions,
            new ListInstallProgress(),
            dryRun: false);

        Assert.Equal("s3cret", config.HostAgent.ServiceAccountPassword);
        Assert.Equal("s3cret", config.HostAgent.IisAppPoolPassword);
    }
}

public class CliParsingTests
{
    [Fact]
    public void Machine_name_without_dry_run_is_rejected()
    {
        var parse = () => CliOptions.Parse(["--machine-name", "SERVER01"]);
        var ex = Assert.Throws<InvalidOperationException>(parse);
        Assert.Contains("--dry-run", ex.Message);
    }

    [Fact]
    public void Machine_name_with_dry_run_is_accepted()
    {
        var options = CliOptions.Parse(["--dry-run", "--machine-name", "SERVER01"]);
        Assert.True(options.DryRun);
        Assert.Equal("SERVER01", options.MachineName);
    }

    [Fact]
    public void Unknown_argument_is_rejected()
        => Assert.Throws<InvalidOperationException>(() => CliOptions.Parse(["--frobnicate"]));

    [Fact]
    public void Machine_name_override_includes_the_short_name()
    {
        var names = Program.InstallerProfileResolverCandidateNames("server01.example.com");
        Assert.Contains("server01.example.com", names);
        Assert.Contains("server01", names);
    }
}

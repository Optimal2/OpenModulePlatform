using OpenModulePlatform.Installer.Core;

namespace OpenModulePlatform.Installer.Tests;

/// <summary>A fully fake machine for prerequisite evaluation tests.</summary>
internal sealed class FakePrerequisiteEnvironment : IPrerequisiteEnvironment
{
    public bool IsServerOs { get; set; } = true;

    public string WindowsEditionName { get; set; } = "Windows Server 2022 Standard";

    public string MachineName { get; set; } = "SERVER01";

    public Dictionary<string, bool?> FeatureStates { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<int> AspNetCoreMajors { get; } = [10];

    public bool IisModuleRegistered { get; set; } = true;

    public bool IisModuleDllExists { get; set; } = true;

    public (bool Ok, string Detail) SqlProbeResult { get; set; } = (true, "connected");

    public (bool Found, bool HasPrivateKey)? CertificateProbeResult { get; set; } = (true, true);

    public string? LastCertificateThumbprint { get; private set; }

    public long FreeDisk { get; set; } = 100L * 1024 * 1024 * 1024;

    public List<string> HostingBundles { get; } = [];

    public List<string> RuntimeConfigTexts { get; } = [];

    public bool? IsFeatureInstalled(string featureName)
        => FeatureStates.TryGetValue(featureName, out var state) ? state : true;

    public IReadOnlyList<int> GetInstalledAspNetCoreRuntimeMajors() => AspNetCoreMajors;

    public bool IsAspNetCoreIisModuleRegistered() => IisModuleRegistered;

    public bool AspNetCoreIisModuleDllExists() => IisModuleDllExists;

    public Task<(bool Ok, string Detail)> ProbeSqlDatabaseAsync(string server, string database, bool trustServerCertificate, CancellationToken cancellationToken)
        => Task.FromResult(SqlProbeResult);

    public (bool Found, bool HasPrivateKey)? ProbeLocalMachineCertificate(string thumbprint)
    {
        LastCertificateThumbprint = thumbprint;
        return CertificateProbeResult;
    }

    public long GetFreeDiskBytes(string pathOnTargetDrive) => FreeDisk;

    public IReadOnlyList<string> GetAvailableHostingBundleInstallers() => HostingBundles;

    public IReadOnlyList<string> FindRuntimeConfigTexts(string payloadRoot) => RuntimeConfigTexts;

    public string? TryReadAllText(string path) => null;

    public bool FileExists(string path) => false;

    public bool DirectoryExists(string path) => false;

    public static FakePrerequisiteEnvironment AllGreen()
    {
        var env = new FakePrerequisiteEnvironment();
        env.FeatureStates["Web-Server"] = true;
        env.FeatureStates["Web-Windows-Auth"] = true;
        env.FeatureStates["Web-WebSockets"] = true;
        return env;
    }
}

public class PrerequisiteEvaluatorTests
{
    private static OpenModulePlatform.Installation.BootstrapConfig MinimalConfig()
        => new()
        {
            Sql = new OpenModulePlatform.Installation.SqlBootstrapOptions
            {
                Server = "sql.example.com",
                Database = "OpenModulePlatform"
            },
            HostAgent = new OpenModulePlatform.Installation.HostAgentInstallOptions
            {
                InstallPath = @"D:\OMP\Services\HostAgent"
            }
        };

    [Fact]
    public async Task Green_machine_passes_every_check()
    {
        var evaluation = await PrerequisiteEvaluator.EvaluateAsync(
            MinimalConfig(),
            @"C:\pkg",
            FakePrerequisiteEnvironment.AllGreen(),
            serviceAccountPasswordValidated: false);

        Assert.All(evaluation.Checks, check => Assert.True(check.IsSatisfied, check.Title + ": " + check.Detail));
        Assert.Empty(evaluation.MissingIisFeatures);
        Assert.False(evaluation.HostingBundleRequired);
        Assert.True(evaluation.CanInstall);
    }

    [Fact]
    public async Task Missing_iis_is_listed_with_server_feature_names()
    {
        var env = FakePrerequisiteEnvironment.AllGreen();
        env.FeatureStates["Web-Server"] = false;

        var evaluation = await PrerequisiteEvaluator.EvaluateAsync(
            MinimalConfig(), @"C:\pkg", env, serviceAccountPasswordValidated: false);

        Assert.Contains("Web-Server", evaluation.MissingIisFeatures);
        var check = evaluation.Checks.Single(c => c.Id == PrerequisiteCheckId.IisWebServer);
        Assert.False(check.IsSatisfied);
        Assert.True(check.CanAutoFix);
        Assert.True(evaluation.CanInstall);
    }

    [Fact]
    public async Task Client_os_uses_client_feature_names()
    {
        var env = FakePrerequisiteEnvironment.AllGreen();
        env.IsServerOs = false;
        env.FeatureStates["IIS-WebServer"] = false;

        var evaluation = await PrerequisiteEvaluator.EvaluateAsync(
            MinimalConfig(), @"C:\pkg", env, serviceAccountPasswordValidated: false);

        Assert.Contains("IIS-WebServer", evaluation.MissingIisFeatures);
        Assert.False(evaluation.IsServerOs);
    }

    [Fact]
    public async Task Windows_authentication_is_checked_only_when_the_profile_uses_it()
    {
        var withoutAuth = await PrerequisiteEvaluator.EvaluateAsync(
            MinimalConfig(), @"C:\pkg", FakePrerequisiteEnvironment.AllGreen(), serviceAccountPasswordValidated: false);
        Assert.DoesNotContain(withoutAuth.Checks, c => c.Id == PrerequisiteCheckId.IisWindowsAuthentication);

        var config = MinimalConfig();
        config.Artifacts.Add(new OpenModulePlatform.Installation.ArtifactPayloadOptions
        {
            Source = @"data\global\artifacts\omp-auth-web-0.3.354.zip",
            Target = @"D:\OMP\WebApps\auth"
        });
        var env = FakePrerequisiteEnvironment.AllGreen();
        env.FeatureStates["Web-Windows-Auth"] = false;
        var withAuth = await PrerequisiteEvaluator.EvaluateAsync(
            config, @"C:\pkg", env, serviceAccountPasswordValidated: false);
        var check = withAuth.Checks.Single(c => c.Id == PrerequisiteCheckId.IisWindowsAuthentication);
        Assert.False(check.IsSatisfied);
        Assert.Contains("Web-Windows-Auth", withAuth.MissingIisFeatures);
    }

    [Fact]
    public async Task Missing_runtime_major_requires_the_hosting_bundle()
    {
        var env = FakePrerequisiteEnvironment.AllGreen();
        env.AspNetCoreMajors.Clear();
        env.AspNetCoreMajors.Add(8);
        env.RuntimeConfigTexts.Add(
            """{"runtimeOptions":{"framework":{"name":"Microsoft.AspNetCore.App","version":"10.0.1"}}}""");
        env.HostingBundles.Add(@"C:\pkg\prereqs\dotnet-hosting-10.0.3-win.exe");

        var evaluation = await PrerequisiteEvaluator.EvaluateAsync(
            MinimalConfig(), @"C:\pkg", env, serviceAccountPasswordValidated: false);

        Assert.Equal(10, evaluation.RequiredRuntimeMajor);
        Assert.False(evaluation.Checks.Single(c => c.Id == PrerequisiteCheckId.AspNetCoreRuntime).IsSatisfied);
        Assert.True(evaluation.HostingBundleRequired);
        Assert.False(evaluation.HostingBundleRepairRequired);
        Assert.Equal(@"C:\pkg\prereqs\dotnet-hosting-10.0.3-win.exe", evaluation.HostingBundleInstallerPath);
    }

    [Fact]
    public async Task A_newer_runtime_major_alone_does_not_satisfy_the_check()
    {
        // No roll-forward across major versions: only .NET 11 installed must
        // NOT satisfy artifacts built for .NET 10.
        var env = FakePrerequisiteEnvironment.AllGreen();
        env.AspNetCoreMajors.Clear();
        env.AspNetCoreMajors.Add(11);
        env.HostingBundles.Add(@"C:\pkg\prereqs\dotnet-hosting-10.0.3-win.exe");

        var evaluation = await PrerequisiteEvaluator.EvaluateAsync(
            MinimalConfig(), @"C:\pkg", env, serviceAccountPasswordValidated: false);

        Assert.Equal(10, evaluation.RequiredRuntimeMajor);
        var check = evaluation.Checks.Single(c => c.Id == PrerequisiteCheckId.AspNetCoreRuntime);
        Assert.False(check.IsSatisfied);
        Assert.True(evaluation.HostingBundleRequired);
    }

    [Fact]
    public async Task Runtime_present_but_iis_module_missing_means_repair()
    {
        var env = FakePrerequisiteEnvironment.AllGreen();
        env.IisModuleRegistered = false;
        env.HostingBundles.Add(@"C:\pkg\prereqs\dotnet-hosting-10.0.3-win.exe");

        var evaluation = await PrerequisiteEvaluator.EvaluateAsync(
            MinimalConfig(), @"C:\pkg", env, serviceAccountPasswordValidated: false);

        Assert.True(evaluation.HostingBundleRequired);
        Assert.True(evaluation.HostingBundleRepairRequired);
    }

    [Fact]
    public async Task Missing_bundle_installer_blocks_install()
    {
        var env = FakePrerequisiteEnvironment.AllGreen();
        env.AspNetCoreMajors.Clear();

        var evaluation = await PrerequisiteEvaluator.EvaluateAsync(
            MinimalConfig(), @"C:\pkg", env, serviceAccountPasswordValidated: false);

        var bundleCheck = evaluation.Checks.Single(c => c.Id == PrerequisiteCheckId.HostingBundleInstaller);
        Assert.False(bundleCheck.IsSatisfied);
        Assert.False(bundleCheck.CanAutoFix);
        Assert.False(evaluation.CanInstall);
    }

    [Fact]
    public async Task Https_binding_certificate_is_checked_and_blocks_when_missing()
    {
        var config = MinimalConfig();
        config.HostAgent.IisBindingProtocol = "https";
        config.HostAgent.IisBindingCertificateThumbprint = "AABBCCDDEEFF00112233445566778899AABBCCDD";

        var okEnv = FakePrerequisiteEnvironment.AllGreen();
        var ok = await PrerequisiteEvaluator.EvaluateAsync(
            config, @"C:\pkg", okEnv, serviceAccountPasswordValidated: false);
        var okCheck = ok.Checks.Single(c => c.Id == PrerequisiteCheckId.IisBindingCertificate);
        Assert.True(okCheck.IsSatisfied);
        Assert.Equal("AABBCCDDEEFF00112233445566778899AABBCCDD", okEnv.LastCertificateThumbprint);

        var missingEnv = FakePrerequisiteEnvironment.AllGreen();
        missingEnv.CertificateProbeResult = (false, false);
        var missing = await PrerequisiteEvaluator.EvaluateAsync(
            config, @"C:\pkg", missingEnv, serviceAccountPasswordValidated: false);
        var missingCheck = missing.Checks.Single(c => c.Id == PrerequisiteCheckId.IisBindingCertificate);
        Assert.False(missingCheck.IsSatisfied);
        Assert.False(missingCheck.CanAutoFix);
        Assert.False(missing.CanInstall);
    }

    [Fact]
    public async Task Https_binding_certificate_without_private_key_blocks_install()
    {
        var config = MinimalConfig();
        config.HostAgent.IisBindingProtocol = "https";
        config.HostAgent.IisBindingCertificateThumbprint = "AABBCCDDEEFF00112233445566778899AABBCCDD";
        var env = FakePrerequisiteEnvironment.AllGreen();
        env.CertificateProbeResult = (true, false);

        var evaluation = await PrerequisiteEvaluator.EvaluateAsync(
            config, @"C:\pkg", env, serviceAccountPasswordValidated: false);

        var check = evaluation.Checks.Single(c => c.Id == PrerequisiteCheckId.IisBindingCertificate);
        Assert.False(check.IsSatisfied);
        Assert.Contains("private key", check.Detail);
        Assert.False(evaluation.CanInstall);
    }

    [Fact]
    public async Task Unreadable_certificate_store_blocks_with_a_plain_message()
    {
        var config = MinimalConfig();
        config.HostAgent.IisBindingProtocol = "https";
        config.HostAgent.IisBindingCertificateThumbprint = "AABBCCDDEEFF00112233445566778899AABBCCDD";
        var env = FakePrerequisiteEnvironment.AllGreen();
        env.CertificateProbeResult = null;

        var evaluation = await PrerequisiteEvaluator.EvaluateAsync(
            config, @"C:\pkg", env, serviceAccountPasswordValidated: false);

        var check = evaluation.Checks.Single(c => c.Id == PrerequisiteCheckId.IisBindingCertificate);
        Assert.False(check.IsSatisfied);
        Assert.Contains("could not be read", check.Detail);
    }

    [Fact]
    public async Task Certificate_check_runs_only_for_https_with_a_thumbprint()
    {
        var env = FakePrerequisiteEnvironment.AllGreen();
        var http = await PrerequisiteEvaluator.EvaluateAsync(
            MinimalConfig(), @"C:\pkg", env, serviceAccountPasswordValidated: false);
        Assert.DoesNotContain(http.Checks, c => c.Id == PrerequisiteCheckId.IisBindingCertificate);

        var config = MinimalConfig();
        config.HostAgent.IisBindingProtocol = "https";
        var noThumbprint = await PrerequisiteEvaluator.EvaluateAsync(
            config, @"C:\pkg", FakePrerequisiteEnvironment.AllGreen(), serviceAccountPasswordValidated: false);
        Assert.DoesNotContain(noThumbprint.Checks, c => c.Id == PrerequisiteCheckId.IisBindingCertificate);
    }

    [Fact]
    public async Task Missing_database_blocks_install_and_is_never_created()
    {
        var env = FakePrerequisiteEnvironment.AllGreen();
        env.SqlProbeResult = (false, "The database was not found.");

        var evaluation = await PrerequisiteEvaluator.EvaluateAsync(
            MinimalConfig(), @"C:\pkg", env, serviceAccountPasswordValidated: false);

        var check = evaluation.Checks.Single(c => c.Id == PrerequisiteCheckId.SqlDatabase);
        Assert.False(check.IsSatisfied);
        Assert.False(check.CanAutoFix);
        Assert.False(evaluation.CanInstall);
    }

    [Fact]
    public async Task Too_little_disk_blocks_install()
    {
        var env = FakePrerequisiteEnvironment.AllGreen();
        env.FreeDisk = 512L * 1024 * 1024;

        var evaluation = await PrerequisiteEvaluator.EvaluateAsync(
            MinimalConfig(), @"C:\pkg", env, serviceAccountPasswordValidated: false);

        Assert.False(evaluation.Checks.Single(c => c.Id == PrerequisiteCheckId.FreeDiskSpace).IsSatisfied);
        Assert.False(evaluation.CanInstall);
    }

    [Fact]
    public async Task Undeterminable_disk_blocks_install()
    {
        var env = FakePrerequisiteEnvironment.AllGreen();
        env.FreeDisk = -1;

        var evaluation = await PrerequisiteEvaluator.EvaluateAsync(
            MinimalConfig(), @"C:\pkg", env, serviceAccountPasswordValidated: false);

        var check = evaluation.Checks.Single(c => c.Id == PrerequisiteCheckId.FreeDiskSpace);
        Assert.False(check.IsSatisfied);
        Assert.Contains("could not be determined", check.Detail);
        Assert.False(evaluation.CanInstall);
    }

    [Fact]
    public async Task Domain_account_requires_a_validated_password_before_install()
    {
        var config = MinimalConfig();
        config.HostAgent.ServiceAccountName = @"CONTOSO\svc-omp";
        var env = FakePrerequisiteEnvironment.AllGreen();

        var pending = await PrerequisiteEvaluator.EvaluateAsync(
            config, @"C:\pkg", env, serviceAccountPasswordValidated: false);
        Assert.False(pending.Checks.Single(c => c.Id == PrerequisiteCheckId.ServiceAccount).IsSatisfied);
        Assert.False(pending.CanInstall);

        var validated = await PrerequisiteEvaluator.EvaluateAsync(
            config, @"C:\pkg", env, serviceAccountPasswordValidated: true);
        Assert.True(validated.Checks.Single(c => c.Id == PrerequisiteCheckId.ServiceAccount).IsSatisfied);
        Assert.True(validated.CanInstall);
    }

    [Fact]
    public async Task Built_in_account_needs_no_password()
    {
        var config = MinimalConfig();
        config.HostAgent.ServiceAccountName = "NetworkService";
        var evaluation = await PrerequisiteEvaluator.EvaluateAsync(
            config, @"C:\pkg", FakePrerequisiteEnvironment.AllGreen(), serviceAccountPasswordValidated: false);

        Assert.True(evaluation.Checks.Single(c => c.Id == PrerequisiteCheckId.ServiceAccount).IsSatisfied);
        Assert.True(evaluation.CanInstall);
    }
}

public class RuntimeRequirementResolverTests
{
    [Fact]
    public void Highest_framework_major_wins()
    {
        var major = RuntimeRequirementResolver.ResolveRequiredMajor(
        [
            """{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"8.0.1"}}}""",
            """{"runtimeOptions":{"frameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"},{"name":"Microsoft.AspNetCore.App","version":"10.0.2"}]}}"""
        ]);
        Assert.Equal(10, major);
    }

    [Fact]
    public void No_runtimeconfigs_falls_back_to_the_repository_major()
        => Assert.Equal(
            RuntimeRequirementResolver.FallbackRuntimeMajor,
            RuntimeRequirementResolver.ResolveRequiredMajor([]));

    [Fact]
    public void Malformed_runtimeconfig_is_ignored()
        => Assert.Equal(
            RuntimeRequirementResolver.FallbackRuntimeMajor,
            RuntimeRequirementResolver.ResolveRequiredMajor(["not json {"]));

    [Fact]
    public void Bundle_picker_prefers_the_matching_major()
    {
        var picked = RuntimeRequirementResolver.PickHostingBundleInstaller(
            [@"C:\p\dotnet-hosting-8.0.11-win.exe", @"C:\p\dotnet-hosting-10.0.3-win.exe"],
            10);
        Assert.Equal(@"C:\p\dotnet-hosting-10.0.3-win.exe", picked);
    }

    [Fact]
    public void Bundle_picker_returns_null_without_a_match()
        => Assert.Null(RuntimeRequirementResolver.PickHostingBundleInstaller(
            [@"C:\p\dotnet-hosting-8.0.11-win.exe"], 10));
}

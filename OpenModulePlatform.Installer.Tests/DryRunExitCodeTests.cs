using OpenModulePlatform.Installer.Core;

namespace OpenModulePlatform.Installer.Tests;

/// <summary>
/// The dry-run exit-code contract: an existing LOCAL installation is an
/// information line with its own exit code (4), never confused with the
/// profile-matching failure (2), and the prerequisite list always prints in
/// full before the code is decided.
/// </summary>
public class DryRunExitCodeTests
{
    [Fact]
    public void Existing_local_installation_gets_its_own_exit_code()
        => Assert.Equal(4, DryRunExitCodes.Decide(
            localInstallationExists: true,
            simulatingAnotherMachine: false,
            blockingCheckCount: 0));

    [Fact]
    public void Already_installed_is_distinct_from_profile_matching_failures()
        => Assert.NotEqual(DryRunExitCodes.NoOrSeveralMatchingProfiles, DryRunExitCodes.AlreadyInstalled);

    [Fact]
    public void Machine_name_override_keeps_a_local_installation_informational()
    {
        // --machine-name simulates another machine: the local installation
        // says nothing about the target, so a green dry run stays green.
        Assert.Equal(DryRunExitCodes.Success, DryRunExitCodes.Decide(
            localInstallationExists: true,
            simulatingAnotherMachine: true,
            blockingCheckCount: 0));
    }

    [Fact]
    public void Machine_name_override_still_reports_blocking_checks()
        => Assert.Equal(DryRunExitCodes.Blocked, DryRunExitCodes.Decide(
            localInstallationExists: true,
            simulatingAnotherMachine: true,
            blockingCheckCount: 1));

    [Fact]
    public void Already_installed_wins_over_blocking_checks_on_the_local_machine()
        => Assert.Equal(DryRunExitCodes.AlreadyInstalled, DryRunExitCodes.Decide(
            localInstallationExists: true,
            simulatingAnotherMachine: false,
            blockingCheckCount: 2));

    [Fact]
    public void Blocking_checks_give_the_blocked_code()
        => Assert.Equal(DryRunExitCodes.Blocked, DryRunExitCodes.Decide(
            localInstallationExists: false,
            simulatingAnotherMachine: false,
            blockingCheckCount: 1));

    [Fact]
    public void No_installation_and_no_blocking_checks_is_green()
        => Assert.Equal(DryRunExitCodes.Success, DryRunExitCodes.Decide(
            localInstallationExists: false,
            simulatingAnotherMachine: false,
            blockingCheckCount: 0));

    [Fact]
    public void Exit_codes_are_stable_and_unique()
    {
        var codes = new[]
        {
            DryRunExitCodes.Success,
            DryRunExitCodes.Error,
            DryRunExitCodes.NoOrSeveralMatchingProfiles,
            DryRunExitCodes.Blocked,
            DryRunExitCodes.AlreadyInstalled
        };
        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.Equal(0, DryRunExitCodes.Success);
        Assert.Equal(1, DryRunExitCodes.Error);
        Assert.Equal(2, DryRunExitCodes.NoOrSeveralMatchingProfiles);
        Assert.Equal(3, DryRunExitCodes.Blocked);
        Assert.Equal(4, DryRunExitCodes.AlreadyInstalled);
    }
}

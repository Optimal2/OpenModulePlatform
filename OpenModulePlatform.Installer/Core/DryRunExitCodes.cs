namespace OpenModulePlatform.Installer.Core;

/// <summary>
/// Exit codes for <c>--dry-run</c>. The codes are part of the operator-facing
/// contract and are documented in docs/FIRST_INSTALLER.md and
/// docs/FRESH_INSTALL.md; never renumber them.
/// </summary>
internal static class DryRunExitCodes
{
    /// <summary>The dry run completed and nothing blocks the install.</summary>
    public const int Success = 0;

    /// <summary>The dry run itself failed (bad arguments, unreadable profile, ...).</summary>
    public const int Error = 1;

    /// <summary>No profile matched, or several profiles matched.</summary>
    public const int NoOrSeveralMatchingProfiles = 2;

    /// <summary>A check the installer cannot fix itself would block the install.</summary>
    public const int Blocked = 3;

    /// <summary>
    /// An installation already exists on the local computer. Kept distinct
    /// from <see cref="NoOrSeveralMatchingProfiles"/> so operators and scripts
    /// can tell "wrong machine / wrong package" apart from "already done".
    /// </summary>
    public const int AlreadyInstalled = 4;

    /// <summary>
    /// Decides the dry-run exit code after every check has run and been
    /// printed. An existing LOCAL installation is an information line, not a
    /// refusal: the prerequisite list is always printed in full. With
    /// <c>--machine-name</c> the dry run simulates another machine, so the
    /// local installation says nothing about the target and never decides the
    /// exit code.
    /// </summary>
    public static int Decide(bool localInstallationExists, bool simulatingAnotherMachine, int blockingCheckCount)
    {
        if (localInstallationExists && !simulatingAnotherMachine)
        {
            return AlreadyInstalled;
        }

        return blockingCheckCount > 0 ? Blocked : Success;
    }
}

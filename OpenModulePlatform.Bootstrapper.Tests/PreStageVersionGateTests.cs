namespace OpenModulePlatform.Bootstrapper.Tests;

/// <summary>
/// The pre-stage host-version gate.
///
/// Staging a rebuilt package whose content changed while its registered version
/// stayed the same produces an installation that reports a version it does not
/// actually contain. The host agent only auto-applies a HIGHER version, so the
/// new content is either silently ignored or imported under an identity that no
/// longer describes it -- and every later comparison against that version is
/// wrong.
///
/// Until 2026-09-02 this was a warning against the previous local source stamp,
/// not a gate, and a warning is something a script walks straight past.
/// </summary>
public sealed class PreStageVersionGateTests
{
    private static PreStageComponent Component(
        string key,
        string sourceVersion,
        string? installedVersion,
        bool contentChanged)
        => new(key, sourceVersion, installedVersion, contentChanged);

    [Fact]
    public void UnchangedVersionWithChangedContentIsBlocked()
    {
        // The whole reason the gate exists.
        var verdict = PreStageVersionGate.Evaluate(
            [Component("omp-portal-web", "0.3.624", "0.3.624", contentChanged: true)],
            databaseChecked: true,
            databaseFailure: null);

        Assert.False(verdict.MayProceed);
        Assert.Contains("omp-portal-web", verdict.Message, StringComparison.Ordinal);
        Assert.Contains("0.3.624", verdict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBlockingMessageNamesTheBumpTheOperatorMustMake()
    {
        // A refusal that does not say what to do next is a refusal the operator
        // has to reverse-engineer.
        var verdict = PreStageVersionGate.Evaluate(
            [Component("omp-portal-web", "0.3.624", "0.3.624", contentChanged: true)],
            databaseChecked: true,
            databaseFailure: null);

        Assert.Contains("bump-version", verdict.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllOffendingComponentsAreNamedNotJustTheFirst()
    {
        // Naming one at a time turns a single fix into several round trips.
        var verdict = PreStageVersionGate.Evaluate(
            [
                Component("omp-portal-web", "0.3.624", "0.3.624", contentChanged: true),
                Component("omp-auth-web", "0.3.225", "0.3.225", contentChanged: true),
            ],
            databaseChecked: true,
            databaseFailure: null);

        Assert.False(verdict.MayProceed);
        Assert.Contains("omp-portal-web", verdict.Message, StringComparison.Ordinal);
        Assert.Contains("omp-auth-web", verdict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedContentWithABumpedVersionProceeds()
    {
        // This is the normal, correct flow and must not be slowed down.
        var verdict = PreStageVersionGate.Evaluate(
            [Component("omp-portal-web", "0.3.625", "0.3.624", contentChanged: true)],
            databaseChecked: true,
            databaseFailure: null);

        Assert.True(verdict.MayProceed);
    }

    [Fact]
    public void UnchangedContentAtTheSameVersionProceeds()
    {
        // Re-staging an identical package is a no-op, not a version problem.
        var verdict = PreStageVersionGate.Evaluate(
            [Component("omp-portal-web", "0.3.624", "0.3.624", contentChanged: false)],
            databaseChecked: true,
            databaseFailure: null);

        Assert.True(verdict.MayProceed);
    }

    [Fact]
    public void AComponentNotInstalledYetProceeds()
    {
        // Nothing is being overwritten, so there is no identity to contradict.
        var verdict = PreStageVersionGate.Evaluate(
            [Component("omp-new-web", "0.1.0", null, contentChanged: true)],
            databaseChecked: true,
            databaseFailure: null);

        Assert.True(verdict.MayProceed);
    }

    [Fact]
    public void UnreadableHostStateNeverReportsASafeGreenConclusion()
    {
        // Absence of a measurement must never read as a passing measurement. The
        // host state might well say the version is unchanged.
        var verdict = PreStageVersionGate.Evaluate(
            [Component("omp-portal-web", "0.3.624", null, contentChanged: true)],
            databaseChecked: false,
            databaseFailure: "Login failed for user 'omp'.");

        Assert.False(verdict.MayProceed);
        Assert.Contains("could not be read", verdict.Message, StringComparison.OrdinalIgnoreCase);
        // The underlying reason travels with the refusal, so the operator is not
        // left guessing why the host state was unavailable.
        Assert.Contains("Login failed", verdict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreadableHostStateIsRefusedEvenWhenNothingChanged()
    {
        // "Nothing changed" is itself a claim about the host state, and it is
        // exactly the claim that could not be verified.
        var verdict = PreStageVersionGate.Evaluate(
            [Component("omp-portal-web", "0.3.624", null, contentChanged: false)],
            databaseChecked: false,
            databaseFailure: null);

        Assert.False(verdict.MayProceed);
    }

    [Fact]
    public void AnEmptyComponentSetWithAReadableHostStateProceeds()
    {
        var verdict = PreStageVersionGate.Evaluate([], databaseChecked: true, databaseFailure: null);

        Assert.True(verdict.MayProceed);
    }

    [Fact]
    public void AMissingSourceVersionIsRefusedRatherThanGuessed()
    {
        // A component whose source version is unknown cannot be compared, and a
        // comparison that cannot be made must not resolve to "fine".
        var verdict = PreStageVersionGate.Evaluate(
            [Component("omp-portal-web", "", "0.3.624", contentChanged: true)],
            databaseChecked: true,
            databaseFailure: null);

        Assert.False(verdict.MayProceed);
    }
}

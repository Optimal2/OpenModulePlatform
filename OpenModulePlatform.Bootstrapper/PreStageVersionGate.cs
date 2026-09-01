using System.Text;

namespace OpenModulePlatform.Bootstrapper;

/// <summary>One component as the pre-stage gate sees it.</summary>
/// <param name="ComponentKey">Manifest key, used to name the offender.</param>
/// <param name="SourceVersion">Version the rebuilt package declares.</param>
/// <param name="InstalledVersion">
/// Version registered in the local host state, or null when the component is not
/// installed yet or the host state could not be read.
/// </param>
/// <param name="ContentChanged">
/// Whether the rebuilt package content differs from what is installed.
/// </param>
internal sealed record PreStageComponent(
    string ComponentKey,
    string SourceVersion,
    string? InstalledVersion,
    bool ContentChanged);

/// <summary>The gate's decision, and why.</summary>
internal sealed record PreStageVerdict(bool MayProceed, string Message);

/// <summary>
/// Refuses to stage a rebuilt package whose content changed while its registered
/// version stayed the same.
/// </summary>
/// <remarks>
/// The host agent auto-applies only a HIGHER version. Staging changed content
/// under an unchanged version therefore produces one of two bad outcomes: the
/// new content is silently ignored, or it is imported under an identity that no
/// longer describes it - and every later comparison against that version is
/// wrong, including the ones an operator uses to decide whether a deploy is
/// needed at all.
///
/// Until 2026-09-02 this was a warning against the previous local source stamp
/// rather than a gate, and a warning is something a script walks straight past.
/// The source stamp is still useful as an early local signal, but it is not a
/// substitute for asking the host state what is actually registered.
///
/// The decision lives here as a pure function so it can be proven, rather than
/// inline in the refresh path where it could only be reasoned about.
/// </remarks>
internal static class PreStageVersionGate
{
    public static PreStageVerdict Evaluate(
        IReadOnlyList<PreStageComponent> components,
        bool databaseChecked,
        string? databaseFailure)
    {
        ArgumentNullException.ThrowIfNull(components);

        if (!databaseChecked)
        {
            // Absence of a measurement must never read as a passing measurement.
            // "Nothing changed" is itself a claim about the host state, and it is
            // precisely the claim that could not be verified here.
            var reason = string.IsNullOrWhiteSpace(databaseFailure)
                ? "no reason was reported"
                : databaseFailure.Trim();
            return new PreStageVerdict(
                false,
                "Refusing to stage: the registered host state could not be read, so it is not " +
                $"known whether the package versions being staged are already registered ({reason}). " +
                "Fix the host state connection and run the refresh again, or stage manually once " +
                "the registered versions have been confirmed by hand.");
        }

        var blocked = new List<PreStageComponent>();
        var unknownVersion = new List<PreStageComponent>();

        foreach (var component in components)
        {
            if (string.IsNullOrWhiteSpace(component.SourceVersion))
            {
                // A comparison that cannot be made must not resolve to "fine".
                unknownVersion.Add(component);
                continue;
            }

            if (!component.ContentChanged)
            {
                continue;
            }

            // Not installed yet: nothing is being overwritten, so there is no
            // registered identity to contradict.
            if (string.IsNullOrWhiteSpace(component.InstalledVersion))
            {
                continue;
            }

            if (string.Equals(
                    component.InstalledVersion.Trim(),
                    component.SourceVersion.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                blocked.Add(component);
            }
        }

        if (blocked.Count == 0 && unknownVersion.Count == 0)
        {
            return new PreStageVerdict(true, "Pre-stage version check passed.");
        }

        var message = new StringBuilder();
        message.Append("Refusing to stage: the package content changed while the registered version stayed the same.");

        foreach (var component in blocked)
        {
            message.Append(Environment.NewLine)
                   .Append("  - ")
                   .Append(component.ComponentKey)
                   .Append(" is registered as ")
                   .Append(component.InstalledVersion)
                   .Append(" and the rebuilt package still declares ")
                   .Append(component.SourceVersion)
                   .Append(", but its content differs.");
        }

        foreach (var component in unknownVersion)
        {
            message.Append(Environment.NewLine)
                   .Append("  - ")
                   .Append(component.ComponentKey)
                   .Append(" declares no source version, so it cannot be compared with the registered state.");
        }

        message.Append(Environment.NewLine)
               .Append("Bump the affected component(s) before staging, for example: ")
               .Append(@".\scripts\omp\bump-version.ps1 -ComponentKey ")
               .Append(blocked.Count > 0
                   ? string.Join(",", blocked.Select(component => component.ComponentKey))
                   : "<component>")
               .Append(Environment.NewLine)
               .Append("The host agent auto-applies only a higher version, so staging this as-is would ")
               .Append("either ignore the new content or register it under a version that no longer describes it.");

        return new PreStageVerdict(false, message.ToString());
    }
}

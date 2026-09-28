using System.Text;

namespace OpenModulePlatform.Bootstrapper;

/// <summary>One artifact of the rebuilt package as the pre-stage gate sees it.</summary>
/// <param name="ComponentKey">Target / component key, used to name the offender.</param>
/// <param name="SourceVersion">Version the rebuilt package declares.</param>
/// <param name="InstalledVersion">
/// Version of the artifact row registered for the same app, package type and
/// target at <paramref name="SourceVersion"/>, or null when no such row exists.
/// </param>
/// <param name="PackageSha256">
/// SHA-256 of the artifact content in the rebuilt package, measured exactly as the
/// host agent import measures it, or null when it was not (or could not be) measured.
/// </param>
/// <param name="RegisteredSha256">omp.Artifacts.Sha256 of the registered row, or null.</param>
/// <param name="Identity">Human-readable artifact identity for messages.</param>
/// <param name="MeasurementFailure">Why <paramref name="PackageSha256"/> could not be measured.</param>
internal sealed record PreStageComponent(
    string ComponentKey,
    string SourceVersion,
    string? InstalledVersion,
    string? PackageSha256,
    string? RegisteredSha256,
    string? Identity = null,
    string? MeasurementFailure = null)
{
    /// <summary>Whether both hashes are known and differ.</summary>
    public bool ContentChanged
        => !string.IsNullOrWhiteSpace(PackageSha256)
           && !string.IsNullOrWhiteSpace(RegisteredSha256)
           && !string.Equals(PackageSha256.Trim(), RegisteredSha256.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>The gate's decision, and why.</summary>
internal sealed record PreStageVerdict(bool MayProceed, string Message);

/// <summary>
/// Refuses to stage a rebuilt package whose artifact content differs from what is
/// already registered under the same version.
/// </summary>
/// <remarks>
/// The host agent import identifies an artifact by app, version, package type and
/// target, and refuses one whose content SHA-256 differs from omp.Artifacts.Sha256
/// under that identity ("The artifact content has changed under the same version").
/// Staging such a package only moves the failure from here to the import log.
///
/// Until 2026-09-28 the gate compared version numbers only and was fed the
/// developer source status "DIFF" as its content signal. "DIFF" actually means
/// "source version is older than the installed one", so the gate never saw a
/// same-version content change and passed packages the import then rejected.
/// It now compares the same measure the import uses.
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
        var unmeasured = new List<PreStageComponent>();
        var unknownVersion = new List<PreStageComponent>();

        foreach (var component in components)
        {
            if (string.IsNullOrWhiteSpace(component.SourceVersion))
            {
                // A comparison that cannot be made must not resolve to "fine".
                unknownVersion.Add(component);
                continue;
            }

            // Not registered at this version: nothing is being overwritten, so
            // there is no registered identity to contradict.
            if (string.IsNullOrWhiteSpace(component.InstalledVersion)
                || !string.Equals(
                    component.InstalledVersion.Trim(),
                    component.SourceVersion.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Registered without a hash: the import adopts the existing row.
            if (string.IsNullOrWhiteSpace(component.RegisteredSha256))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(component.PackageSha256))
            {
                unmeasured.Add(component);
                continue;
            }

            if (component.ContentChanged)
            {
                blocked.Add(component);
            }
        }

        if (blocked.Count == 0 && unmeasured.Count == 0 && unknownVersion.Count == 0)
        {
            return new PreStageVerdict(true, "Pre-stage content check passed.");
        }

        var message = new StringBuilder();
        message.Append("Refusing to stage: the artifact content has changed under the same version, ")
               .Append("so the host agent import would reject it.");

        foreach (var component in blocked)
        {
            message.Append(Environment.NewLine)
                   .Append("  - ")
                   .Append(component.ComponentKey)
                   .Append(": ")
                   .Append(component.Identity ?? component.SourceVersion)
                   .Append(" is registered with SHA-256 ")
                   .Append(component.RegisteredSha256)
                   .Append(" but the package content has SHA-256 ")
                   .Append(component.PackageSha256)
                   .Append('.');
        }

        foreach (var component in unmeasured)
        {
            message.Append(Environment.NewLine)
                   .Append("  - ")
                   .Append(component.ComponentKey)
                   .Append(": ")
                   .Append(component.Identity ?? component.SourceVersion)
                   .Append(" is already registered, but the package content could not be measured")
                   .Append(string.IsNullOrWhiteSpace(component.MeasurementFailure)
                       ? "."
                       : $" ({component.MeasurementFailure.Trim()}).");
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
               .Append("An artifact identity is its version plus the SHA-256 of its content; the same version ")
               .Append("cannot be registered twice with different content.");

        return new PreStageVerdict(false, message.ToString());
    }
}

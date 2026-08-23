namespace OpenModulePlatform.Artifacts;

public enum ArtifactConfigurationCarryForwardOutcome
{
    /// <summary>
    /// The previous artifact row was operator-edited (or operator-disabled), the
    /// package file is unchanged against that row's package baseline, and the
    /// operator content was carried forward to the new artifact row.
    /// </summary>
    Preserved,

    /// <summary>
    /// The previous row had NO package baseline, so its content was carried forward
    /// over a target row that was still pristine package content.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Preserved"/> because the lineage is genuinely unknown.
    /// A missing baseline means either an operator-created row or a row that predates
    /// the PackageFileContent column (added 2026-08-12) and was never re-imported - the
    /// schema says as much: "NULL means unknown lineage (legacy row or operator-created
    /// row)". Reporting these as operator edits would be a false claim.
    ///
    /// Carrying them forward is the deliberate choice: a real customer install lost its
    /// configured OmpAuth:Oidc block because these rows lost to the package default, and
    /// the loss compounded across versions. The cost is the opposite error - a deliberate
    /// package change to a never-edited legacy row does not reach the new version. That
    /// case is why this has its own outcome instead of hiding inside Preserved: it is
    /// reported separately so somebody can look at it.
    /// </remarks>
    PreservedWithoutBaseline,

    /// <summary>
    /// The effective configuration content of the previous artifact differs from
    /// the new artifact and could not be carried forward safely: either the
    /// package file changed against an operator-edited baseline, or the previous
    /// row has no package baseline and the TARGET row already carries an operator
    /// edit that must not be overwritten. The package file wins; review manually.
    /// </summary>
    Conflict,

    /// <summary>
    /// The previous artifact has an operator-edited row whose relative path is
    /// not part of the new package, so nothing was carried forward for it.
    /// </summary>
    MissingInPackage
}

public sealed record ArtifactConfigurationCarryForwardItem(
    string RelativePath,
    ArtifactConfigurationCarryForwardOutcome Outcome);

public sealed record ArtifactConfigurationCarryForwardResult(
    string? SourceVersion,
    IReadOnlyList<ArtifactConfigurationCarryForwardItem> Items)
{
    public static ArtifactConfigurationCarryForwardResult Empty { get; } = new(null, []);

    public IReadOnlyList<string> PathsWithOutcome(ArtifactConfigurationCarryForwardOutcome outcome)
        => Items
            .Where(item => item.Outcome == outcome)
            .Select(static item => item.RelativePath)
            .ToList();

    /// <summary>
    /// Builds the operator-facing import message for this carry-forward pass, or
    /// null when there is nothing worth reporting.
    /// </summary>
    public string? BuildImportMessage()
    {
        if (Items.Count == 0 || string.IsNullOrWhiteSpace(SourceVersion))
        {
            return null;
        }

        var parts = new List<string>();
        var preserved = PathsWithOutcome(ArtifactConfigurationCarryForwardOutcome.Preserved);
        if (preserved.Count > 0)
        {
            parts.Add(
                $"Preserved {preserved.Count} operator-edited configuration file(s) from version {SourceVersion}: {string.Join(", ", preserved)}.");
        }

        // Reported apart from Preserved on purpose: these rows have no package baseline,
        // so calling them operator edits would assert something unknown. They are the
        // one category where a deliberate package change can fail to reach the new
        // version, which is exactly why the operator has to see them by name.
        var withoutBaseline = PathsWithOutcome(
            ArtifactConfigurationCarryForwardOutcome.PreservedWithoutBaseline);
        if (withoutBaseline.Count > 0)
        {
            parts.Add(
                $"Carried forward {withoutBaseline.Count} configuration file(s) from version {SourceVersion} " +
                "that have no package baseline, so they could not be confirmed as operator edits: " +
                $"{string.Join(", ", withoutBaseline)}. If this package intended to change any of them, " +
                "the change did NOT take effect - compare against the package file and re-save to adopt it.");
        }

        var conflicts = PathsWithOutcome(ArtifactConfigurationCarryForwardOutcome.Conflict);
        if (conflicts.Count > 0)
        {
            parts.Add(
                $"Warning: configuration file(s) differ from version {SourceVersion} and were taken from the package; review them for lost operator edits: {string.Join(", ", conflicts)}.");
        }

        var missing = PathsWithOutcome(ArtifactConfigurationCarryForwardOutcome.MissingInPackage);
        if (missing.Count > 0)
        {
            parts.Add(
                $"Warning: operator-edited configuration file(s) from version {SourceVersion} are not part of this package and were not carried forward: {string.Join(", ", missing)}.");
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }
}

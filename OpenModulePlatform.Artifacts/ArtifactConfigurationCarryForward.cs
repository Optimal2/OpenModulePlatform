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
    /// The effective configuration content of the previous artifact differs from
    /// the new artifact and could not be carried forward safely: either the
    /// package file changed against an operator-edited baseline, or the previous
    /// row has no package baseline (legacy row) so operator edits cannot be told
    /// apart from package changes. The package file wins; review manually.
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

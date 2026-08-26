// File: OpenModulePlatform.Portal/Services/AdRolePrincipalMigrationPlanner.cs
namespace OpenModulePlatform.Portal.Services;

/// <summary>
/// Pure decision logic for the bulk "move AD-based role principal rows to OMP users"
/// admin view (campaign ad-principalformen-hela-vagen-adfs-till-rbac, DEL 3).
/// </summary>
/// <remarks>
/// <para>
/// The planner is deliberately free of data access: the repository feeds it the
/// RolePrincipals rows together with the link candidates found for each principal,
/// and the planner returns one deterministic decision per row. Preview and execute
/// share the same matching SQL and the same planner, so the preview equals the outcome.
/// </para>
/// <para>
/// Design decision (do not change without a new risk analysis): the bulk move never
/// deletes the source ADUser/User rows. Deleting them is only safe once every sign-in
/// path is proven to yield an OMP-user linkage, which is not proven (for example ADFS
/// sign-ins for users without a resolvable AD auth link). The source rows are retained;
/// the per-user move keeps its own delete behaviour and is not affected by this planner.
/// </para>
/// </remarks>
internal static class AdRolePrincipalMigrationPlanner
{
    /// <summary>
    /// Returns one decision per input row, in input order. Combined with the
    /// deterministic ORDER BY of the matching query the output is deterministic.
    /// </summary>
    internal static IReadOnlyList<AdRolePrincipalMigrationDecision> Plan(
        IEnumerable<AdRolePrincipalMigrationInput> rows)
    {
        var decisions = new List<AdRolePrincipalMigrationDecision>();
        foreach (var row in rows)
        {
            decisions.Add(PlanRow(row));
        }

        return decisions;
    }

    private static AdRolePrincipalMigrationDecision PlanRow(AdRolePrincipalMigrationInput row)
    {
        // ADGroup (and any other non-user principal type) must never be touched.
        if (!IsMovablePrincipalType(row.PrincipalType))
        {
            return new AdRolePrincipalMigrationDecision(
                row.RoleId,
                row.RoleName,
                row.PrincipalType,
                row.Principal,
                AdRolePrincipalMigrationOutcome.Skipped,
                TargetUserId: null,
                TargetDisplayName: null,
                AdRolePrincipalMigrationSkipReason.UnsupportedPrincipalType,
                RiskNote: false);
        }

        if (row.Candidates.Count == 0)
        {
            return Skip(row, AdRolePrincipalMigrationSkipReason.NoEnabledAdLink);
        }

        var activeUsers = row.Candidates
            .Where(static candidate => candidate.IsActive)
            .GroupBy(static candidate => candidate.UserId)
            .Select(static group => group.First())
            .OrderBy(static candidate => candidate.UserId)
            .ToList();

        if (activeUsers.Count > 1)
        {
            return Skip(row, AdRolePrincipalMigrationSkipReason.AmbiguousLinkedUsers);
        }

        if (activeUsers.Count == 0)
        {
            return Skip(row, AdRolePrincipalMigrationSkipReason.LinkedUserInactive);
        }

        var target = activeUsers[0];
        if (row.OmpUserAssignmentAlreadyPresent)
        {
            return new AdRolePrincipalMigrationDecision(
                row.RoleId,
                row.RoleName,
                row.PrincipalType,
                row.Principal,
                AdRolePrincipalMigrationOutcome.AlreadyPresent,
                target.UserId,
                target.DisplayName,
                AdRolePrincipalMigrationSkipReason.None,
                RiskNote: false);
        }

        // RiskNote is informational only: the user currently only signs in via a path
        // that may not yield an OMP-user linkage (no enabled non-AD auth link). The
        // source row is retained either way, so nothing is lost.
        return new AdRolePrincipalMigrationDecision(
            row.RoleId,
            row.RoleName,
            row.PrincipalType,
            row.Principal,
            AdRolePrincipalMigrationOutcome.Move,
            target.UserId,
            target.DisplayName,
            AdRolePrincipalMigrationSkipReason.None,
            RiskNote: !target.HasNonAdEnabledLink);
    }

    private static AdRolePrincipalMigrationDecision Skip(
        AdRolePrincipalMigrationInput row,
        AdRolePrincipalMigrationSkipReason reason)
        => new(
            row.RoleId,
            row.RoleName,
            row.PrincipalType,
            row.Principal,
            AdRolePrincipalMigrationOutcome.Skipped,
            TargetUserId: null,
            TargetDisplayName: null,
            reason,
            RiskNote: false);

    private static bool IsMovablePrincipalType(string principalType)
        => string.Equals(principalType, "ADUser", StringComparison.OrdinalIgnoreCase)
            || string.Equals(principalType, "User", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What the planner decided for one RolePrincipals row.</summary>
public enum AdRolePrincipalMigrationOutcome
{
    /// <summary>Create an OmpUser row with Principal = the target user id.</summary>
    Move,

    /// <summary>The OmpUser target row already exists; no insert would happen.</summary>
    AlreadyPresent,

    /// <summary>Nothing is created; <see cref="AdRolePrincipalMigrationDecision.SkipReason"/> says why.</summary>
    Skipped
}

/// <summary>Machine-readable reason for a skipped row, localized in the page layer.</summary>
public enum AdRolePrincipalMigrationSkipReason
{
    None,
    UnsupportedPrincipalType,
    NoEnabledAdLink,
    AmbiguousLinkedUsers,
    LinkedUserInactive
}

/// <summary>
/// One enabled AD auth link that matched the principal, resolved to its OMP user.
/// </summary>
internal sealed record AdRolePrincipalMigrationLinkCandidate(
    int UserId,
    string DisplayName,
    bool IsActive,
    bool HasNonAdEnabledLink);

/// <summary>
/// One RolePrincipals row plus the link candidates found for its principal.
/// </summary>
internal sealed record AdRolePrincipalMigrationInput(
    int RoleId,
    string RoleName,
    string PrincipalType,
    string Principal,
    bool OmpUserAssignmentAlreadyPresent,
    IReadOnlyList<AdRolePrincipalMigrationLinkCandidate> Candidates);

/// <summary>
/// The planner verdict for one RolePrincipals row. Target fields are set for Move and
/// AlreadyPresent; <see cref="SkipReason"/> is set for Skipped.
/// </summary>
public sealed record AdRolePrincipalMigrationDecision(
    int RoleId,
    string RoleName,
    string PrincipalType,
    string Principal,
    AdRolePrincipalMigrationOutcome Outcome,
    int? TargetUserId,
    string? TargetDisplayName,
    AdRolePrincipalMigrationSkipReason SkipReason,
    bool RiskNote);

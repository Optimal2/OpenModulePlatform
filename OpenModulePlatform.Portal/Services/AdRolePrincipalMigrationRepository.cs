// File: OpenModulePlatform.Portal/Services/AdRolePrincipalMigrationRepository.cs
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Web.Shared.Services;
using System.Data;
using System.Globalization;

namespace OpenModulePlatform.Portal.Services;

/// <summary>
/// Data access for the bulk "move AD-based role principal rows to OMP users" admin
/// view (campaign ad-principalformen-hela-vagen-adfs-till-rbac, DEL 3).
/// </summary>
/// <remarks>
/// <para>
/// Preview and execute share the exact same matching SQL and the same
/// <see cref="AdRolePrincipalMigrationPlanner"/>, so the preview equals the outcome.
/// Execute re-runs the matching query inside a serializable transaction and inserts
/// with WHERE NOT EXISTS, which makes a repeated run a no-op.
/// </para>
/// <para>
/// The bulk move NEVER deletes the source ADUser/User rows and NEVER touches ADGroup
/// rows. Source rows are retained because deleting them is only safe once every
/// sign-in path is proven to yield an OMP-user linkage (see the planner remarks).
/// </para>
/// </remarks>
public sealed class AdRolePrincipalMigrationRepository
{
    private const string AdProviderDisplayName = "AD";

    /// <summary>
    /// The single matching query shared by preview and execute. Each output row is one
    /// RolePrincipals row (PrincipalType ADUser/User) crossed with one enabled AD auth
    /// link that matched the principal; rows without any link surface once with NULL
    /// user columns so the planner can skip them with a reason.
    /// </summary>
    private const string MatchingSql = @"
SELECT rp.RoleId,
       r.Name,
       rp.PrincipalType,
       rp.Principal,
       u.user_id,
       u.display_name,
       u.account_status,
       CASE WHEN EXISTS
       (
           SELECT 1
           FROM omp.user_auth other_link
           INNER JOIN omp.auth_providers other_provider
               ON other_provider.provider_id = other_link.provider_id
           WHERE other_link.user_id = u.user_id
             AND other_provider.display_name <> @ad_provider_display_name
             AND other_link.auth_status = N'enabled'
       ) THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END,
       CASE WHEN EXISTS
       (
           SELECT 1
           FROM omp.RolePrincipals existing
           WHERE existing.RoleId = rp.RoleId
             AND existing.PrincipalType = N'OmpUser'
             AND existing.Principal = CONVERT(nvarchar(20), u.user_id)
       ) THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END
FROM omp.RolePrincipals rp
INNER JOIN omp.Roles r ON r.RoleId = rp.RoleId
LEFT JOIN omp.user_auth ua
INNER JOIN omp.auth_providers ap
    ON ap.provider_id = ua.provider_id
   AND ap.display_name = @ad_provider_display_name
    ON ua.provider_user_key = rp.Principal
   AND ua.auth_status = N'enabled'
LEFT JOIN omp.users u ON u.user_id = ua.user_id
WHERE rp.PrincipalType IN (N'ADUser', N'User')
ORDER BY r.Name,
         rp.RoleId,
         rp.PrincipalType,
         rp.Principal,
         u.user_id;";

    private const string InsertOmpUserAssignmentSql = @"
INSERT INTO omp.RolePrincipals(RoleId, PrincipalType, Principal)
SELECT @RoleId, N'OmpUser', @Principal
WHERE NOT EXISTS
(
    SELECT 1
    FROM omp.RolePrincipals existing
    WHERE existing.RoleId = @RoleId
      AND existing.PrincipalType = N'OmpUser'
      AND existing.Principal = @Principal
);";

    private readonly SqlConnectionFactory _db;

    public AdRolePrincipalMigrationRepository(SqlConnectionFactory db)
    {
        _db = db;
    }

    /// <summary>
    /// Runs the matching query and returns the planner decision for every ADUser/User
    /// RolePrincipals row. Nothing is written.
    /// </summary>
    public async Task<IReadOnlyList<AdRolePrincipalMigrationDecision>> PreviewAsync(CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        var inputs = await ReadInputsAsync(conn, tx: null, ct);
        return AdRolePrincipalMigrationPlanner.Plan(inputs);
    }

    /// <summary>
    /// Re-runs the matching query inside a serializable transaction, plans again, and
    /// inserts one OmpUser assignment per Move decision (WHERE NOT EXISTS, so a second
    /// run creates zero rows). Source rows are retained. Commits once; rolls back on
    /// failure.
    /// </summary>
    public async Task<AdRolePrincipalMigrationReport> ExecuteAsync(CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var inputs = await ReadInputsAsync(conn, tx, ct);
            var decisions = AdRolePrincipalMigrationPlanner.Plan(inputs);

            var created = new List<AdRolePrincipalMigrationCreatedAssignment>();
            var skipped = new List<AdRolePrincipalMigrationSkippedRow>();
            var alreadyPresentCount = 0;

            foreach (var decision in decisions)
            {
                switch (decision.Outcome)
                {
                    case AdRolePrincipalMigrationOutcome.Move:
                        var inserted = await InsertOmpUserAssignmentAsync(
                            conn,
                            tx,
                            decision.RoleId,
                            decision.TargetUserId!.Value,
                            ct);
                        if (inserted)
                        {
                            created.Add(new AdRolePrincipalMigrationCreatedAssignment(
                                decision.RoleId,
                                decision.RoleName,
                                decision.PrincipalType,
                                decision.Principal,
                                decision.TargetUserId.Value,
                                decision.TargetDisplayName ?? string.Empty));
                        }
                        else
                        {
                            // A concurrent run beat us to it; the idempotent insert found the row.
                            alreadyPresentCount++;
                        }

                        break;

                    case AdRolePrincipalMigrationOutcome.AlreadyPresent:
                        alreadyPresentCount++;
                        break;

                    default:
                        skipped.Add(new AdRolePrincipalMigrationSkippedRow(
                            decision.RoleId,
                            decision.RoleName,
                            decision.PrincipalType,
                            decision.Principal,
                            decision.SkipReason));
                        break;
                }
            }

            await tx.CommitAsync(ct);
            return new AdRolePrincipalMigrationReport(created, skipped, alreadyPresentCount);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static async Task<IReadOnlyList<AdRolePrincipalMigrationInput>> ReadInputsAsync(
        SqlConnection conn,
        SqlTransaction? tx,
        CancellationToken ct)
    {
        var inputs = new List<AdRolePrincipalMigrationInput>();
        var indexByKey = new Dictionary<RowKey, int>();

        await using var cmd = new SqlCommand(MatchingSql, conn, tx);
        Add(cmd, "@ad_provider_display_name", AdProviderDisplayName);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var roleId = rdr.GetInt32(0);
            var roleName = rdr.GetString(1);
            var principalType = rdr.GetString(2);
            var principal = rdr.GetString(3);

            var key = new RowKey(roleId, principalType, principal);
            if (!indexByKey.TryGetValue(key, out var index))
            {
                index = inputs.Count;
                indexByKey[key] = index;
                inputs.Add(new AdRolePrincipalMigrationInput(
                    roleId,
                    roleName,
                    principalType,
                    principal,
                    OmpUserAssignmentAlreadyPresent: false,
                    []));
            }

            if (rdr.IsDBNull(4))
            {
                // No enabled AD link matched this principal.
                continue;
            }

            var userId = rdr.GetInt32(4);
            var displayName = rdr.IsDBNull(5) ? string.Empty : rdr.GetString(5);
            var isActive = !rdr.IsDBNull(6) && rdr.GetInt32(6) == 1;
            var hasNonAdEnabledLink = rdr.GetBoolean(7);
            var assignmentExists = rdr.GetBoolean(8);

            var current = inputs[index];
            var candidates = new List<AdRolePrincipalMigrationLinkCandidate>(current.Candidates);
            if (candidates.All(candidate => candidate.UserId != userId))
            {
                candidates.Add(new AdRolePrincipalMigrationLinkCandidate(
                    userId,
                    displayName,
                    isActive,
                    hasNonAdEnabledLink));
            }

            inputs[index] = current with
            {
                OmpUserAssignmentAlreadyPresent =
                    current.OmpUserAssignmentAlreadyPresent || (assignmentExists && isActive),
                Candidates = candidates
            };
        }

        return inputs;
    }

    private static async Task<bool> InsertOmpUserAssignmentAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int roleId,
        int targetUserId,
        CancellationToken ct)
    {
        await using var cmd = new SqlCommand(InsertOmpUserAssignmentSql, conn, tx);
        Add(cmd, "@RoleId", roleId);
        Add(cmd, "@Principal", targetUserId.ToString(CultureInfo.InvariantCulture));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static void Add(SqlCommand cmd, string name, object? value)
    {
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private readonly record struct RowKey(int RoleId, string PrincipalType, string Principal);
}

/// <summary>One OmpUser role assignment created by the bulk move.</summary>
public sealed record AdRolePrincipalMigrationCreatedAssignment(
    int RoleId,
    string RoleName,
    string SourcePrincipalType,
    string SourcePrincipal,
    int TargetUserId,
    string TargetDisplayName);

/// <summary>One RolePrincipals row the bulk move skipped, with the planner reason.</summary>
public sealed record AdRolePrincipalMigrationSkippedRow(
    int RoleId,
    string RoleName,
    string PrincipalType,
    string Principal,
    AdRolePrincipalMigrationSkipReason Reason);

/// <summary>The after-report of an execute run.</summary>
public sealed record AdRolePrincipalMigrationReport(
    IReadOnlyList<AdRolePrincipalMigrationCreatedAssignment> Created,
    IReadOnlyList<AdRolePrincipalMigrationSkippedRow> Skipped,
    int AlreadyPresentCount);

using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed partial class OmpHostArtifactRepository
{
    /// <summary>Maintenance finding category for widgets a HostAgent import skipped.</summary>
    public const string DashboardWidgetImportSkippedCategory = "DashboardWidgetImportSkipped";

    /// <summary>
    /// Records the outcome of one dashboard widget file in <c>omp.MaintenanceFindings</c>:
    /// every skipped widget becomes (or stays) an open finding that Portal Maintenance shows
    /// under dashboard widget readiness, and an open finding for a widget key that this
    /// import stored is closed. Keyless skips are grouped by source and close only after
    /// that source imports without any skips. Without the maintenance schema this is a
    /// no-op, so the widget import itself never depends on it.
    /// </summary>
    public async Task RecordDashboardWidgetImportFindingsAsync(
        IReadOnlyCollection<string> importedWidgetKeys,
        IReadOnlyCollection<PortableDashboardWidgetSkip> skippedWidgets,
        string sourceName,
        CancellationToken ct)
    {
        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceName)));
        // Parentheses cannot occur in a portable widget key, keeping the two identities disjoint.
        var sourceFindingKey = $"{DashboardWidgetImportSkippedCategory}:(source):{sourceHash}";

        const string resolveSql = @"
IF OBJECT_ID(N'omp.MaintenanceFindings', N'U') IS NULL
    RETURN;

UPDATE omp.MaintenanceFindings
SET Status = @cleanedStatus,
    ResultMessage = CASE WHEN FindingKey = @sourceFindingKey
        THEN N'A later import of this source completed without skipped widgets.'
        ELSE N'A later import stored this dashboard widget.' END,
    UpdatedUtc = SYSUTCDATETIME()
WHERE Category = @category
  AND Status IN (@openStatus, @failedStatus)
  AND (TargetIdentifier IN (SELECT CAST(value AS nvarchar(1000)) FROM OPENJSON(@widgetKeysJson))
       OR (@sourceSucceeded = 1 AND FindingKey = @sourceFindingKey));";

        // An ignored finding stays ignored: the operator chose not to act on it.
        const string upsertSql = @"
IF OBJECT_ID(N'omp.MaintenanceFindings', N'U') IS NULL
    RETURN;

DECLARE @nowUtc datetime2(3) = SYSUTCDATETIME();

MERGE omp.MaintenanceFindings WITH (HOLDLOCK) AS target
USING (SELECT @findingKey AS FindingKey) AS source
ON target.FindingKey = source.FindingKey
WHEN MATCHED THEN
    UPDATE SET
        Title = @title,
        Detail = @detail,
        RecommendedAction = @recommendedAction,
        SafetyNotes = @safetyNotes,
        Status = CASE WHEN target.Status = @ignoredStatus THEN target.Status ELSE @openStatus END,
        ResultMessage = CASE WHEN target.Status = @ignoredStatus THEN target.ResultMessage ELSE NULL END,
        LastSeenUtc = @nowUtc,
        UpdatedUtc = @nowUtc
WHEN NOT MATCHED THEN
    INSERT (FindingKey, Scope, HostId, Category, TargetKind, TargetIdentifier, Title, Detail,
            RecommendedAction, SafetyNotes, ActionJson, Status, Severity, Confidence,
            DetectedByHostAgentJobId, DetectedUtc, LastSeenUtc, UpdatedUtc)
    VALUES (@findingKey, N'Global', NULL, @category, N'DatabaseRow', @targetIdentifier, @title, @detail,
            @recommendedAction, @safetyNotes, NULL, @openStatus, 2, 100,
            NULL, @nowUtc, @nowUtc, @nowUtc);";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            if (importedWidgetKeys.Count > 0 || skippedWidgets.Count == 0)
            {
                await using var cmd = new SqlCommand(resolveSql, conn, tx);
                Add(cmd, "@category", SqlDbType.NVarChar, 100, DashboardWidgetImportSkippedCategory);
                Add(cmd, "@widgetKeysJson", SqlDbType.NVarChar, -1, JsonSerializer.Serialize(importedWidgetKeys));
                Add(cmd, "@sourceFindingKey", SqlDbType.NVarChar, 450, sourceFindingKey);
                Add(cmd, "@sourceSucceeded", SqlDbType.Bit, skippedWidgets.Count == 0);
                Add(cmd, "@openStatus", SqlDbType.TinyInt, MaintenanceFindingStatuses.Open);
                Add(cmd, "@failedStatus", SqlDbType.TinyInt, MaintenanceFindingStatuses.Failed);
                Add(cmd, "@cleanedStatus", SqlDbType.TinyInt, MaintenanceFindingStatuses.Cleaned);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            foreach (var skips in skippedWidgets.GroupBy(skip => skip.WidgetKey?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                var isKeyless = skips.Key.Length == 0;
                var targetIdentifier = Truncate(
                    isKeyless ? $"(missing widgetKey) {sourceName}" : skips.Key,
                    1000);
                await using var cmd = new SqlCommand(upsertSql, conn, tx);
                Add(cmd, "@findingKey", SqlDbType.NVarChar, 450, isKeyless
                    ? sourceFindingKey
                    : Truncate($"{DashboardWidgetImportSkippedCategory}:{targetIdentifier}", 450));
                Add(cmd, "@category", SqlDbType.NVarChar, 100, DashboardWidgetImportSkippedCategory);
                Add(cmd, "@targetIdentifier", SqlDbType.NVarChar, 1000, targetIdentifier);
                Add(cmd, "@title", SqlDbType.NVarChar, 300, "Dashboard widget skipped during HostAgent import");
                Add(cmd, "@detail", SqlDbType.NVarChar, -1, string.Join(Environment.NewLine, skips.Select(skip => skip.Reason)));
                Add(cmd, "@recommendedAction", SqlDbType.NVarChar, 300,
                    "Upgrade HostAgent and Portal to a version that supports this widget definition, then re-import the package.");
                Add(cmd, "@safetyNotes", SqlDbType.NVarChar, -1, $"Source file: {sourceName}. No stored widget was changed.");
                Add(cmd, "@openStatus", SqlDbType.TinyInt, MaintenanceFindingStatuses.Open);
                Add(cmd, "@ignoredStatus", SqlDbType.TinyInt, MaintenanceFindingStatuses.Ignored);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}

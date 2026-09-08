# Channel-type reconciliation contract and template

## Contract and evidence baseline

Implement the module-owned reconciliation pattern below: resolve each channel's
effective artifact and placements, disable stale requirements, and upsert active
requirements. The reference implementation is IbsPackager commit
`7a2deecfe26872c455618c3355c54611c3efd447`, specifically
`sql/1-setup-ibspackager.sql:229-332`.

All source `file:line` citations refer to that IbsPackager commit unless explicitly
marked **OMP**. Paths are relative to the respective repository. The OMP inspection
baseline is `8fd27ef2db2bd7f4695007b5ca1680f4e3f4d890`.
Read the decision in [ADR 0004](adr/0004-channel-type-reconcile-generalization.md#decision);
use this pinned extraction for implementation details instead of the ADR's historical
line numbers. Supporting platform documents are [Module Definitions](MODULE_DEFINITIONS.md),
[Worker Runtime](WORKER_RUNTIME.md), and [HostAgent](HOST_AGENT.md#worker-manager-integration).

### Module-owned table shape

Preserve the following shape under `omp_<module>`. `?` means nullable; other columns
are NOT NULL. IDs marked identity use `(1,1)`. Timestamp defaults below use
`SYSUTCDATETIME()`. These are source shapes, including the later channel alterations,
not just the columns used by the procedure. Sources: `sql/1-setup-ibspackager.sql:189-225`,
`:336-377`, `:391-429`, `:944-975`, `:1450-1458`.

| Table | Columns and keys to preserve | Source |
| --- | --- | --- |
| `ChannelTypes` | `ChannelTypeId int identity` PK; `ChannelTypeKey nvarchar(100)` unique; `DisplayName nvarchar(200)`; `Description nvarchar(500)?`; `AssemblyPath`, `FactoryTypeName nvarchar(500)`; `DefaultVersion nvarchar(50)?`; `IsEnabled bit = 1`; `CreatedUtc`, `UpdatedUtc datetime2(3)` with timestamp defaults. | `sql/1-setup-ibspackager.sql:189-202` |
| `ChannelTypeVersions` | `ChannelTypeVersionId int identity` PK; `ChannelTypeId int` FK to `ChannelTypes`; `ArtifactId int?` FK to `omp.Artifacts(ArtifactId)`; `Version nvarchar(50)`; `DisplayName nvarchar(200)?`; `AssemblyPath`, `FactoryTypeName nvarchar(500)`; `ConfigSchemaJson nvarchar(max)?`; `IsDefault bit = 0`; `IsEnabled bit = 1`; `CreatedUtc`, `UpdatedUtc datetime2(3)` with timestamp defaults; unique `(ChannelTypeId, Version)`. | `sql/1-setup-ibspackager.sql:208-225` |
| `Channels` | `ChannelId uniqueidentifier` PK; `AppInstanceId uniqueidentifier` FK to `omp.AppInstances`; `WorkerInstanceId uniqueidentifier?` FK to `omp.WorkerInstances`; `ChannelTypeId int` FK to `ChannelTypes`; `PinnedChannelTypeVersionId int?` FK to `ChannelTypeVersions`; `ChannelKey nvarchar(150)`; `DisplayName nvarchar(200)`; `Description nvarchar(500)?`; `ChannelTypeVersion nvarchar(50)?`; `ConfigJson nvarchar(max)`; `DesiredState tinyint = 1`; `IsEnabled bit = 1`; `SortOrder int = 0`; `CreatedUtc`, `UpdatedUtc datetime2(3)` with timestamp defaults; unique `(AppInstanceId, ChannelKey)`. Later additions: `ChannelConfigId uniqueidentifier?`, `ChannelConfigVersion int?`, `IsDeleted bit = 0`, `DeletedUtc datetime2(3)?`. | `sql/1-setup-ibspackager.sql:336-377`, `:1450-1458` |
| `ChannelConfigs` | `ChannelConfigId uniqueidentifier` PK; `ChannelTypeId`, `ChannelTypeVersionId int` with type/version FKs; `ConfigKey nvarchar(150)` unique; `DisplayName nvarchar(200)`; `Description nvarchar(500)?`; `CurrentConfigVersion int = 0`, checked `>= 0`; `IsEnabled bit = 1`; `IsDeleted bit = 0`; `CreatedBy`, `UpdatedBy nvarchar(256)?`; `CreatedUtc`, `UpdatedUtc datetime2(3)` with timestamp defaults. | `sql/1-setup-ibspackager.sql:391-410` |
| `ChannelConfigVersions` | `ChannelConfigVersionId bigint identity` PK; `ChannelConfigId uniqueidentifier` FK to `ChannelConfigs`; `ConfigVersion int`, checked `> 0`; `ConfigJson nvarchar(max)`; `Source nvarchar(50) = N'Web'`; `Comment nvarchar(500)?`; `CreatedBy nvarchar(256)?`; `CreatedUtc datetime2(3)` with timestamp default; unique `(ChannelConfigId, ConfigVersion)`. | `sql/1-setup-ibspackager.sql:416-429` |
| `ChannelRuntimeStates` | `ChannelId uniqueidentifier` PK/FK to `Channels`; `WorkerInstanceId uniqueidentifier?` FK to `omp.WorkerInstances`; `ObservedState tinyint = 0`; `ProcessId int?`; `LastSeenUtc datetime2(3)?`; `LastMessage nvarchar(500)?`; `CreatedUtc`, `UpdatedUtc datetime2(3)` with timestamp defaults. | `sql/1-setup-ibspackager.sql:944-956` |
| `ChannelWorkerLeases` | Composite PK `(ChannelId, WorkerInstanceId)`, both `uniqueidentifier`, with FKs to `Channels` and `omp.WorkerInstances`; `HostName nvarchar(260)`; `ProcessId int?`; `OwnerToken nvarchar(128)?`; `LastSeenUtc datetime2(3)`; `CreatedUtc`, `UpdatedUtc datetime2(3)` with timestamp defaults. | `sql/1-setup-ibspackager.sql:962-975` |

Keep the linked configuration consistency constraints as well: channel/config FK,
both config references null or both non-null, composite type/version FKs, and the
channel/config-version FK. Sources: `sql/1-setup-ibspackager.sql:734-736`, `:791-798`,
`:842-875`. The setup also defines consistency triggers; inspect their complete
bodies when copying the table setup (`sql/1-setup-ibspackager.sql:879-939`).

### Requirement identity and effective state

1. The per-channel key is `<module>.channeltype:<channel-id>`, with the GUID converted
   to `nvarchar(36)`; the reference uses `ibs_packager.channeltype:`. A requirement is
   matched by **both** `HostId` and `RequirementKey`, allowing the same channel key on
   several hosts. Source: `sql/1-setup-ibspackager.sql:261-266`, `:319-330`.
2. Choose an enabled version of the same channel type. With a non-null pin, only that
   version qualifies. With no pin, only enabled defaults qualify. Resolve ties with
   the exact source ordering. An unavailable/disabled pin does **not** fall back to
   default in this procedure; a null artifact produces no effective tuple.
   Source: `sql/1-setup-ibspackager.sql:271-279`, `:294-295`.
3. A concrete placement uses `COALESCE(ai.HostId, wi.HostId)` (app host first).
   Only when both are null, expand `ai.TargetHostTemplateId` through
   `omp.HostDeploymentAssignments` with matching `HostTemplateId` and `IsActive = 1`.
   This checks assignment activity; it does not join `omp.Hosts` or inspect host
   health. With neither a concrete host nor a matching assignment there is no tuple.
   Source: `sql/1-setup-ibspackager.sql:269-270`, `:280-295`.
4. A tuple is active exactly when `Channels.IsEnabled = 1 AND DesiredState = 1`.
   The procedure does not read `Channels.IsDeleted`, `ChannelTypes.IsEnabled`, app or
   worker enable flags, or `Artifacts.IsEnabled`. Soft deletion explicitly stops and
   disables the channel in the repository. Sources: `sql/1-setup-ibspackager.sql:261-295`;
   `IbsPackager.Runtime/Services/IbsPackagerRepository.cs:3416-3423`.
5. Disable enabled requirements selected by the module prefix when no active tuple
   matches key, host and artifact. Thus deleted, disabled, stopped, unplaced or
   unresolved channels and obsolete host/artifact mappings lose their enabled
   requirement. Then `MERGE ... WITH (HOLDLOCK)` re-enables/updates matches or inserts
   active rows. Keep `SET NOCOUNT ON`, `SET XACT_ABORT ON`, the shared timestamp and
   `HOLDLOCK`. The procedure has no explicit encompassing transaction; repeated runs
   preserve desired state but refresh timestamps. Source:
   `sql/1-setup-ibspackager.sql:242-245`, `:297-330`.
6. Preserve the literal SQL `LIKE` behavior in the template. The reference prefix
   contains an underscore and does not escape it in `LIKE`; literal namespace
   isolation beyond those SQL matching semantics is not proven here.
   Source: `sql/1-setup-ibspackager.sql:303`.

### Every table and column used by the procedure

This inventory covers `ALTER PROCEDURE` through its terminating `GO`, including
the table variable; metadata checks in the preceding creation guard are separate.
Source: `sql/1-setup-ibspackager.sql:233-332`.

| Object | Reads | Writes | Source |
| --- | --- | --- | --- |
| `omp_<module>.Channels` | `ChannelId`, `AppInstanceId`, `WorkerInstanceId`, `ChannelTypeId`, `PinnedChannelTypeVersionId`, `IsEnabled`, `DesiredState` | None | `sql/1-setup-ibspackager.sql:261-278` |
| `omp_<module>.ChannelTypeVersions` | `ChannelTypeId`, `ChannelTypeVersionId`, `ArtifactId`, `IsEnabled`, `IsDefault` | None | `sql/1-setup-ibspackager.sql:273-278` |
| `omp.AppInstances` | `AppInstanceId`, `HostId`, `TargetHostTemplateId` | None | `sql/1-setup-ibspackager.sql:269`, `:282-291` |
| `omp.WorkerInstances` | `WorkerInstanceId`, `HostId` | None | `sql/1-setup-ibspackager.sql:270`, `:282-289` |
| `omp.HostDeploymentAssignments` | `HostId`, `HostTemplateId`, `IsActive` | None | `sql/1-setup-ibspackager.sql:287-292` |
| `omp.HostArtifactRequirements` | `RequirementKey`, `HostId`, `ArtifactId`, `IsEnabled` | Update: `IsEnabled`, `UpdatedUtc`, `ArtifactId`; insert: `HostId`, `ArtifactId`, `RequirementKey`, `IsEnabled`, `CreatedUtc`, `UpdatedUtc` | `sql/1-setup-ibspackager.sql:300-330` |
| `@Effective` (table variable) | `RequirementKey`, `HostId`, `ArtifactId`, `IsActive` | Insert: `ChannelId`, `HostId`, `ArtifactId`, `RequirementKey`, `IsActive` | `sql/1-setup-ibspackager.sql:252-267`, `:307-324` |

### HostAgent requirements, retention and RPC boundary

The producer writes an artifact FK and a module-prefixed key; automatic version
advance also maintains a separate baseline key `channel-type:<target>:<version>`.
Keep those identities distinct. Sources: `sql/1-setup-ibspackager.sql:266`, `:319-330`;
`IbsPackager.Runtime/Services/IbsPackagerRepository.cs:3302-3327`.

**OMP consumer verification:** the inspected explicit-requirement query passes through
`RequirementKey` and filters host, enabled requirement, enabled artifact and nonempty
artifact path. Its retention logic protects enabled requirements and removes disabled
ones for artifacts selected for deletion, using `ArtifactId`/`IsEnabled` rather than a
`channeltype:` prefix. External module FKs to `omp.Artifacts` are discovered separately
and protect referenced artifacts even after a requirement is disabled. IbsPackager
provides that FK (`sql/1-setup-ibspackager.sql:223`). OMP evidence at the baseline above:
`OpenModulePlatform.HostAgent.Runtime/Services/OmpHostArtifactRepository.cs:2555-2558`,
`:2697-2700`, `:3844-3861`, and
`OpenModulePlatform.HostAgent.Runtime/Services/ArtifactRetentionProtectedReferences.cs:31-62`,
`:76-85`. There is no module-prefix parsing in these inspected paths.

When no provisioned path is available, the worker can use the existing
`EnsureArtifactAsync` call, subject to an artifact ID and enabled RPC client
(`IbsPackager.Runtime/Services/IbsPackagerWorkerEngine.cs:60-117`); see
[HostAgent's existing RPC](HOST_AGENT.md#implemented). Placements without any host
mapping produce no rows in the SQL procedure (`sql/1-setup-ibspackager.sql:280-295`).
Do not infer a new host-neutral provisioning protocol from this template; its
implementation is **not verified** here (see the final section).

### Complete production call-site and mutation inventory

For source parity, preserve each operation below and its actual mechanism. A direct
requirements cleanup and a call to the version service are explicitly distinguished
from a stored-procedure call. The repository wrapper executes the procedure at
`IbsPackager.Runtime/Services/IbsPackagerRepository.cs:2787-2794`.

| Trigger | Web/runtime entry and exact downstream call site | Mechanism |
| --- | --- | --- |
| Save channel, including placement, pin and state edits | `IbsPackager.Web/Pages/Channels/Index.cshtml.cs:413`; `IbsPackager.Runtime/Services/IbsPackagerRepository.cs:2568` | `SaveChannelAsync` calls the wrapper after save and permission sync. Its transaction also writes the saved channel's requirement directly at `IbsPackager.Runtime/Services/IbsPackagerRepository.cs:2519-2538`; keep the later global reconcile for complete placement expansion. |
| Enable or disable channel | `IbsPackager.Web/Pages/Channels/Index.cshtml.cs:306`; `IbsPackager.Runtime/Services/IbsPackagerRepository.cs:2684` | `SetChannelEnabledAsync` calls the wrapper after the mutation. |
| Start or stop desired state | `IbsPackager.Web/Pages/Channels/Index.cshtml.cs:332`; `IbsPackager.Runtime/Services/IbsPackagerRepository.cs:2784` | `SetChannelDesiredStateAsync` calls the wrapper after the mutation. |
| Delete channel | `IbsPackager.Web/Pages/Channels/Index.cshtml.cs:461`; `IbsPackager.Runtime/Services/IbsPackagerRepository.cs:3400`, `:3491` | `DeleteChannelAsync` directly deletes requirements for the exact channel key in the deletion transaction. Soft deletion disables/stops the channel (`:3416-3423`); hard deletion removes it (`:3487`). No procedure call occurs before the return (`:3528-3539`). |
| Save channel-type version, change artifact, enable flag or default | `IbsPackager.Web/Pages/Channels/Index.cshtml.cs:671-682`; `IbsPackager.Runtime/Services/IbsPackagerRepository.cs:3707` | `SaveChannelTypeVersionAsync` calls the wrapper. `IsDefault` is part of this save; existing default flags are cleared at `IbsPackager.Runtime/Services/IbsPackagerRepository.cs:3666-3670`. |
| Delete channel-type version, including default reassignment | `IbsPackager.Web/Pages/Channels/Index.cshtml.cs:718`; `IbsPackager.Runtime/Services/IbsPackagerRepository.cs:3776` | `DeleteChannelTypeVersionAsync` calls the wrapper. Pinned/last-version deletion is rejected (`:3727-3736`); default replacement happens inside the transaction (`:3746-3768`). |
| Portal Reconcile button | `IbsPackager.Web/Pages/Channels/Index.cshtml.cs:512-523` | Admin-guarded direct wrapper call. The displayed count is prefix rows updated since five seconds before the call (`:522-536`), not a count returned by the procedure or proof of artifact deployment. |
| Recover unloadable pins | `IbsPackager.Runtime/Services/ChannelTypeVersionReconciliationService.cs:164`; `IbsPackager.Runtime/Services/IbsPackagerRepository.cs:3043-3044` | `RecoverUnloadableChannelTypePinsAsync` executes the procedure after commit only when `@recovered > 0`. |
| Advance default to registered artifact | `IbsPackager.Runtime/Services/ChannelTypeVersionReconciliationService.cs:72`; `IbsPackager.Runtime/Services/IbsPackagerRepository.cs:3331` | `AdvanceChannelTypeDefaultVersionAsync` executes the procedure after its transaction commits. |
| Worker start and interval | `IbsPackager.Runtime/Services/IbsPackagerWorkerEngine.cs:201`, `:928`; injected by `IbsPackager.Worker/IbsPackagerWorkerFactory.cs:30` | Starts the version-reconciliation loop beside the channel loop. First pass precedes the timer wait; disabled when the service is absent or interval is nonpositive (`IbsPackager.Runtime/Services/IbsPackagerWorkerEngine.cs:914-939`). Requirements are reconciled through recovery/advance above, not unconditionally on every worker start. |
| Web startup and interval | `IbsPackager.Web/Program.cs:32`; `IbsPackager.Web/Services/ChannelTypeVersionReconciliationHostedService.cs:39` | Registered hosted service invokes version reconciliation when enabled, then waits for its configured interval (`IbsPackager.Web/Services/ChannelTypeVersionReconciliationHostedService.cs:30-61`). |
| Initialization SQL, before and after final channel version mapping | `sql/2-initialize-ibspackager.sql:421`, `:1575` | Two explicit procedure executions; preserve the final pass after pin/default mapping. |

Version reconciliation attempts pin recovery before querying advance candidates or
returning early. Recovery mode can suppress pin recovery. Thus a zero return count
means no default advanced; it does not prove that no pin or requirement changed.
Sources: `IbsPackager.Runtime/Services/ChannelTypeVersionReconciliationService.cs:34-64`,
`:119`, `:137-164`;
`IbsPackager.Tests/Integration/ChannelTypeVersionReconciliationIntegrationTests.cs:158-173`.

Outside production, the synthetic development seed also executes the procedure at
`scripts/dev-seed-data.ps1:488`, in the SQL passed to the runner at
`scripts/dev-seed-data.ps1:491`.

The following test executions complete the direct-call inventory outside production:
wrapper calls at `IbsPackager.Tests/ReconcileChannelTypeArtifactRequirementsTests.cs:333`,
`:347`, `:360`, `:373`; version-service calls at
`IbsPackager.Tests/Integration/ChannelTypeVersionReconciliationIntegrationTests.cs:46`,
`:107`, `:119`, `:126`, `:160`, `:171`, `:222`, `:278`; and the simulated-import SQL
procedure execution at `IbsPackager.Tests/Integration/ChannelTypeVersionReconciliationIntegrationTests.cs:374`.
The procedure-creation stub itself is DDL, not reconciliation
(`sql/1-setup-ibspackager.sql:233-237`).

### Module-definition integrity

- Declare **every** setup `CREATE TABLE` in `integrity.requiredTables`, with its own
  module schema, source and purpose. This is the R12-G4 invariant recorded in
  `ibs_packager.module-definition.json:129`. The reference has 22 entries at
  `ibs_packager.module-definition.json:133-266`: `ValidationLists`, `MappingSets`,
  `AllowedListValues`, `ValueMappings`, `ChannelTypes`, `ChannelTypeVersions`,
  `Channels`, `ChannelConfigs`, `ChannelConfigVersions`, `ChannelRuntimeStates`,
  `ChannelWorkerLeases`, `Jobs`, `JobEvents`, `ManualReviewItems`,
  `ManualReviewCommands`, `ManualReviewFiles`, `Settings`, `JobOutputBatches`,
  `MaintenanceRequests`, `TestDocumentRequests`, `JobStateChangeAudit`, `ActivityLog`.
  Apply the completeness rule to the new module's actual setup tables; these names
  inventory the reference module's domain, not extra domain features to implement.
- Preserve `integrity.requiredModuleRows.channelTypes` entries with
  `channelTypeKey`, `assemblyPath` and `factoryTypeName`, and the accompanying
  `channelTypeVersions` expectations (`channelTypeKey`, `artifactPackageType`,
  `artifactTargetName`, `isDefault`). The pinned reference declares one required
  channel type, `file_drop_to_ibs`; do not claim every plugin appears in that array.
  Source: `ibs_packager.module-definition.json:399-414`.
- Declare the worker and its channel-type artifacts together in
  `consistentArtifactSets`, using each artifact's `appKey`, `packageType` and
  `targetName`, with `versionMatchRule: "exact"`. The reference set
  `worker-channel-types` contains the worker and both `file-drop` and `image-compose`
  channel-type artifacts. Source: `ibs_packager.module-definition.json:66-88`.
- Set `allowMultipleActiveInstances: true` on the worker app declaration, as in
  `ibs_packager.module-definition.json:23-29`. Preserve per-worker channel lease
  identity (`sql/1-setup-ibspackager.sql:964-974`).

### Tests every adopting module must duplicate

Use the same scenario names with module-specific fixtures. The first two groups
are database tests, and their skip guards mean a skipped run is not execution
evidence (`IbsPackager.Tests/ReconcileChannelTypeArtifactRequirementsTests.cs:327-373`;
`IbsPackager.Tests/Integration/ChannelTypeVersionReconciliationIntegrationTests.cs:25-30`).

| Reference test and scenario | Source |
| --- | --- |
| `Reconcile_UpsertsRequirement_ForEnabledRunningChannel`: correct an existing requirement to the effective artifact and enable it. | `IbsPackager.Tests/ReconcileChannelTypeArtifactRequirementsTests.cs:328-337` |
| `Reconcile_DisablesRequirements_ForDisabledChannel`: retained row becomes disabled. | `IbsPackager.Tests/ReconcileChannelTypeArtifactRequirementsTests.cs:341-350` |
| `Reconcile_DisablesRequirements_ForStoppedDesiredState`: retained row becomes disabled. | `IbsPackager.Tests/ReconcileChannelTypeArtifactRequirementsTests.cs:354-363` |
| `Reconcile_UpsertsNewRequirement_WhenPinnedVersionChanges`: requirement follows the new pinned artifact. | `IbsPackager.Tests/ReconcileChannelTypeArtifactRequirementsTests.cs:367-377` |
| `Reconcile_AdvancesDefault_MovesFollowers_KeepsPins_RetiresRows_RepointsRequirements`: move followers and linked config, preserve the eligible explicit older pin, copy schema/loader identity, retire the old default, move baseline requirements, and return zero on a second pass. | `IbsPackager.Tests/Integration/ChannelTypeVersionReconciliationIntegrationTests.cs:37-107` |
| `Reconcile_WaitsForTheMatchingWorkerArtifact`: channel-type arrival alone is insufficient when the app ships workers; matching worker arrival permits advance. | `IbsPackager.Tests/Integration/ChannelTypeVersionReconciliationIntegrationTests.cs:111-129` |
| `Reconcile_RecoversAPinTheWorkerCannotLoad`: recover an incompatible pin even without a default advance; repeat safely. | `IbsPackager.Tests/Integration/ChannelTypeVersionReconciliationIntegrationTests.cs:148-173` |
| `Reconcile_RecoversAPinTheWorkerCannotLoad_EvenWithNoAdvanceCandidates`: prove the candidate list is empty and recovery still occurs. | `IbsPackager.Tests/Integration/ChannelTypeVersionReconciliationIntegrationTests.cs:197-230` |
| `Reconcile_PinRecovery_RepointsPerChannelRequirements_AcrossTwoConsecutiveImports`: simulate two imports; assert a single enabled artifact version and the new per-channel artifact after each. | `IbsPackager.Tests/Integration/ChannelTypeVersionReconciliationIntegrationTests.cs:263-302` |
| `FileDropSeedSchemaMatchesTheFactorySchema` and `ImageComposeSeedSchemaMatchesTheFactorySchema`: adapt one test per plugin; compare factory/seed JSON canonically and reject duplicate properties in either copy. These are parity unit tests. | `IbsPackager.Tests/ChannelTypeSchemaParityTests.cs:25-59`, `:68-88` |

## SQL template

This is the complete creation guard and procedure from
`sql/1-setup-ibspackager.sql:233-332`, with only two substitutions:
`omp_ibs_packager` becomes `omp_<module>` and the key prefix
`ibs_packager.channeltype:` becomes `<module>.channeltype:`. Replace `<module>` with
the adopting module key before execution. No predicate, statement, ordering or
locking change is part of the template.

```sql
IF OBJECT_ID(N'omp_<module>.usp_ReconcileChannelTypeArtifactRequirements', N'P') IS NULL
BEGIN
    EXEC(N'CREATE PROCEDURE omp_<module>.usp_ReconcileChannelTypeArtifactRequirements AS SET NOCOUNT ON; SELECT 1;');
END
GO

ALTER PROCEDURE omp_<module>.usp_ReconcileChannelTypeArtifactRequirements
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @nowUtc datetime2(3) = SYSUTCDATETIME();

    -- Effective (channel, host, artifact) tuples. Concrete placements resolve
    -- to their pinned host; role-based placements (both instance HostIds NULL,
    -- the app instance targets a host template) expand to every active host
    -- assigned to that template, so each role host gets the channel-type
    -- artifact provisioned.
    DECLARE @Effective TABLE
    (
        ChannelId uniqueidentifier NOT NULL,
        HostId uniqueidentifier NOT NULL,
        ArtifactId int NOT NULL,
        RequirementKey nvarchar(200) NOT NULL,
        IsActive bit NOT NULL
    );

    INSERT INTO @Effective (ChannelId, HostId, ArtifactId, RequirementKey, IsActive)
    SELECT
        c.ChannelId,
        hostSet.HostId,
        eff.ArtifactId,
        CONCAT(N'<module>.channeltype:', CONVERT(nvarchar(36), c.ChannelId)),
        CASE WHEN c.IsEnabled = 1 AND c.DesiredState = 1 THEN 1 ELSE 0 END
    FROM omp_<module>.Channels c
    LEFT JOIN omp.AppInstances ai ON ai.AppInstanceId = c.AppInstanceId
    LEFT JOIN omp.WorkerInstances wi ON wi.WorkerInstanceId = c.WorkerInstanceId
    OUTER APPLY
    (
        SELECT TOP (1) v.ArtifactId
        FROM omp_<module>.ChannelTypeVersions v
        WHERE v.ChannelTypeId = c.ChannelTypeId
          AND v.IsEnabled = 1
          AND (v.ChannelTypeVersionId = c.PinnedChannelTypeVersionId OR (c.PinnedChannelTypeVersionId IS NULL AND v.IsDefault = 1))
        ORDER BY CASE WHEN v.ChannelTypeVersionId = c.PinnedChannelTypeVersionId THEN 0 ELSE 1 END, v.IsDefault DESC, v.ChannelTypeVersionId DESC
    ) eff
    OUTER APPLY
    (
        SELECT COALESCE(ai.HostId, wi.HostId) AS HostId
        WHERE COALESCE(ai.HostId, wi.HostId) IS NOT NULL

        UNION ALL

        SELECT hda.HostId
        FROM omp.HostDeploymentAssignments hda
        WHERE COALESCE(ai.HostId, wi.HostId) IS NULL
          AND ai.TargetHostTemplateId IS NOT NULL
          AND hda.HostTemplateId = ai.TargetHostTemplateId
          AND hda.IsActive = 1
    ) hostSet
    WHERE eff.ArtifactId IS NOT NULL
      AND hostSet.HostId IS NOT NULL;

    -- Disable requirements that no longer match an active channel/host/artifact
    -- tuple. Rows whose artifact moved are re-enabled with the new artifact by
    -- the merge below.
    UPDATE r
    SET IsEnabled = 0, UpdatedUtc = @nowUtc
    FROM omp.HostArtifactRequirements r
    WHERE r.RequirementKey LIKE N'<module>.channeltype:%'
      AND r.IsEnabled = 1
      AND NOT EXISTS
      (
          SELECT 1
          FROM @Effective e
          WHERE e.RequirementKey = r.RequirementKey
            AND e.HostId = r.HostId
            AND e.ArtifactId = ISNULL(r.ArtifactId, -1)
            AND e.IsActive = 1
      );

    -- Upsert requirements for active channels on every effective host.
    -- HOLDLOCK: this procedure runs from both the web save path and the worker
    -- reconciliation, and without it two concurrent runs could both take the
    -- NOT MATCHED branch and collide on the key (R2-ST8).
    MERGE omp.HostArtifactRequirements WITH (HOLDLOCK) AS target
    USING (
        SELECT HostId, ArtifactId, RequirementKey
        FROM @Effective
        WHERE IsActive = 1
    ) AS source
    ON target.HostId = source.HostId AND target.RequirementKey = source.RequirementKey
    WHEN MATCHED THEN
        UPDATE SET ArtifactId = source.ArtifactId, IsEnabled = 1, UpdatedUtc = @nowUtc
    WHEN NOT MATCHED THEN
        INSERT (HostId, ArtifactId, RequirementKey, IsEnabled, CreatedUtc, UpdatedUtc)
        VALUES (source.HostId, source.ArtifactId, source.RequirementKey, 1, @nowUtc, @nowUtc);
END
GO
```

## Checklist: how to prove module parity

- [ ] Compare the template with the pinned procedure after reversing only the schema
  and key-prefix substitutions; preserve all predicates, `HOLDLOCK` and ordering.
  Source: `sql/1-setup-ibspackager.sql:233-332`.
- [ ] Verify the module-owned table shapes, artifact FK, configuration relationships
  and composite lease key against `sql/1-setup-ibspackager.sql:189-225`, `:336-429`,
  `:734-939`, `:944-975`, `:1450-1458`.
- [ ] Inventory all setup `CREATE TABLE` statements and compare their names with
  `integrity.requiredTables`; verify required channel rows, exact worker/plugin
  artifact sets and multiple worker instances. Source:
  `ibs_packager.module-definition.json:23-29`, `:66-88`, `:129-266`, `:399-414`.
- [ ] Map each production row in the call-site inventory to the adopting module,
  including direct deletion cleanup, both initialization calls and both version
  recovery/advance paths. Sources:
  `IbsPackager.Runtime/Services/IbsPackagerRepository.cs:2568`, `:2684`, `:2784`,
  `:3044`, `:3331`, `:3491`, `:3707`, `:3776`;
  `IbsPackager.Web/Pages/Channels/Index.cshtml.cs:523`;
  `IbsPackager.Runtime/Services/IbsPackagerWorkerEngine.cs:201`, `:928`;
  `IbsPackager.Web/Services/ChannelTypeVersionReconciliationHostedService.cs:39`;
  `sql/2-initialize-ibspackager.sql:421`, `:1575`.
- [ ] Execute the nine named database scenarios and the two schema-parity scenarios
  adapted per plugin; record executed and skipped counts separately. Sources:
  `IbsPackager.Tests/ReconcileChannelTypeArtifactRequirementsTests.cs:327-377`;
  `IbsPackager.Tests/Integration/ChannelTypeVersionReconciliationIntegrationTests.cs:25-302`;
  `IbsPackager.Tests/ChannelTypeSchemaParityTests.cs:25-59`.
- [ ] Compare concrete-host, role-assignment and stale-row results with the SQL
  predicates, including missing/disabled pins and removed assignments. These checks
  derive from the procedure; they are not claimed as named reference test coverage.
  Source: `sql/1-setup-ibspackager.sql:261-330`.
- [ ] Check per-channel and baseline keys separately after two successive imports;
  do not use the Portal's recent-update count as deployment proof. Sources:
  `IbsPackager.Tests/Integration/ChannelTypeVersionReconciliationIntegrationTests.cs:263-302`;
  `IbsPackager.Web/Pages/Channels/Index.cshtml.cs:522-544`.

## Not verified

- Runtime execution of the extracted SQL, an adopting module, artifact provisioning,
  or customer deployment is not verified by this documentation extraction. The
  cited tests describe required scenarios, not test results from this task.
- A mandatory unconditional stored-procedure call on channel deletion or every
  worker start is not supported by the inspected source. Deletion uses direct
  cleanup; worker start invokes a conditional version service. Evidence:
  `IbsPackager.Runtime/Services/IbsPackagerRepository.cs:3491`, `:3528-3539`;
  `IbsPackager.Runtime/Services/IbsPackagerWorkerEngine.cs:201`, `:914-939`;
  `IbsPackager.Runtime/Services/ChannelTypeVersionReconciliationService.cs:34-76`.
- Named integration coverage for role expansion, stale host-assignment removal,
  missing/disabled-pin behavior, prefix wildcard collisions and concurrent procedure
  execution is not established by the listed reference scenarios. The SQL behavior
  is inspectable at `sql/1-setup-ibspackager.sql:261-330`; the named test evidence is
  `IbsPackager.Tests/ReconcileChannelTypeArtifactRequirementsTests.cs:327-377` and
  `IbsPackager.Tests/Integration/ChannelTypeVersionReconciliationIntegrationTests.cs:36-302`.
- A new host-neutral RPC protocol and a blanket retention guarantee based on the
  key prefix are not established. The verified producer is host-mapped SQL
  (`sql/1-setup-ibspackager.sql:280-295`), and the existing worker RPC call is at
  `IbsPackager.Runtime/Services/IbsPackagerWorkerEngine.cs:104-117`; the separately
  pinned OMP consumer evidence is listed above.

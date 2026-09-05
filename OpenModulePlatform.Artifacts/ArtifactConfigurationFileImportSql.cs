namespace OpenModulePlatform.Artifacts;

/// <summary>
/// Shared T-SQL for registering package-delivered artifact configuration files
/// and for carrying operator-edited configuration content forward to a newly
/// imported artifact version. HostAgent import, Portal upload/import, and the
/// Bootstrapper must all use these statements so the preservation semantics
/// stay identical across import paths.
///
/// Content comparisons cast nvarchar(max) to varbinary(max) so they are exact
/// (case- and whitespace-sensitive) regardless of database collation.
/// </summary>
public static class ArtifactConfigurationFileImportSql
{
    /// <summary>
    /// Upserts one package-delivered configuration file row.
    /// Parameters: @ArtifactId, @RelativePath, @FileContent.
    ///
    /// The package content always becomes the new PackageFileContent baseline.
    /// When the row already exists with a known baseline and the incoming
    /// package content is unchanged against that baseline, the operator-edited
    /// FileContent and IsEnabled values are kept. In every other case the
    /// package content wins, which matches the pre-baseline behavior.
    /// </summary>
    public const string UpsertPackageConfigurationFile = @"
-- UPDLOCK + SERIALIZABLE on the probing UPDATE so two concurrent imports of the
-- same artifact (the standard multi-host rollout against one database) cannot
-- both see zero updated rows and both INSERT — the loser otherwise hit the
-- unique index and failed the whole import (R4-D3). The range lock is held to
-- the end of the enclosing transaction, serializing the insert path per
-- (ArtifactId, RelativePath).
UPDATE omp.ArtifactConfigurationFiles WITH (UPDLOCK, SERIALIZABLE)
SET FileContent = CASE
        WHEN PackageFileContent IS NOT NULL
             AND CAST(PackageFileContent AS varbinary(max)) = CAST(@FileContent AS varbinary(max))
            THEN FileContent
        ELSE @FileContent
    END,
    IsEnabled = CASE
        WHEN PackageFileContent IS NOT NULL
             AND CAST(PackageFileContent AS varbinary(max)) = CAST(@FileContent AS varbinary(max))
            THEN IsEnabled
        ELSE 1
    END,
    PackageFileContent = @FileContent,
    UpdatedUtc = SYSUTCDATETIME()
WHERE ArtifactId = @ArtifactId
  AND RelativePath = @RelativePath;

IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO omp.ArtifactConfigurationFiles
    (
        ArtifactId,
        RelativePath,
        FileContent,
        PackageFileContent,
        IsEnabled
    )
    VALUES
    (
        @ArtifactId,
        @RelativePath,
        @FileContent,
        @FileContent,
        1
    );
END;";

    /// <summary>
    /// Deletes one configuration file row that is no longer part of the package.
    /// Parameters: @ArtifactId, @RelativePath.
    /// </summary>
    public const string DeleteConfigurationFileByPath = @"
DELETE FROM omp.ArtifactConfigurationFiles
WHERE ArtifactId = @ArtifactId
  AND RelativePath = @RelativePath;";

    /// <summary>
    /// Reads the relative paths currently registered for one artifact.
    /// Parameters: @ArtifactId.
    /// </summary>
    public const string SelectConfigurationFilePaths = @"
SELECT RelativePath
FROM omp.ArtifactConfigurationFiles
WHERE ArtifactId = @ArtifactId;";

    /// <summary>
    /// Resolves the configuration continuity source for the artifact identified
    /// by @ArtifactId. Declares and fills @AppId, @PackageType, @TargetName,
    /// @SourceArtifactId, @SourceVersion and the @FallbackRows table variable;
    /// include it verbatim at the top of a batch that copies or carries
    /// configuration forward.
    ///
    /// Primary source: the artifact the slot's pointers still reference -- app
    /// instances (omp.AppInstances.ArtifactId), instance-template rows
    /// (omp.InstanceTemplateAppInstances.DesiredArtifactId), and the last
    /// successful deployment states (omp.HostAppDeploymentStates with
    /// DeploymentState = 2, HostDeploymentStatuses.Succeeded). Pointers are
    /// repointed only after the import registers configuration, so at this
    /// point they name the artifact whose configuration is actually live. When
    /// several pointers disagree, the most recently created pointed-to artifact
    /// with configuration rows wins.
    ///
    /// Fallback (no pointer names a source): search ALL previous enabled
    /// versions in the slot and take, per relative path, the newest row that
    /// carries an operator delta (content differs from the package baseline, no
    /// baseline at all, or operator-disabled); a path without any delta takes
    /// the newest row that exists, so pristine files keep their continuity too.
    /// </summary>
    public const string ResolveContinuitySource = @"
DECLARE @AppId int;
DECLARE @PackageType nvarchar(100);
DECLARE @TargetName nvarchar(200);
DECLARE @SourceArtifactId int;
DECLARE @SourceVersion nvarchar(50);
DECLARE @FallbackRows TABLE
(
    RelativePath nvarchar(400) NOT NULL PRIMARY KEY,
    FileContent nvarchar(max) NOT NULL,
    PackageFileContent nvarchar(max) NULL,
    IsEnabled bit NOT NULL,
    SourceVersion nvarchar(50) NULL,
    SourceCreatedUtc datetime2(3) NULL,
    SourceArtifactId int NULL
);

SELECT @AppId = AppId,
       @PackageType = PackageType,
       @TargetName = TargetName
FROM omp.Artifacts
WHERE ArtifactId = @ArtifactId;

SELECT TOP (1)
       @SourceArtifactId = source.ArtifactId,
       @SourceVersion = source.Version
FROM
(
    SELECT ai.ArtifactId
    FROM omp.AppInstances ai
    WHERE ai.AppId = @AppId
      AND ai.ArtifactId IS NOT NULL

    UNION

    SELECT tai.DesiredArtifactId
    FROM omp.InstanceTemplateAppInstances tai
    WHERE tai.AppId = @AppId
      AND tai.DesiredArtifactId IS NOT NULL

    UNION

    -- DeploymentState 2 = HostDeploymentStatuses.Succeeded: the last artifact
    -- this host actually deployed for the app instance.
    SELECT deployment.ArtifactId
    FROM omp.HostAppDeploymentStates deployment
    INNER JOIN omp.AppInstances deployedApp
        ON deployedApp.AppInstanceId = deployment.AppInstanceId
    WHERE deployedApp.AppId = @AppId
      AND deployment.ArtifactId IS NOT NULL
      AND deployment.DeploymentState = 2
) pointed
INNER JOIN omp.Artifacts source
    ON source.ArtifactId = pointed.ArtifactId
WHERE source.ArtifactId <> @ArtifactId
  AND source.AppId = @AppId
  AND source.PackageType = @PackageType
  AND ISNULL(source.TargetName, N'') = ISNULL(@TargetName, N'')
  AND source.IsEnabled = 1
  AND EXISTS
  (
      SELECT 1
      FROM omp.ArtifactConfigurationFiles sourceFile
      WHERE sourceFile.ArtifactId = source.ArtifactId
  )
ORDER BY source.CreatedUtc DESC, source.ArtifactId DESC;

IF @SourceArtifactId IS NULL
BEGIN
    ;WITH ranked AS
    (
        SELECT sourceFile.RelativePath,
               sourceFile.FileContent,
               sourceFile.PackageFileContent,
               sourceFile.IsEnabled,
               source.Version AS SourceVersion,
               source.CreatedUtc AS SourceCreatedUtc,
               source.ArtifactId AS SourceArtifactId,
               ROW_NUMBER() OVER
               (
                   PARTITION BY sourceFile.RelativePath
                   ORDER BY CASE
                                WHEN sourceFile.PackageFileContent IS NULL
                                     OR CAST(sourceFile.FileContent AS varbinary(max)) <> CAST(sourceFile.PackageFileContent AS varbinary(max))
                                     OR sourceFile.IsEnabled = 0
                                    THEN 0
                                ELSE 1
                            END,
                            source.CreatedUtc DESC,
                            source.ArtifactId DESC
               ) AS PathRank
        FROM omp.Artifacts source
        INNER JOIN omp.ArtifactConfigurationFiles sourceFile
            ON sourceFile.ArtifactId = source.ArtifactId
        WHERE source.ArtifactId <> @ArtifactId
          AND source.AppId = @AppId
          AND source.PackageType = @PackageType
          AND ISNULL(source.TargetName, N'') = ISNULL(@TargetName, N'')
          AND source.IsEnabled = 1
    )
    INSERT INTO @FallbackRows
    (
        RelativePath,
        FileContent,
        PackageFileContent,
        IsEnabled,
        SourceVersion,
        SourceCreatedUtc,
        SourceArtifactId
    )
    SELECT RelativePath,
           FileContent,
           PackageFileContent,
           IsEnabled,
           SourceVersion,
           SourceCreatedUtc,
           SourceArtifactId
    FROM ranked
    WHERE PathRank = 1;

    SELECT TOP (1) @SourceVersion = SourceVersion
    FROM @FallbackRows
    ORDER BY SourceCreatedUtc DESC, SourceArtifactId DESC;
END;";

    /// <summary>
    /// Copies the continuity source's configuration rows onto the artifact
    /// identified by @ArtifactId, skipping paths the artifact already has.
    /// Used when the imported package ships no configuration files at all.
    /// Result set: SourceArtifactId (NULL when the per-path fallback supplied
    /// the rows), SourceVersion, CopiedCount.
    /// </summary>
    public const string CopyConfigurationFilesFromContinuitySource = ResolveContinuitySource + @"

IF @SourceArtifactId IS NOT NULL
BEGIN
    INSERT INTO omp.ArtifactConfigurationFiles
    (
        ArtifactId,
        RelativePath,
        FileContent,
        PackageFileContent,
        IsEnabled
    )
    SELECT @ArtifactId,
           sourceFile.RelativePath,
           sourceFile.FileContent,
           sourceFile.PackageFileContent,
           sourceFile.IsEnabled
    FROM omp.ArtifactConfigurationFiles sourceFile
    WHERE sourceFile.ArtifactId = @SourceArtifactId
      AND NOT EXISTS
      (
          SELECT 1
          FROM omp.ArtifactConfigurationFiles targetFile
          WHERE targetFile.ArtifactId = @ArtifactId
            AND targetFile.RelativePath = sourceFile.RelativePath
      );

    SELECT @SourceArtifactId AS SourceArtifactId,
           @SourceVersion AS SourceVersion,
           @@ROWCOUNT AS CopiedCount;
END;
ELSE
BEGIN
    INSERT INTO omp.ArtifactConfigurationFiles
    (
        ArtifactId,
        RelativePath,
        FileContent,
        PackageFileContent,
        IsEnabled
    )
    SELECT @ArtifactId,
           fallback.RelativePath,
           fallback.FileContent,
           fallback.PackageFileContent,
           fallback.IsEnabled
    FROM @FallbackRows fallback
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM omp.ArtifactConfigurationFiles targetFile
        WHERE targetFile.ArtifactId = @ArtifactId
          AND targetFile.RelativePath = fallback.RelativePath
    );

    SELECT CAST(NULL AS int) AS SourceArtifactId,
           @SourceVersion AS SourceVersion,
           @@ROWCOUNT AS CopiedCount;
END;";

    /// <summary>
    /// Carries operator-edited configuration content forward from the
    /// continuity source of the artifact identified by @ArtifactId -- the
    /// artifact the slot's pointers referenced before the import, or, when no
    /// pointer names a source, the newest operator-delta row per relative path
    /// across all previous enabled versions (see
    /// <see cref="ResolveContinuitySource"/>) -- and reports one row per
    /// configuration file whose continuity needs operator attention.
    ///
    /// Result set columns: SourceVersion, RelativePath, Outcome
    /// ('Preserved' | 'PreservedWithoutBaseline' | 'Conflict' | 'MissingInPackage').
    /// Empty when there is no source row at all, or nothing worth reporting.
    ///
    /// A row is carried forward only when the previous row has a known package
    /// baseline, the operator changed it (content or IsEnabled), and the new
    /// package content is unchanged against that baseline (three-way rule).
    /// Rows without a baseline are operator-owned content with no package to
    /// compare against; they are carried forward when the target row is still
    /// pristine package content, and reported as Conflict only when the target
    /// itself already carries an operator edit that must not be overwritten.
    /// </summary>
    public const string CarryForwardOperatorEdits = ResolveContinuitySource + @"

-- The source rows the classification below compares against: every row of the
-- pointer-designated source artifact, or the per-path fallback rows when no
-- pointer named a source.
DECLARE @SourceRows TABLE
(
    RelativePath nvarchar(400) NOT NULL PRIMARY KEY,
    FileContent nvarchar(max) NOT NULL,
    PackageFileContent nvarchar(max) NULL,
    IsEnabled bit NOT NULL
);

IF @SourceArtifactId IS NOT NULL
BEGIN
    INSERT INTO @SourceRows
    (
        RelativePath,
        FileContent,
        PackageFileContent,
        IsEnabled
    )
    SELECT RelativePath,
           FileContent,
           PackageFileContent,
           IsEnabled
    FROM omp.ArtifactConfigurationFiles
    WHERE ArtifactId = @SourceArtifactId;
END;
ELSE
BEGIN
    INSERT INTO @SourceRows
    (
        RelativePath,
        FileContent,
        PackageFileContent,
        IsEnabled
    )
    SELECT RelativePath,
           FileContent,
           PackageFileContent,
           IsEnabled
    FROM @FallbackRows;
END;

IF NOT EXISTS (SELECT 1 FROM @SourceRows)
BEGIN
    SELECT CAST(NULL AS nvarchar(50)) AS SourceVersion,
           CAST(NULL AS nvarchar(400)) AS RelativePath,
           CAST(NULL AS nvarchar(40)) AS Outcome
    WHERE 1 = 0;
    RETURN;
END;

-- Classify every relevant source row against the pre-update target state.
-- sourceEdited: the previous row provably carries operator changes.
-- baselinesEqual: the package file is unchanged between the two versions.
-- targetPristine: the target row is still exactly what its package delivered,
-- so carrying content forward cannot overwrite a newer operator edit.
DECLARE @Report TABLE
(
    RelativePath nvarchar(400) NOT NULL,
    Outcome nvarchar(40) NOT NULL
);

INSERT INTO @Report (RelativePath, Outcome)
SELECT sourceFile.RelativePath,
       CASE
           WHEN target.ArtifactConfigurationFileId IS NULL THEN N'MissingInPackage'
           WHEN sourceFile.PackageFileContent IS NOT NULL
                AND target.PackageFileContent IS NOT NULL
                AND CAST(target.PackageFileContent AS varbinary(max)) = CAST(sourceFile.PackageFileContent AS varbinary(max))
                AND (CAST(sourceFile.FileContent AS varbinary(max)) <> CAST(sourceFile.PackageFileContent AS varbinary(max))
                     OR sourceFile.IsEnabled = 0)
               THEN N'Preserved'
           -- A source row with NO package baseline has UNKNOWN lineage: the schema says
           -- 'legacy row or operator-created row'. It may be operator-owned, or it may
           -- simply predate the PackageFileContent column (added 2026-08-12) and never
           -- have been re-imported. Requiring a baseline on the SOURCE made these rows Conflict
           -- and let the package default win, which is how a configured OmpAuth:Oidc
           -- block disappeared from a working install (a customer test environment, measured 2026-08-23):
           -- the operator's row sat on the previous artifact while every newer version
           -- carried the package default, and once one version held that default it
           -- became the carry-forward source for the next -- the loss compounded and
           -- never healed.
           -- Carrying it forward is safe precisely because the TARGET is still exactly
           -- what its package delivered and is enabled, so there is no newer operator
           -- edit here to overwrite. A target that was already edited stays untouched
           -- and is still reported as Conflict below.
           -- Reported as its own outcome, never as Preserved: claiming these are operator
           -- edits would assert lineage nobody can prove. The trade is deliberate and has
           -- a real cost in the other direction -- a package that genuinely means to change
           -- a never-edited legacy row will not reach the new version -- so the operator is
           -- told by name which files this applied to.
           --
           -- Note what does NOT happen here: when the TARGET already carries an operator
           -- edit, the WHERE clause below never lets the row in, so it is not reported at
           -- all -- not as Conflict. The target keeps its own content, which is the safe
           -- outcome, but the pairing is silent. Do not describe that case as Conflict;
           -- ELSE N'Conflict' is unreachable for a baseline-less source.
           WHEN sourceFile.PackageFileContent IS NULL
                AND target.PackageFileContent IS NOT NULL
                AND CAST(target.FileContent AS varbinary(max)) = CAST(target.PackageFileContent AS varbinary(max))
                AND target.IsEnabled = 1
               THEN N'PreservedWithoutBaseline'
           ELSE N'Conflict'
       END
FROM @SourceRows sourceFile
LEFT JOIN omp.ArtifactConfigurationFiles target
    ON target.ArtifactId = @ArtifactId
   AND target.RelativePath = sourceFile.RelativePath
WHERE
  (
      -- Operator-edited previous rows the new package no longer ships. Includes rows
      -- with no package baseline (NULL), whose lineage is unknown -- operator-created
      -- OR predating the PackageFileContent column -- and which the baseline-only
      -- check used to drop silently (R4-D5). Both are reported as MissingInPackage.
      (target.ArtifactConfigurationFileId IS NULL
       AND (
           (sourceFile.PackageFileContent IS NOT NULL
            AND (CAST(sourceFile.FileContent AS varbinary(max)) <> CAST(sourceFile.PackageFileContent AS varbinary(max))
                 OR sourceFile.IsEnabled = 0))
           OR sourceFile.PackageFileContent IS NULL
       ))
      OR
      -- Rows in both versions whose effective content differs, where the
      -- target row is still pristine package content. Rows whose previous
      -- version is provably unedited are a normal package change and are not
      -- reported. Targets already operator-edited are left alone entirely.
      (target.ArtifactConfigurationFileId IS NOT NULL
       AND target.PackageFileContent IS NOT NULL
       AND CAST(target.FileContent AS varbinary(max)) = CAST(target.PackageFileContent AS varbinary(max))
       AND target.IsEnabled = 1
       AND (CAST(target.FileContent AS varbinary(max)) <> CAST(sourceFile.FileContent AS varbinary(max))
            OR target.IsEnabled <> sourceFile.IsEnabled)
       AND NOT (sourceFile.PackageFileContent IS NOT NULL
                AND CAST(sourceFile.FileContent AS varbinary(max)) = CAST(sourceFile.PackageFileContent AS varbinary(max))
                AND sourceFile.IsEnabled = 1))
  );

UPDATE target
SET FileContent = sourceFile.FileContent,
    IsEnabled = sourceFile.IsEnabled,
    UpdatedUtc = SYSUTCDATETIME()
FROM omp.ArtifactConfigurationFiles target
INNER JOIN @SourceRows sourceFile
    ON sourceFile.RelativePath = target.RelativePath
INNER JOIN @Report report
    ON report.RelativePath = target.RelativePath
   AND report.Outcome IN (N'Preserved', N'PreservedWithoutBaseline')
WHERE target.ArtifactId = @ArtifactId;

SELECT @SourceVersion AS SourceVersion,
       RelativePath,
       Outcome
FROM @Report
ORDER BY RelativePath;";

    /// <summary>
    /// Marker inside <see cref="CarryConfigurationRowsOnPointerMove"/> that the
    /// caller replaces with a parenthesized, comma-separated list of artifact
    /// ids (e.g. "(100),(101)") -- the artifacts the moved pointers referenced
    /// before the move. Ids come from the database as ints, so literal
    /// interpolation cannot inject SQL.
    /// </summary>
    public const string PointerMoveSourceIdsMarker = "/*POINTER_MOVE_SOURCE_IDS*/";

    /// <summary>
    /// Carries configuration rows onto the artifact identified by @ArtifactId
    /// from the artifacts a set of application pointers referenced before they
    /// were moved to @ArtifactId. Two cases per relative path (newest source
    /// row wins when pointers disagreed):
    ///
    /// 1. The target has no row at all for a path a source artifact carries:
    ///    the row is copied (reported as 'Copied').
    /// 2. The target row is still pristine package baseline while the source
    ///    row carries an operator delta: the edit is carried over (reported as
    ///    'CarriedEdit'). A target row the operator already edited on the new
    ///    version itself is never touched.
    ///
    /// Runs BEFORE the pointer updates so the copy sees the pre-move state.
    /// Result set: RelativePath, ChangeKind ('Copied' | 'CarriedEdit').
    /// </summary>
    public const string CarryConfigurationRowsOnPointerMove = @"
DECLARE @MovedFrom TABLE
(
    ArtifactId int NOT NULL PRIMARY KEY
);

INSERT INTO @MovedFrom (ArtifactId)
VALUES /*POINTER_MOVE_SOURCE_IDS*/;

DECLARE @SourceRows TABLE
(
    RelativePath nvarchar(400) NOT NULL PRIMARY KEY,
    FileContent nvarchar(max) NOT NULL,
    PackageFileContent nvarchar(max) NULL,
    IsEnabled bit NOT NULL
);

;WITH ranked AS
(
    SELECT sourceFile.RelativePath,
           sourceFile.FileContent,
           sourceFile.PackageFileContent,
           sourceFile.IsEnabled,
           ROW_NUMBER() OVER
           (
               PARTITION BY sourceFile.RelativePath
               ORDER BY source.CreatedUtc DESC, source.ArtifactId DESC
           ) AS PathRank
    FROM omp.ArtifactConfigurationFiles sourceFile
    INNER JOIN @MovedFrom moved
        ON moved.ArtifactId = sourceFile.ArtifactId
    INNER JOIN omp.Artifacts source
        ON source.ArtifactId = sourceFile.ArtifactId
)
INSERT INTO @SourceRows
(
    RelativePath,
    FileContent,
    PackageFileContent,
    IsEnabled
)
SELECT RelativePath,
       FileContent,
       PackageFileContent,
       IsEnabled
FROM ranked
WHERE PathRank = 1;

DECLARE @Changes TABLE
(
    RelativePath nvarchar(400) NOT NULL,
    ChangeKind nvarchar(20) NOT NULL
);

INSERT INTO omp.ArtifactConfigurationFiles
(
    ArtifactId,
    RelativePath,
    FileContent,
    PackageFileContent,
    IsEnabled
)
OUTPUT inserted.RelativePath, N'Copied' INTO @Changes (RelativePath, ChangeKind)
SELECT @ArtifactId,
       sourceRows.RelativePath,
       sourceRows.FileContent,
       sourceRows.PackageFileContent,
       sourceRows.IsEnabled
FROM @SourceRows sourceRows
WHERE NOT EXISTS
(
    SELECT 1
    FROM omp.ArtifactConfigurationFiles targetFile
    WHERE targetFile.ArtifactId = @ArtifactId
      AND targetFile.RelativePath = sourceRows.RelativePath
);

UPDATE target
SET FileContent = sourceRows.FileContent,
    IsEnabled = sourceRows.IsEnabled,
    UpdatedUtc = SYSUTCDATETIME()
OUTPUT inserted.RelativePath, N'CarriedEdit' INTO @Changes (RelativePath, ChangeKind)
FROM omp.ArtifactConfigurationFiles target
INNER JOIN @SourceRows sourceRows
    ON sourceRows.RelativePath = target.RelativePath
WHERE target.ArtifactId = @ArtifactId
  AND target.PackageFileContent IS NOT NULL
  AND CAST(target.FileContent AS varbinary(max)) = CAST(target.PackageFileContent AS varbinary(max))
  AND target.IsEnabled = 1
  AND
  (
      sourceRows.PackageFileContent IS NULL
      OR CAST(sourceRows.FileContent AS varbinary(max)) <> CAST(sourceRows.PackageFileContent AS varbinary(max))
      OR sourceRows.IsEnabled = 0
  )
  AND
  (
      CAST(target.FileContent AS varbinary(max)) <> CAST(sourceRows.FileContent AS varbinary(max))
      OR target.IsEnabled <> sourceRows.IsEnabled
  );

SELECT RelativePath,
       ChangeKind
FROM @Changes
ORDER BY RelativePath;";

    /// <summary>
    /// Retention guard, used by both the HostAgent cleanup and the Portal
    /// preview: removes from @DeleteArtifacts every artifact whose
    /// configuration rows carry an operator delta (content differs from the
    /// package baseline, no baseline at all, or operator-disabled) that does
    /// NOT survive byte-identically, for the same relative path, on a newer
    /// artifact in the same slot that is itself preserved (ranked within the
    /// keep limit, referenced, or already spared by this guard).
    ///
    /// Iterated to a fixed point: sparing the newest artifact of an identical
    /// delta chain makes the next-older one redundant, which frees it for
    /// deletion on the next pass.
    ///
    /// Expects the caller's table variables @RankedArtifacts (every artifact
    /// with AppId, PackageType, TargetName, RetentionRank) and
    /// @DeleteArtifacts (the current deletion candidates, same slot columns).
    /// </summary>
    public const string ProtectUniqueOperatorDeltaArtifacts = @"
WHILE 1 = 1
BEGIN
    DELETE d
    FROM @DeleteArtifacts d
    WHERE EXISTS
    (
        SELECT 1
        FROM omp.ArtifactConfigurationFiles configFile
        WHERE configFile.ArtifactId = d.ArtifactId
          AND
          (
              configFile.PackageFileContent IS NULL
              OR CAST(configFile.FileContent AS varbinary(max)) <> CAST(configFile.PackageFileContent AS varbinary(max))
              OR configFile.IsEnabled = 0
          )
          AND NOT EXISTS
          (
              SELECT 1
              FROM @RankedArtifacts newer
              INNER JOIN omp.ArtifactConfigurationFiles newerFile
                  ON newerFile.ArtifactId = newer.ArtifactId
                 AND newerFile.RelativePath = configFile.RelativePath
              WHERE newer.AppId = d.AppId
                AND newer.PackageType = d.PackageType
                AND ISNULL(newer.TargetName, N'') = ISNULL(d.TargetName, N'')
                AND newer.RetentionRank < d.RetentionRank
                AND CAST(newerFile.FileContent AS varbinary(max)) = CAST(configFile.FileContent AS varbinary(max))
                AND NOT EXISTS
                (
                    SELECT 1
                    FROM @DeleteArtifacts newerDelete
                    WHERE newerDelete.ArtifactId = newer.ArtifactId
                )
          )
    );

    IF @@ROWCOUNT = 0
    BEGIN
        BREAK;
    END
END;";
}

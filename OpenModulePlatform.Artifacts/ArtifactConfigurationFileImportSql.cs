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
    /// Carries operator-edited configuration content forward from the latest
    /// previous enabled artifact in the same app/package-type/target slot to
    /// the artifact identified by @ArtifactId, and reports one row per
    /// configuration file whose continuity needs operator attention.
    ///
    /// Result set columns: SourceVersion, RelativePath, Outcome
    /// ('Preserved' | 'Conflict' | 'MissingInPackage'). Empty when there is no
    /// previous artifact with configuration rows, or nothing worth reporting.
    ///
    /// A row is carried forward only when the previous row has a known package
    /// baseline, the operator changed it (content or IsEnabled), and the new
    /// package content is unchanged against that baseline (three-way rule).
    /// Rows without a baseline are operator-owned content with no package to
    /// compare against; they are carried forward when the target row is still
    /// pristine package content, and reported as Conflict only when the target
    /// itself already carries an operator edit that must not be overwritten.
    /// </summary>
    public const string CarryForwardOperatorEdits = @"
DECLARE @AppId int;
DECLARE @PackageType nvarchar(100);
DECLARE @TargetName nvarchar(200);
DECLARE @SourceArtifactId int;
DECLARE @SourceVersion nvarchar(50);

SELECT @AppId = AppId,
       @PackageType = PackageType,
       @TargetName = TargetName
FROM omp.Artifacts
WHERE ArtifactId = @ArtifactId;

SELECT TOP (1)
       @SourceArtifactId = source.ArtifactId,
       @SourceVersion = source.Version
FROM omp.Artifacts source
WHERE source.ArtifactId <> @ArtifactId
  AND source.AppId = @AppId
  AND source.PackageType = @PackageType
  AND ((source.TargetName = @TargetName) OR (source.TargetName IS NULL AND @TargetName IS NULL))
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
           -- block disappeared from a working install (VGR Test, measured 2026-08-23):
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
           WHEN sourceFile.PackageFileContent IS NULL
                AND target.PackageFileContent IS NOT NULL
                AND CAST(target.FileContent AS varbinary(max)) = CAST(target.PackageFileContent AS varbinary(max))
                AND target.IsEnabled = 1
               THEN N'PreservedWithoutBaseline'
           ELSE N'Conflict'
       END
FROM omp.ArtifactConfigurationFiles sourceFile
LEFT JOIN omp.ArtifactConfigurationFiles target
    ON target.ArtifactId = @ArtifactId
   AND target.RelativePath = sourceFile.RelativePath
WHERE sourceFile.ArtifactId = @SourceArtifactId
  AND
  (
      -- Operator-edited previous rows the new package no longer ships. Includes
      -- operator-CREATED rows that have no package baseline (NULL) — the most
      -- unambiguously operator-owned content — which the baseline-only check used
      -- to drop silently (R4-D5). Both are reported as MissingInPackage.
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
INNER JOIN omp.ArtifactConfigurationFiles sourceFile
    ON sourceFile.ArtifactId = @SourceArtifactId
   AND sourceFile.RelativePath = target.RelativePath
INNER JOIN @Report report
    ON report.RelativePath = target.RelativePath
   AND report.Outcome IN (N'Preserved', N'PreservedWithoutBaseline')
WHERE target.ArtifactId = @ArtifactId;

SELECT @SourceVersion AS SourceVersion,
       RelativePath,
       Outcome
FROM @Report
ORDER BY RelativePath;";
}

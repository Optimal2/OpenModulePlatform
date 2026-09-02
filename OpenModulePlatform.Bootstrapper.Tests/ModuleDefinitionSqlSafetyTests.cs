namespace OpenModulePlatform.Bootstrapper.Tests;

public sealed class ModuleDefinitionSqlSafetyTests
{
    private const string ArtifactWriteMessage =
        "Module definition SQL must not register or mutate omp.Artifacts; artifact registration is owned by the artifact import path.";

    private const string ArtifactPointerWriteMessage =
        "Module definition SQL must not write omp.InstanceTemplateAppInstances.DesiredArtifactId or omp.AppInstances.ArtifactId; artifact selection is owned by artifact auto-apply.";

    [Theory]
    [InlineData("INSERT INTO omp.Artifacts(AppId, Version) VALUES(1, N'1.0.0');", ArtifactWriteMessage)]
    [InlineData("UPDATE [omp].[Artifacts] SET IsEnabled = 0 WHERE ArtifactId = 1;", ArtifactWriteMessage)]
    [InlineData("UPDATE artifact SET IsEnabled = 0 FROM omp.Artifacts AS artifact WHERE artifact.ArtifactId = 1;", ArtifactWriteMessage)]
    [InlineData("INSERT INTO \"omp\".\"Artifacts\"(AppId, Version) VALUES(1, N'1.0.0');", ArtifactWriteMessage)]
    [InlineData("UPDATE TOP (10) omp.Artifacts SET IsEnabled = 0 WHERE ArtifactId > 0;", ArtifactWriteMessage)]
    [InlineData("INSERT TOP (1) INTO omp.Artifacts(AppId, Version) VALUES(1, N'1.0.0');", ArtifactWriteMessage)]
    [InlineData("MERGE INTO omp.Artifacts AS target USING (SELECT 1 AS AppId) source ON 1 = 0 WHEN NOT MATCHED THEN INSERT(AppId) VALUES(source.AppId);", ArtifactWriteMessage)]
    [InlineData("INSERT INTO omp.AppInstances(AppId, ArtifactId) VALUES(1, 2);", ArtifactPointerWriteMessage)]
    [InlineData("UPDATE ai SET ArtifactId = 2 FROM omp.AppInstances AS ai WHERE ai.AppInstanceId = NEWID();", ArtifactPointerWriteMessage)]
    [InlineData("UPDATE omp.InstanceTemplateAppInstances SET DesiredArtifactId = 2 WHERE InstanceTemplateAppInstanceId = 1;", ArtifactPointerWriteMessage)]
    [InlineData("INSERT INTO omp.InstanceTemplateAppInstances WITH (TABLOCK) (AppId, DesiredArtifactId) VALUES(1, 2);", ArtifactPointerWriteMessage)]
    public void BootstrapperValidator_BlocksArtifactOwnedWrites(string sql, string expectedMessage)
    {
        Assert.Equal(expectedMessage, Program.ValidateSafeModuleDefinitionSql(sql));
    }

    // Probe batch for the 2026-09 hardening round. Every blocked case below was first
    // verified RED against the pre-fix validators (each one passed all three mirrors),
    // then the hole was closed. Keep this batch identical to the Portal and HostAgent
    // suites so a divergence between the mirrors shows up as a failing test.
    public static TheoryData<string, string> HardenedBlockedSql => new()
    {
        // Family 4: OUTPUT INTO writes rows without INSERT/UPDATE/MERGE in front of the table.
        { "UPDATE omp.AppInstances SET DisplayName = N'ghost' OUTPUT inserted.AppInstanceId INTO omp.Artifacts(AppId) WHERE AppInstanceId = NEWID();", ArtifactWriteMessage },
        // Family 2: a CTE over the table can be the DML target; SQL Server writes the base table.
        { ";WITH c AS (SELECT ArtifactId, IsEnabled FROM omp.Artifacts) UPDATE c SET IsEnabled = 0 WHERE ArtifactId = 1;", ArtifactWriteMessage },
        // Family 9: modules must not delete artifact rows either.
        { "DELETE FROM omp.Artifacts WHERE Version = N'1.0';", ArtifactWriteMessage },
        { "DELETE omp.Artifacts WHERE ArtifactId = 1;", ArtifactWriteMessage },
        { ";WITH c AS (SELECT ArtifactId FROM omp.Artifacts) DELETE FROM c WHERE ArtifactId = 1;", ArtifactWriteMessage },
        // Mirror divergence: GO with a repeat count must still split batches everywhere.
        { "CREATE PROCEDURE omp.GhostProc AS BEGIN SELECT 1; END\nGO 2\nINSERT INTO omp.Artifacts(AppId) VALUES(1);", ArtifactWriteMessage },
        // Family 5: positional INSERT has no column list to scan.
        { "INSERT INTO omp.InstanceTemplateAppInstances VALUES(1, 1, 5);", ArtifactPointerWriteMessage },
        { "INSERT INTO omp.AppInstances DEFAULT VALUES;", ArtifactPointerWriteMessage },
        { "MERGE omp.InstanceTemplateAppInstances AS target USING (SELECT 1 AS AppId) source ON 1 = 0 WHEN NOT MATCHED THEN INSERT VALUES(1, 1, 5);", ArtifactPointerWriteMessage },
        // Family 6: compound assignment.
        { "UPDATE omp.AppInstances SET ArtifactId += 1 WHERE AppInstanceId = NEWID();", ArtifactPointerWriteMessage },
        { "UPDATE omp.AppInstances SET [ArtifactId] -= 1 WHERE AppInstanceId = NEWID();", ArtifactPointerWriteMessage },
        // Family 2: CTE write against an artifact pointer column.
        { ";WITH c AS (SELECT InstanceTemplateAppInstanceId, DesiredArtifactId FROM omp.InstanceTemplateAppInstances) UPDATE c SET DesiredArtifactId = 999 WHERE InstanceTemplateAppInstanceId = 1;", ArtifactPointerWriteMessage },
        // Family 7: the assignment scan must not stop inside a subquery, at a parameter
        // named @Where, or at the WHEN of a CASE expression.
        { "UPDATE omp.AppInstances SET DisplayName = (SELECT TOP 1 name FROM sys.tables WHERE 1 = 1), ArtifactId = 5 WHERE AppInstanceId = NEWID();", ArtifactPointerWriteMessage },
        { "UPDATE omp.AppInstances SET DisplayName = @Where, ArtifactId = 5 WHERE AppInstanceId = NEWID();", ArtifactPointerWriteMessage },
        { "MERGE omp.InstanceTemplateAppInstances AS target USING (SELECT 1 AS AppId) source ON 1 = 0 WHEN MATCHED THEN UPDATE SET DisplayName = CASE WHEN 1 = 1 THEN N'x' ELSE N'y' END, DesiredArtifactId = 5;", ArtifactPointerWriteMessage },
    };

    [Theory]
    [MemberData(nameof(HardenedBlockedSql))]
    public void BootstrapperValidator_BlocksKnownBypasses(string sql, string expectedMessage)
    {
        Assert.Equal(expectedMessage, Program.ValidateSafeModuleDefinitionSql(sql));
    }

    public static TheoryData<string> HardenedAllowedSql => new()
    {
        // A column list that does not name the owned column remains allowed.
        "INSERT INTO omp.AppInstances(AppId, DisplayName) VALUES(1, N'x');",
        // CASE WHEN in an ordinary UPDATE assignment list.
        "UPDATE omp.AppInstances SET DisplayName = CASE WHEN 1 = 1 THEN N'x' ELSE N'y' END WHERE AppInstanceId = NEWID();",
        // A foreign key's ON DELETE clause is schema metadata, not an executable DELETE;
        // all three mirrors must agree (the line break before DELETE is deliberate).
        "CREATE TABLE omp.FkProbeChild (Id int NOT NULL PRIMARY KEY, ParentId int NULL, CONSTRAINT FK_FkProbeChild FOREIGN KEY (ParentId) REFERENCES omp.FkProbeChild(Id) ON\nDELETE CASCADE);",
        // WHEN NOT MATCHED BY SOURCE THEN DELETE is a MERGE action scoped by the merge predicate.
        "MERGE omp.Apps AS target USING (SELECT 1 AS AppKey) source ON 1 = 0 WHEN NOT MATCHED BY SOURCE THEN\nDELETE;",
    };

    [Theory]
    [MemberData(nameof(HardenedAllowedSql))]
    public void BootstrapperValidator_AllowsNonOwningStatements(string sql)
    {
        Assert.Null(Program.ValidateSafeModuleDefinitionSql(sql));
    }

    [Fact]
    public void BootstrapperValidator_AllowsArtifactReads()
    {
        Assert.Null(Program.ValidateSafeModuleDefinitionSql(
            "SELECT ArtifactId FROM omp.Artifacts WHERE IsEnabled = 1;"));
    }

    [Fact]
    public void BootstrapperValidator_AllowsStoredProcedureDefinitionsThatOwnRuntimeMaterialization()
    {
        const string sql = """
ALTER PROCEDURE omp.MaterializeInstanceTemplate
AS
BEGIN
    UPDATE omp.InstanceTemplateAppInstances
    SET DesiredArtifactId = 2
    WHERE InstanceTemplateAppInstanceId = 1;
END
GO
""";

        Assert.Null(Program.ValidateSafeModuleDefinitionSql(sql));
    }
}

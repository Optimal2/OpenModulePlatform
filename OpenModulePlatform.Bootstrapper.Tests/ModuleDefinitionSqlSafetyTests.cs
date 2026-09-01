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

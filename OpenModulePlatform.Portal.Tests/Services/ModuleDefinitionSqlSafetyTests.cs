using OpenModulePlatform.HostAgent.Runtime.Services;
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class ModuleDefinitionSqlSafetyTests
{
    private const string ArtifactWriteMessage =
        "Module definition SQL must not register or mutate omp.Artifacts; artifact registration is owned by the artifact import path.";

    private const string ArtifactPointerWriteMessage =
        "Module definition SQL must not write omp.InstanceTemplateAppInstances.DesiredArtifactId or omp.AppInstances.ArtifactId; artifact selection is owned by artifact auto-apply.";

    public static TheoryData<string, string> BlockedSql => new()
    {
        { "INSERT INTO omp.Artifacts(AppId, Version) VALUES(1, N'1.0.0');", ArtifactWriteMessage },
        { "UPDATE [omp].[Artifacts] SET IsEnabled = 0 WHERE ArtifactId = 1;", ArtifactWriteMessage },
        { "UPDATE artifact SET IsEnabled = 0 FROM omp.Artifacts AS artifact WHERE artifact.ArtifactId = 1;", ArtifactWriteMessage },
        { "INSERT INTO \"omp\".\"Artifacts\"(AppId, Version) VALUES(1, N'1.0.0');", ArtifactWriteMessage },
        { "UPDATE TOP (10) omp.Artifacts SET IsEnabled = 0 WHERE ArtifactId > 0;", ArtifactWriteMessage },
        { "INSERT TOP (1) INTO omp.Artifacts(AppId, Version) VALUES(1, N'1.0.0');", ArtifactWriteMessage },
        { "MERGE TOP (1) INTO omp.Artifacts AS target USING (SELECT 1 AS AppId) source ON 1 = 0 WHEN NOT MATCHED THEN INSERT(AppId) VALUES(source.AppId);", ArtifactWriteMessage },
        { "MERGE INTO [omp].Artifacts AS target USING (SELECT 1 AS AppId) source ON 1 = 0 WHEN NOT MATCHED THEN INSERT(AppId) VALUES(source.AppId);", ArtifactWriteMessage },
        { "INSERT INTO omp.AppInstances(AppId, ArtifactId) VALUES(1, 2);", ArtifactPointerWriteMessage },
        { "UPDATE omp.AppInstances SET ArtifactId = 2 WHERE AppInstanceId = NEWID();", ArtifactPointerWriteMessage },
        { "UPDATE ai SET ArtifactId = 2 FROM omp.AppInstances AS ai WHERE ai.AppInstanceId = NEWID();", ArtifactPointerWriteMessage },
        { "INSERT INTO [omp].[InstanceTemplateAppInstances](AppId, [DesiredArtifactId]) VALUES(1, 2);", ArtifactPointerWriteMessage },
        { "INSERT INTO \"omp\".\"AppInstances\"(AppId, \"ArtifactId\") VALUES(1, 2);", ArtifactPointerWriteMessage },
        { "INSERT INTO omp.InstanceTemplateAppInstances WITH (TABLOCK) (AppId, DesiredArtifactId) VALUES(1, 2);", ArtifactPointerWriteMessage },
        { "UPDATE TOP (10) omp.AppInstances WITH (UPDLOCK) SET ArtifactId = 2 WHERE AppInstanceId = NEWID();", ArtifactPointerWriteMessage },
        { "MERGE omp.InstanceTemplateAppInstances AS target USING (SELECT 1 AS AppId) source ON 1 = 0 WHEN MATCHED THEN UPDATE SET DesiredArtifactId = 2;", ArtifactPointerWriteMessage }
    };

    [Theory]
    [MemberData(nameof(BlockedSql))]
    public void PlatformRuntimeValidators_BlockArtifactOwnedWrites(string sql, string expectedMessage)
    {
        Assert.Equal(expectedMessage, OmpHostArtifactRepository.ValidateSafeModuleDefinitionSql(sql));
        Assert.Equal(expectedMessage, OmpAdminRepository.ValidateSafeModuleDefinitionSql(sql));
    }

    [Fact]
    public void PlatformRuntimeValidators_AllowArtifactReadsAndUnrelatedArtifactIdColumns()
    {
        const string sql = """
SELECT ArtifactId FROM omp.Artifacts WHERE IsEnabled = 1;
UPDATE omp.WorkerInstances SET ArtifactId = 2 WHERE WorkerInstanceId = 1;
UPDATE omp.AppInstances SET DisplayName = N'ArtifactId is descriptive text' WHERE AppInstanceId = NEWID();
UPDATE omp.InstanceTemplateAppInstances SET DisplayName = N'x' WHERE DesiredArtifactId = 5;
UPDATE app SET DisplayName = N'x' FROM omp.AppInstances AS app WHERE app.ArtifactId = 5;
""";

        Assert.Null(OmpHostArtifactRepository.ValidateSafeModuleDefinitionSql(sql));
        Assert.Null(OmpAdminRepository.ValidateSafeModuleDefinitionSql(sql));
    }

    [Fact]
    public void PlatformRuntimeValidators_AllowStoredProcedureDefinitionsThatOwnRuntimeMaterialization()
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

        Assert.Null(OmpHostArtifactRepository.ValidateSafeModuleDefinitionSql(sql));
        Assert.Null(OmpAdminRepository.ValidateSafeModuleDefinitionSql(sql));
    }
}

using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Verifies that re-applying a module definition (package import with an equal or
/// newer definitionVersion) does not clobber operator-owned columns on existing
/// omp.InstanceTemplateAppInstances rows seeded from
/// integrity.requiredOmpRows.instanceTemplateAppInstances.
/// </summary>
public sealed class OmpAdminRepositoryModuleDefinitionApplyTests : IClassFixture<ModuleDefinitionApplyTestFixture>
{
    private readonly ModuleDefinitionApplyTestFixture _fixture;

    public OmpAdminRepositoryModuleDefinitionApplyTests(ModuleDefinitionApplyTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Apply_ReimportSameDefinition_PreservesOperatorOwnedColumns()
    {
        var repo = _fixture.CreatePortalRepository();
        var moduleKey = "seed_guard_same";
        var documentId = await _fixture.InsertModuleDefinitionDocumentAsync(
            moduleKey,
            "1.0.0",
            BuildDefinitionJson(displayName: "Seed Name", description: "seed description"));

        var applyResult = await repo.ApplyModuleDefinitionDocumentAsync(
            documentId,
            allowTemporaryIncompatibleArtifacts: true,
            CancellationToken.None);
        Assert.True(applyResult.Applied);

        var seeded = await _fixture.GetTemplateAppInstanceAsync(moduleKey, "svc-default");
        Assert.NotNull(seeded);
        Assert.Equal(0, seeded!.DesiredState);
        Assert.False(seeded.IsEnabled);
        Assert.False(seeded.IsAllowed);
        Assert.Equal("/seed-route", seeded.RoutePath);

        // Operator enables the service and adapts deployment paths in the template.
        await _fixture.UpdateTemplateAppInstanceAsync(
            moduleKey,
            "svc-default",
            "DesiredState = 1, IsEnabled = 1, IsAllowed = 1, RoutePath = N'/operator-route', InstallPath = N'E:/Operator', InstallationName = N'OperatorInstall'");

        // Re-import of the same definitionVersion applies the same document again.
        var reapplyResult = await repo.ApplyModuleDefinitionDocumentAsync(
            documentId,
            allowTemporaryIncompatibleArtifacts: true,
            CancellationToken.None);
        Assert.True(reapplyResult.Applied);

        var afterReimport = await _fixture.GetTemplateAppInstanceAsync(moduleKey, "svc-default");
        Assert.NotNull(afterReimport);
        Assert.Equal(1, afterReimport!.DesiredState);
        Assert.True(afterReimport.IsEnabled);
        Assert.True(afterReimport.IsAllowed);
        Assert.Equal("/operator-route", afterReimport.RoutePath);
        Assert.Equal("E:/Operator", afterReimport.InstallPath);
        Assert.Equal("OperatorInstall", afterReimport.InstallationName);
    }

    [Fact]
    public async Task Apply_NewerDefinition_PreservesOperatorColumns_UpdatesDescriptive_AndSeedsNewRows()
    {
        var repo = _fixture.CreatePortalRepository();
        var moduleKey = "seed_guard_newer";
        var v1DocumentId = await _fixture.InsertModuleDefinitionDocumentAsync(
            moduleKey,
            "1.0.0",
            BuildDefinitionJson(displayName: "Seed Name", description: "seed description"));

        await repo.ApplyModuleDefinitionDocumentAsync(
            v1DocumentId,
            allowTemporaryIncompatibleArtifacts: true,
            CancellationToken.None);

        await _fixture.UpdateTemplateAppInstanceAsync(
            moduleKey,
            "svc-default",
            "DesiredState = 1, IsEnabled = 1, IsAllowed = 1, RoutePath = N'/operator-route'");

        var v2DocumentId = await _fixture.InsertModuleDefinitionDocumentAsync(
            moduleKey,
            "2.0.0",
            BuildDefinitionJson(
                displayName: "Seed Name v2",
                description: "seed description v2",
                includeExtraInstance: true));

        var applyResult = await repo.ApplyModuleDefinitionDocumentAsync(
            v2DocumentId,
            allowTemporaryIncompatibleArtifacts: true,
            CancellationToken.None);
        Assert.True(applyResult.Applied);

        // Existing row: operator-owned columns survive, descriptive columns follow the seed.
        var matched = await _fixture.GetTemplateAppInstanceAsync(moduleKey, "svc-default");
        Assert.NotNull(matched);
        Assert.Equal(1, matched!.DesiredState);
        Assert.True(matched.IsEnabled);
        Assert.True(matched.IsAllowed);
        Assert.Equal("/operator-route", matched.RoutePath);
        Assert.Equal("Seed Name v2", matched.DisplayName);
        Assert.Equal("seed description v2", matched.Description);

        // Brand-new row still receives its seed defaults.
        var inserted = await _fixture.GetTemplateAppInstanceAsync(moduleKey, "svc-extra");
        Assert.NotNull(inserted);
        Assert.Equal(1, inserted!.DesiredState);
        Assert.True(inserted.IsEnabled);
        Assert.True(inserted.IsAllowed);
        Assert.Equal("/extra-route", inserted.RoutePath);
        Assert.Equal("Extra Seed", inserted.DisplayName);
    }

    [Fact]
    public async Task Apply_MatchedRow_FillsDeploymentColumnsOnlyWhenNull()
    {
        var repo = _fixture.CreatePortalRepository();
        var moduleKey = "seed_guard_fillnull";
        var documentId = await _fixture.InsertModuleDefinitionDocumentAsync(
            moduleKey,
            "1.0.0",
            BuildDefinitionJson(displayName: "Seed Name", description: "seed description"));

        await repo.ApplyModuleDefinitionDocumentAsync(
            documentId,
            allowTemporaryIncompatibleArtifacts: true,
            CancellationToken.None);

        // Simulate a pre-existing row that never received deployment values.
        await _fixture.UpdateTemplateAppInstanceAsync(
            moduleKey,
            "svc-default",
            "RoutePath = NULL, InstallPath = NULL, InstallationName = NULL");

        await repo.ApplyModuleDefinitionDocumentAsync(
            documentId,
            allowTemporaryIncompatibleArtifacts: true,
            CancellationToken.None);

        var row = await _fixture.GetTemplateAppInstanceAsync(moduleKey, "svc-default");
        Assert.NotNull(row);
        Assert.Equal("/seed-route", row!.RoutePath);
        Assert.Equal("C:/Seed", row.InstallPath);
        Assert.Equal("SeedInstall", row.InstallationName);
    }

    private static string BuildDefinitionJson(
        string displayName,
        string description,
        bool includeExtraInstance = false)
    {
        var extraInstance = includeExtraInstance
            ? """
                    ,
                    {
                      "instanceTemplateKey": "default",
                      "moduleInstanceKey": "svc-module",
                      "appInstanceKey": "svc-extra",
                      "appKey": "svc",
                      "displayName": "Extra Seed",
                      "routePath": "/extra-route",
                      "desiredState": 1,
                      "isEnabled": true,
                      "isAllowed": true,
                      "sortOrder": 6
                    }
            """
            : string.Empty;

        return $$"""
        {
          "module": {
            "displayName": "Seed Guard Module",
            "moduleType": "ServiceAppModule",
            "schemaName": "seed_guard",
            "description": "Module definition apply seed guard tests",
            "sortOrder": 1,
            "isEnabled": true
          },
          "apps": [
            {
              "appKey": "svc",
              "displayName": "Seed Guard Service",
              "appType": "ServiceApp"
            }
          ],
          "integrity": {
            "requiredOmpRows": {
              "instanceTemplateModuleInstances": [
                {
                  "instanceTemplateKey": "default",
                  "moduleInstanceKey": "svc-module"
                }
              ],
              "instanceTemplateAppInstances": [
                {
                  "instanceTemplateKey": "default",
                  "moduleInstanceKey": "svc-module",
                  "appInstanceKey": "svc-default",
                  "appKey": "svc",
                  "displayName": "{{displayName}}",
                  "description": "{{description}}",
                  "routePath": "/seed-route",
                  "installPath": "C:/Seed",
                  "installationName": "SeedInstall",
                  "desiredState": 0,
                  "isEnabled": false,
                  "isAllowed": false,
                  "sortOrder": 5
                }{{extraInstance}}
              ]
            }
          }
        }
        """;
    }
}

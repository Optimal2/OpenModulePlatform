namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Exercises the real omp.MaterializeInstanceTemplate procedure (applied from
/// sql/1-setup-openmoduleplatform.sql by the fixture) to prove that disabling
/// a template row propagates to materialized runtime rows -- and that
/// hand-created rows, which share no key with any template row, are never
/// touched by that propagation.
///
/// The fixture database is shared by the whole test class, so every test scopes
/// its materialization runs to its own instance via @InstanceKey; only the
/// host-scoping test deliberately runs with @HostKey.
/// </summary>
public sealed class OmpTemplateMaterializationTests : IClassFixture<TemplateMaterializationTestFixture>
{
    private readonly TemplateMaterializationTestFixture _fixture;

    public OmpTemplateMaterializationTests(TemplateMaterializationTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TemplateAppDisable_DisablesMaterializedRow_AndLeavesHandCreatedRowsUntouched()
    {
        const string instanceKey = "tmpl-app-disable-instance";
        var templateId = await _fixture.InsertTemplateAsync("tmpl-app-disable");
        var moduleId = await _fixture.InsertModuleAsync("tmpl-app-disable-module");
        var appId = await _fixture.InsertAppAsync(moduleId, "tmpl-app-disable-app");
        var templateModuleInstanceId = await _fixture.InsertTemplateModuleInstanceAsync(templateId, moduleId, "mod");
        var templateAppInstanceId = await _fixture.InsertTemplateAppInstanceAsync(templateModuleInstanceId, appId, "templated-app");
        var instanceId = await _fixture.InsertInstanceAsync(instanceKey, templateId);

        var firstRun = await _fixture.MaterializeAsync(instanceKey: instanceKey);
        Assert.Equal(1, firstRun.ModuleInstanceChanges);
        Assert.Equal(1, firstRun.AppInstanceChanges);
        Assert.True(await _fixture.GetAppInstanceEnabledAsync(instanceKey, "mod", "templated-app"));

        // Hand-created rows: a module instance and an app instance whose keys do
        // not exist in the template. The materializer must never touch these.
        var handModuleInstanceId = await _fixture.InsertHandCreatedModuleInstanceAsync(instanceId, moduleId, "hand-mod");
        await _fixture.InsertHandCreatedAppInstanceAsync(handModuleInstanceId, appId, "hand-app");
        var materializedModuleInstanceId = await GetMaterializedModuleInstanceIdAsync(instanceKey, "mod");
        await _fixture.InsertHandCreatedAppInstanceAsync(materializedModuleInstanceId, appId, "hand-app-under-materialized-mod");

        // The reported lie: the template row is turned off, and before the fix
        // the materialized row stayed enabled forever.
        await _fixture.SetTemplateAppInstanceEnabledAsync(templateAppInstanceId, false);
        var secondRun = await _fixture.MaterializeAsync(instanceKey: instanceKey);

        Assert.False(
            await _fixture.GetAppInstanceEnabledAsync(instanceKey, "mod", "templated-app"),
            "Disabling the template app row must propagate to the materialized app instance.");
        Assert.Equal(1, secondRun.AppInstanceChanges);

        // The more important half: hand-created rows survive untouched.
        Assert.True(
            await _fixture.GetAppInstanceEnabledAsync(instanceKey, "hand-mod", "hand-app"),
            "A hand-created app instance must not be disabled by template propagation.");
        Assert.True(
            await _fixture.GetModuleInstanceEnabledAsync(instanceKey, "hand-mod"),
            "A hand-created module instance must not be disabled by template propagation.");
        Assert.True(
            await _fixture.GetAppInstanceEnabledAsync(instanceKey, "mod", "hand-app-under-materialized-mod"),
            "A hand-created app instance under a materialized module instance must not be disabled.");
    }

    [Fact]
    public async Task TemplateAppReenable_ReenablesMaterializedRow()
    {
        const string instanceKey = "tmpl-app-reenable-instance";
        var templateId = await _fixture.InsertTemplateAsync("tmpl-app-reenable");
        var moduleId = await _fixture.InsertModuleAsync("tmpl-app-reenable-module");
        var appId = await _fixture.InsertAppAsync(moduleId, "tmpl-app-reenable-app");
        var templateModuleInstanceId = await _fixture.InsertTemplateModuleInstanceAsync(templateId, moduleId, "mod");
        var templateAppInstanceId = await _fixture.InsertTemplateAppInstanceAsync(templateModuleInstanceId, appId, "templated-app");
        await _fixture.InsertInstanceAsync(instanceKey, templateId);

        await _fixture.MaterializeAsync(instanceKey: instanceKey);
        await _fixture.SetTemplateAppInstanceEnabledAsync(templateAppInstanceId, false);
        await _fixture.MaterializeAsync(instanceKey: instanceKey);
        Assert.False(await _fixture.GetAppInstanceEnabledAsync(instanceKey, "mod", "templated-app"));

        await _fixture.SetTemplateAppInstanceEnabledAsync(templateAppInstanceId, true);
        await _fixture.MaterializeAsync(instanceKey: instanceKey);
        Assert.True(
            await _fixture.GetAppInstanceEnabledAsync(instanceKey, "mod", "templated-app"),
            "Re-enabling the template app row must re-enable the materialized app instance.");
    }

    [Fact]
    public async Task TemplateModuleInstanceDisable_DisablesMaterializedModuleAndItsApps()
    {
        const string instanceKey = "tmpl-mod-disable-instance";
        var templateId = await _fixture.InsertTemplateAsync("tmpl-mod-disable");
        var moduleId = await _fixture.InsertModuleAsync("tmpl-mod-disable-module");
        var appId = await _fixture.InsertAppAsync(moduleId, "tmpl-mod-disable-app");
        var templateModuleInstanceId = await _fixture.InsertTemplateModuleInstanceAsync(templateId, moduleId, "mod");
        await _fixture.InsertTemplateAppInstanceAsync(templateModuleInstanceId, appId, "templated-app");
        var instanceId = await _fixture.InsertInstanceAsync(instanceKey, templateId);

        await _fixture.MaterializeAsync(instanceKey: instanceKey);
        var handModuleInstanceId = await _fixture.InsertHandCreatedModuleInstanceAsync(instanceId, moduleId, "hand-mod");
        await _fixture.InsertHandCreatedAppInstanceAsync(handModuleInstanceId, appId, "hand-app");

        await _fixture.SetTemplateModuleInstanceEnabledAsync(templateModuleInstanceId, false);
        var run = await _fixture.MaterializeAsync(instanceKey: instanceKey);

        Assert.False(
            await _fixture.GetModuleInstanceEnabledAsync(instanceKey, "mod"),
            "Disabling the template module instance must propagate to the materialized module instance.");
        Assert.False(
            await _fixture.GetAppInstanceEnabledAsync(instanceKey, "mod", "templated-app"),
            "Disabling the template module instance must also disable its materialized app instances.");
        Assert.Equal(1, run.ModuleInstanceChanges);
        Assert.Equal(1, run.AppInstanceChanges);

        Assert.True(await _fixture.GetModuleInstanceEnabledAsync(instanceKey, "hand-mod"));
        Assert.True(await _fixture.GetAppInstanceEnabledAsync(instanceKey, "hand-mod", "hand-app"));
    }

    [Fact]
    public async Task TemplateDisable_DisablesAllMaterializedRowsForInstancesUsingIt()
    {
        const string instanceKey = "tmpl-disable-instance";
        var templateId = await _fixture.InsertTemplateAsync("tmpl-disable");
        var moduleId = await _fixture.InsertModuleAsync("tmpl-disable-module");
        var appId = await _fixture.InsertAppAsync(moduleId, "tmpl-disable-app");
        var templateModuleInstanceId = await _fixture.InsertTemplateModuleInstanceAsync(templateId, moduleId, "mod");
        await _fixture.InsertTemplateAppInstanceAsync(templateModuleInstanceId, appId, "templated-app");
        await _fixture.InsertInstanceAsync(instanceKey, templateId);

        await _fixture.MaterializeAsync(instanceKey: instanceKey);
        await _fixture.SetTemplateEnabledAsync(templateId, false);
        await _fixture.MaterializeAsync(instanceKey: instanceKey);

        Assert.False(await _fixture.GetModuleInstanceEnabledAsync(instanceKey, "mod"));
        Assert.False(await _fixture.GetAppInstanceEnabledAsync(instanceKey, "mod", "templated-app"));
    }

    [Fact]
    public async Task HostScopedMaterialization_DoesNotTouchOtherInstancesRows()
    {
        const string instanceAKey = "tmpl-host-scope-instance-a";
        const string instanceBKey = "tmpl-host-scope-instance-b";
        var templateId = await _fixture.InsertTemplateAsync("tmpl-host-scope");
        var moduleId = await _fixture.InsertModuleAsync("tmpl-host-scope-module");
        var appId = await _fixture.InsertAppAsync(moduleId, "tmpl-host-scope-app");
        var templateModuleInstanceId = await _fixture.InsertTemplateModuleInstanceAsync(templateId, moduleId, "mod");
        var templateAppInstanceId = await _fixture.InsertTemplateAppInstanceAsync(templateModuleInstanceId, appId, "templated-app");

        var instanceAId = await _fixture.InsertInstanceAsync(instanceAKey, templateId);
        var instanceBId = await _fixture.InsertInstanceAsync(instanceBKey, templateId);
        await _fixture.InsertHostAsync(instanceAId, "tmpl-host-scope-host-a");
        await _fixture.InsertHostAsync(instanceBId, "tmpl-host-scope-host-b");

        await _fixture.MaterializeAsync(instanceKey: instanceAKey);
        await _fixture.MaterializeAsync(instanceKey: instanceBKey);

        // The template app row is now disabled. A host-scoped materialization run
        // for host A must propagate the disable inside instance A only; instance
        // B's materialized rows belong to a run scoped to instance B.
        await _fixture.SetTemplateAppInstanceEnabledAsync(templateAppInstanceId, false);
        await _fixture.MaterializeAsync(hostKey: "tmpl-host-scope-host-a");

        Assert.False(
            await _fixture.GetAppInstanceEnabledAsync(instanceAKey, "mod", "templated-app"),
            "The host-scoped run must propagate the disable inside its own instance.");
        Assert.True(
            await _fixture.GetAppInstanceEnabledAsync(instanceBKey, "mod", "templated-app"),
            "The host-scoped run must not touch another instance's materialized rows.");
    }

    private async Task<Guid> GetMaterializedModuleInstanceIdAsync(string instanceKey, string moduleInstanceKey)
    {
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Microsoft.Data.SqlClient.SqlCommand(
            @"
SELECT mi.ModuleInstanceId
FROM omp.ModuleInstances mi
INNER JOIN omp.Instances i ON i.InstanceId = mi.InstanceId
WHERE i.InstanceKey = @instanceKey
  AND mi.ModuleInstanceKey = @moduleInstanceKey;",
            conn);
        cmd.Parameters.AddWithValue("@instanceKey", instanceKey);
        cmd.Parameters.AddWithValue("@moduleInstanceKey", moduleInstanceKey);
        var value = await cmd.ExecuteScalarAsync();
        Assert.True(value is not null and not DBNull, $"Expected a materialized module instance '{moduleInstanceKey}' in '{instanceKey}'.");
        return (Guid)value!;
    }
}

using OpenModulePlatform.ModuleDefinitions;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// A platform delete that a module table blocks with a foreign key names the module and the
/// runtime maintenance event it has to declare; every other error is left alone.
/// </summary>
public sealed class ModuleForeignKeyConflictGuidanceTests
{
    private static string ReferenceConflict(string table)
        => "The DELETE statement conflicted with the REFERENCE constraint \"FK_x\". "
            + $"The conflict occurred in database \"OpenModulePlatform\", table \"{table}\", column 'HostId'.\r\n"
            + "The statement has been terminated.";

    [Fact]
    public void ModuleTable_NamesModuleSchemaTableAndEvent()
    {
        var guidance = ModuleRuntimeMaintenanceExecutor.DescribeModuleForeignKeyConflict(
            547,
            ReferenceConflict("omp_acme_invoicer.ChannelLeases"),
            ModuleRuntimeMaintenance.HostRemoved);

        Assert.NotNull(guidance);
        Assert.StartsWith("OMP-MODULE-RUNTIME-MAINTENANCE: Module 'acme_invoicer' (schema omp_acme_invoicer)", guidance.Message, StringComparison.Ordinal);
        Assert.Contains("omp_acme_invoicer.ChannelLeases", guidance.Message, StringComparison.Ordinal);
        Assert.Contains("'FK_x'", guidance.Message, StringComparison.Ordinal);
        Assert.Contains("event 'host-removed'", guidance.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("omp.WorkerInstances")]
    [InlineData("omp_content.contents")]
    [InlineData("omp_portal.DashboardWidgets")]
    [InlineData("dbo.Anything")]
    public void PlatformOrForeignTable_IsLeftUnchanged(string table)
    {
        Assert.Null(ModuleRuntimeMaintenanceExecutor.DescribeModuleForeignKeyConflict(
            547, ReferenceConflict(table), ModuleRuntimeMaintenance.ArtifactRemoved));
    }

    [Fact]
    public void OtherErrors_AreLeftUnchanged()
    {
        Assert.Null(ModuleRuntimeMaintenanceExecutor.DescribeModuleForeignKeyConflict(
            2627, ReferenceConflict("omp_acme_invoicer.ChannelLeases"), ModuleRuntimeMaintenance.HostRemoved));
        Assert.Null(ModuleRuntimeMaintenanceExecutor.DescribeModuleForeignKeyConflict(
            547,
            "The UPDATE statement conflicted with the CHECK constraint \"CK_x\". The conflict occurred in database \"OpenModulePlatform\", table \"omp_acme_invoicer.ChannelLeases\", column 'State'.",
            ModuleRuntimeMaintenance.AppInstanceRemoved));
    }
}

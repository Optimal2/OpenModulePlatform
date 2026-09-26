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

    // The same error on a server installed in German: only the quoted names survive translation.
    private static string GermanReferenceConflict(string table)
        => "Die DELETE-Anweisung steht in Konflikt mit der REFERENCE-Einschränkung \"FK_x\". "
            + $"Der Konflikt trat in der Datenbank \"OpenModulePlatform\", Tabelle \"{table}\", Spalte 'HostId' auf.\r\n"
            + "Die Anweisung wurde beendet.";

    [Fact]
    public void ModuleTable_NamesModuleSchemaTableAndEvent()
    {
        var conflict = ModuleRuntimeMaintenanceExecutor.ParseForeignKeyConflict(
            547, ReferenceConflict("omp_acme_invoicer.ChannelLeases"));

        var guidance = ModuleRuntimeMaintenanceExecutor.DescribeForeignKeyConflict(
            conflict!, ModuleRuntimeMaintenance.HostRemoved, registeredModuleKey: "acme_invoicer");

        Assert.StartsWith("OMP-MODULE-RUNTIME-MAINTENANCE: Module 'acme_invoicer' (schema omp_acme_invoicer)", guidance.Message, StringComparison.Ordinal);
        Assert.Contains("omp_acme_invoicer.ChannelLeases", guidance.Message, StringComparison.Ordinal);
        Assert.Contains("'FK_x'", guidance.Message, StringComparison.Ordinal);
        Assert.Contains("event 'host-removed'", guidance.Message, StringComparison.Ordinal);
        Assert.Equal(ModuleRuntimeMaintenanceFailure.UnreleasedModuleRows, guidance.Failure);
        Assert.Equal("acme_invoicer", guidance.ModuleKey);
    }

    [Fact]
    public void UnregisteredSchema_SaysNoModuleOwnsIt_InsteadOfAdvisingAnUpgrade()
    {
        var conflict = ModuleRuntimeMaintenanceExecutor.ParseForeignKeyConflict(
            547, ReferenceConflict("omp_x.T"));

        var guidance = ModuleRuntimeMaintenanceExecutor.DescribeForeignKeyConflict(
            conflict!, ModuleRuntimeMaintenance.HostRemoved, registeredModuleKey: null);

        Assert.Contains("schema omp_x belongs to no registered module", guidance.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("upgrade", guidance.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ModuleRuntimeMaintenanceFailure.UnregisteredSchemaRows, guidance.Failure);
        Assert.Null(guidance.ModuleKey);
        Assert.Equal("omp_x", guidance.SchemaName);
    }

    [Fact]
    public void LocalizedText_IsReadFromTheQuotedNames()
    {
        var conflict = ModuleRuntimeMaintenanceExecutor.ParseForeignKeyConflict(
            547, GermanReferenceConflict("omp_acme_invoicer.ChannelLeases"));

        Assert.NotNull(conflict);
        Assert.Equal("omp_acme_invoicer", conflict.Schema);
        Assert.Equal("ChannelLeases", conflict.Table);
        Assert.Equal("FK_x", conflict.Constraint);
        Assert.Equal("acme_invoicer", conflict.DerivedModuleKey);
    }

    [Theory]
    [InlineData("Konflikt mit einer Einschränkung in der Tabelle «omp_acme_invoicer.ChannelLeases».")]
    [InlineData("")]
    public void UnreadableText_GivesGenericGuidance_NeverTheRawError(string message)
    {
        var conflict = ModuleRuntimeMaintenanceExecutor.ParseForeignKeyConflict(547, message);

        Assert.NotNull(conflict);
        Assert.Null(conflict.Schema);
        var guidance = ModuleRuntimeMaintenanceExecutor.DescribeForeignKeyConflict(
            conflict, ModuleRuntimeMaintenance.ArtifactRemoved, registeredModuleKey: null);
        Assert.Equal(ModuleRuntimeMaintenanceFailure.UnidentifiedReference, guidance.Failure);
        Assert.Contains("event 'artifact-removed'", guidance.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was deleted.", guidance.Message, StringComparison.Ordinal);
        if (message.Length > 0)
        {
            Assert.DoesNotContain(message, guidance.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("omp.WorkerInstances")]
    [InlineData("omp_content.contents")]
    [InlineData("omp_portal.DashboardWidgets")]
    [InlineData("dbo.Anything")]
    public void PlatformOrForeignTable_IsLeftUnchanged(string table)
    {
        Assert.Null(ModuleRuntimeMaintenanceExecutor.ParseForeignKeyConflict(547, ReferenceConflict(table)));
        Assert.Null(ModuleRuntimeMaintenanceExecutor.ParseForeignKeyConflict(547, GermanReferenceConflict(table)));
    }

    [Fact]
    public void OtherErrors_AreLeftUnchanged()
    {
        Assert.Null(ModuleRuntimeMaintenanceExecutor.ParseForeignKeyConflict(
            2627, ReferenceConflict("omp_acme_invoicer.ChannelLeases")));
        Assert.Null(ModuleRuntimeMaintenanceExecutor.ParseForeignKeyConflict(
            547,
            "The UPDATE statement conflicted with the CHECK constraint \"CK_x\". The conflict occurred in database \"OpenModulePlatform\", table \"omp_acme_invoicer.ChannelLeases\", column 'State'."));
    }
}

using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;

namespace OpenModulePlatform.TestSupport;

// Exercise each executor's real document reader, before any SQL connection exists.
internal static class PlatformConfigurationImportContract
{
    private static string CoreDefinition()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenModulePlatform.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, "omp_core.module-definition.json"));
    }

    private static object? ReadScripts(Type executor, string json)
        => executor.GetMethod("ReadPortableSqlScripts", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [json]);

    internal static void AcceptsCoreDefinition(Type executor)
        => Assert.NotNull(ReadScripts(executor, CoreDefinition()));

    internal static void CompiledMigrationOwnsHostScope(Type executor)
    {
        var migration = executor.Assembly.GetType("OpenModulePlatform.ModuleDefinitions.PlatformConfigurationMigration")!;
        var schema = (string)migration.GetField("SchemaMigrationSql", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        var scope = (string)migration.GetField("HostScopeMigrationSql", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        Assert.Contains(scope, schema);
        new TSql170Parser(true).Parse(new StringReader(schema), out var errors);
        Assert.Empty(errors);
        Assert.Contains("COL_LENGTH(N'omp.ConfigOverlayDocuments', N'HostScope') IS NULL", scope);
        Assert.Contains("DEFAULT(N'Host')", scope);
        Assert.DoesNotContain("DEFAULT(N'AllHosts')", scope);
        Assert.Contains("EXEC(N'ALTER TABLE omp.ConfigOverlayDocuments WITH CHECK ADD CONSTRAINT", scope);
        Assert.Contains("WITH CHECK CHECK CONSTRAINT CK_omp_ConfigOverlayDocuments_HostScope", scope);
        Assert.DoesNotContain("UPDATE", scope, StringComparison.OrdinalIgnoreCase);
    }

    internal static void RejectsModuleConfigurationMutations(Type executor)
    {
        // Spoofing the core identity must never grant permission to portable SQL.
        foreach (var module in new[] { "module_probe", "omp_core" })
        foreach (var sql in new[]
        {
            "ALTER TABLE omp.ConfigOverlayDocuments ADD HostScope nvarchar(20) NOT NULL DEFAULT(N'Host');",
            "ALTER TABLE omp.ConfigOverlayDocuments WITH CHECK ADD CONSTRAINT CK_Probe CHECK(HostScope IN (N'Host', N'AllHosts'));",
            "UPDATE omp.ConfigOverlayDocuments SET HostScope = N'AllHosts';",
            "EXEC(N'UPDATE omp.ConfigOverlayDocuments SET HostScope = N''AllHosts'';');",
            "DELETE FROM omp.ArtifactConfigurationFiles;",
            "UPDATE omp.ConfigOverlayConfigurationFiles SET FileContent = N'module content';"
        })
        {
            var document = JsonNode.Parse(CoreDefinition())!;
            document["moduleKey"] = module;
            document["sqlScripts"] = new JsonArray(new JsonObject
            {
                ["key"] = "setup-core-schema",
                ["inlineSql"] = sql
            });
            var exception = Assert.Throws<TargetInvocationException>(() => ReadScripts(executor, document.ToJsonString()));
            var blocked = Assert.IsType<InvalidOperationException>(exception.InnerException);
            Assert.Contains("OMP-MODULE-SQL-CONFIG-OWNERSHIP", blocked.Message);
            Assert.Contains("Module definition SQL must not write omp.", blocked.Message);
        }
    }
}

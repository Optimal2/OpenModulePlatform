namespace OpenModulePlatform.Bootstrapper.Tests;

/// <summary>Runs the shared module-definition SQL guard contract against this copy.</summary>
public sealed class ModuleDefinitionSqlSafetyTests : OpenModulePlatform.TestSupport.ModuleDefinitionSqlSafetyContract
{
    protected override string? Validate(string sql) => Program.ValidateSafeModuleDefinitionSql(sql);

    protected override Type ContractType => typeof(InstallationEngine);

    protected override Type PortableSqlType => typeof(Program);
}

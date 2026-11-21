using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>Runs the shared module-definition SQL guard contract against this copy.</summary>
public sealed class ModuleDefinitionSqlSafetyTests : OpenModulePlatform.TestSupport.ModuleDefinitionSqlSafetyContract
{
    protected override string? Validate(string sql) => OmpAdminRepository.ValidateSafeModuleDefinitionSql(sql);

    protected override Type ContractType => typeof(OmpAdminRepository);

    protected override Type PortableSqlType => typeof(OmpAdminRepository);
}

using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>Runs the shared module-definition SQL guard contract against this copy.</summary>
public sealed class ModuleDefinitionSqlSafetyTests : OpenModulePlatform.TestSupport.ModuleDefinitionSqlSafetyContract
{
    protected override string? Validate(string sql) => OmpHostArtifactRepository.ValidateSafeModuleDefinitionSql(sql);

    protected override Type ContractType => typeof(OmpHostArtifactRepository);

    protected override Type PortableSqlType => typeof(OmpHostArtifactRepository);
}

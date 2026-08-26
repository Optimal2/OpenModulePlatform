namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// xUnit collection that gives every AD-principal-migration real-schema test class
/// the same single <see cref="AdPrincipalMigrationTestFixture"/> instance and
/// serializes their execution. Two fixture instances would provision and drop the
/// same database name concurrently.
/// </summary>
[CollectionDefinition(CollectionName)]
public sealed class AdPrincipalMigrationCollection : ICollectionFixture<AdPrincipalMigrationTestFixture>
{
    public const string CollectionName = "AdPrincipalMigration";
}

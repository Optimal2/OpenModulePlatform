namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// xUnit collection that gives every config-overlay real-schema test class the same
/// single <see cref="ConfigOverlayActiveUniquenessTestFixture"/> instance and
/// serializes their execution. Two fixture instances would provision the same
/// database name concurrently, and the real setup script is not fully idempotent.
/// </summary>
[CollectionDefinition(CollectionName)]
public sealed class ConfigOverlayActiveUniquenessCollection : ICollectionFixture<ConfigOverlayActiveUniquenessTestFixture>
{
    public const string CollectionName = "ConfigOverlayActiveUniqueness";
}

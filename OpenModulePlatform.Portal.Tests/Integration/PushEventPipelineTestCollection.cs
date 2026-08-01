namespace OpenModulePlatform.Portal.Tests.Integration;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PushEventPipelineTestCollection : ICollectionFixture<PushEventPipelineTestFixture>
{
    public const string Name = "Push event pipeline integration";
}

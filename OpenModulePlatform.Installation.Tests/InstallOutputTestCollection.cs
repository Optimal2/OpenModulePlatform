namespace OpenModulePlatform.Installation.Tests;

/// <summary>
/// InstallOutput.Current is process-wide. Tests that replace it, and tests whose engine calls
/// write through it, must not run at the same time, or one test's lines land in another
/// test's sink.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InstallOutputTestCollection
{
    public const string Name = "Process-wide InstallOutput";
}

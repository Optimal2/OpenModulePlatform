using OpenModulePlatform.Worker.Abstractions.Contracts;
using OpenModulePlatform.Worker.Abstractions.Models;

namespace OpenModulePlatform.WorkerProcessHost.TestPlugin;

public sealed class CompatibilityTestWorkerFactory : IWorkerModuleFactory
{
    public string WorkerTypeKey => "compatibility-test-worker";

    public IWorkerModule Create(IServiceProvider services) => new CompatibilityTestWorker();

    private sealed class CompatibilityTestWorker : IWorkerModule
    {
        public Task RunAsync(WorkerExecutionContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}

/// <summary>
/// Test double for a plugin compiled against a contract member that the running host lacks.
/// The CLR surfaces that binary mismatch as MissingMethodException when RunAsync begins.
/// </summary>
public sealed class NewerContractTestWorkerFactory : IWorkerModuleFactory
{
    public string WorkerTypeKey => "newer-contract-test-worker";

    public IWorkerModule Create(IServiceProvider services) => new NewerContractTestWorker();

    private sealed class NewerContractTestWorker : IWorkerModule
    {
        public Task RunAsync(WorkerExecutionContext context, CancellationToken cancellationToken)
            => throw new MissingMethodException(
                "Method not found: WorkerExecutionContext.get_NewerContractMember().");
    }
}

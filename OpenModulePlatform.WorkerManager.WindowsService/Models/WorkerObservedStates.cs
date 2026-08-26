// File: OpenModulePlatform.WorkerManager.WindowsService/Models/WorkerObservedStates.cs
namespace OpenModulePlatform.WorkerManager.WindowsService.Models;

public static class WorkerObservedStates
{
    public const byte Unknown = 0;
    public const byte Starting = 1;
    public const byte Running = 2;
    public const byte Stopping = 3;
    public const byte Stopped = 4;
    public const byte Failed = 5;

    /// <summary>
    /// R7-F7. The worker process is alive and heartbeating but has been asked to
    /// finish its in-flight job and accept no new work while a configuration or host
    /// change waits to restart it. Before this state existed a draining worker
    /// published Running, so neither the Portal nor the deployment diagnostics could
    /// tell "working normally" apart from "parked mid-drain" -- and a wedged drain
    /// (the R5-F1/R6-F6/R7-F1 defect family) was invisible until the job channel
    /// silently ran dry.
    /// </summary>
    public const byte Draining = 6;
}

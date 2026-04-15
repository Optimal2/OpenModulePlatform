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
}

// File: OpenModulePlatform.WorkerProcessHost/Runtime/WorkerDrainCoordinator.cs
using Microsoft.Extensions.Logging;
using OpenModulePlatform.Worker.Abstractions.Contracts;

namespace OpenModulePlatform.WorkerProcessHost.Runtime;

/// <summary>
/// Bridges the module-facing drain contract onto the named events owned by
/// WorkerManager: the drain event is manager-to-worker ("start no new jobs"),
/// the busy event is worker-to-manager ("a job is in flight"). The busy event
/// is set before the drain re-check in <see cref="TryBeginJob"/>, so the
/// manager can never observe idle while a job it must not interrupt is being
/// admitted.
/// </summary>
public sealed class WorkerDrainCoordinator : IWorkerDrainCoordinator, IDisposable
{
    private readonly EventWaitHandle _drainEvent;
    private readonly EventWaitHandle _busyEvent;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private int _activeJobCount;
    private bool _drainObservedLogged;

    public WorkerDrainCoordinator(EventWaitHandle drainEvent, EventWaitHandle busyEvent, ILogger logger)
    {
        _drainEvent = drainEvent;
        _busyEvent = busyEvent;
        _logger = logger;
    }

    public bool IsDrainRequested => _drainEvent.WaitOne(0);

    public IDisposable? TryBeginJob()
    {
        lock (_gate)
        {
            _activeJobCount++;
            if (_activeJobCount == 1)
            {
                _busyEvent.Set();
            }
        }

        if (!IsDrainRequested)
        {
            return new JobScope(this);
        }

        EndJob();
        if (!_drainObservedLogged)
        {
            _drainObservedLogged = true;
            _logger.LogInformation("Drain requested; the worker admits no new jobs until it is restarted.");
        }

        return null;
    }

    public void Dispose()
    {
        _drainEvent.Dispose();
        _busyEvent.Dispose();
    }

    private void EndJob()
    {
        lock (_gate)
        {
            _activeJobCount--;
            if (_activeJobCount == 0)
            {
                _busyEvent.Reset();
            }
        }
    }

    private sealed class JobScope : IDisposable
    {
        private WorkerDrainCoordinator? _owner;

        public JobScope(WorkerDrainCoordinator owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.EndJob();
        }
    }
}

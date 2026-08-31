// File: OpenModulePlatform.WorkerManager.WindowsService/Services/WorkerManagerHostedService.cs
using System.ComponentModel;
using System.Management;
using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.WorkerManager.WindowsService.Contracts;
using OpenModulePlatform.WorkerManager.WindowsService.Models;
using OpenModulePlatform.WorkerManager.WindowsService.Runtime;
using OpenModulePlatform.WorkerManager.WindowsService.Utilities;
using DbException = System.Data.Common.DbException;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace OpenModulePlatform.WorkerManager.WindowsService.Services;

public sealed class WorkerManagerHostedService : BackgroundService
{
    private const string WorkerProcessHostExecutableName = "OpenModulePlatform.WorkerProcessHost.exe";

    // Kept below the HostAgent RPC server's 8 pipe instances so a worker fan-out can
    // never exhaust that shared pipe and stall every other caller (R6-F4).
    private const int MaxConcurrentArtifactResolves = 4;

    private static readonly NamedWaitHandleOptions ShutdownEventOptions = new()
    {
        CurrentUserOnly = true
    };

    private readonly ILogger<WorkerManagerHostedService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<WorkerManagerSettings> _settings;
    private readonly IWorkerInstanceCatalog _catalog;
    private readonly OmpWorkerRuntimeRepository _runtimeRepository;
    private readonly HostAgentRpcClient _hostAgentRpcClient;
    private readonly Dictionary<Guid, ManagedWorkerProcess> _managedWorkers = new();

    public WorkerManagerHostedService(
        ILogger<WorkerManagerHostedService> logger,
        IConfiguration configuration,
        IOptionsMonitor<WorkerManagerSettings> settings,
        IWorkerInstanceCatalog catalog,
        OmpWorkerRuntimeRepository runtimeRepository,
        HostAgentRpcClient hostAgentRpcClient)
    {
        _logger = logger;
        _configuration = configuration;
        _settings = settings;
        _catalog = catalog;
        _runtimeRepository = runtimeRepository;
        _hostAgentRpcClient = hostAgentRpcClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hostIdentity = _settings.CurrentValue.ResolveHostKey();
        var refreshInterval = TimeSpan.FromSeconds(Math.Max(1, _settings.CurrentValue.RefreshSeconds));

        _logger.LogInformation(
            "WorkerManager started. HostIdentity={HostIdentity}, RefreshSeconds={RefreshSeconds}",
            hostIdentity,
            refreshInterval.TotalSeconds);

        try
        {
            await CleanupOrphanedWorkerProcessesOnStartupAsync(stoppingToken);
            // R12-D8/F10. Runs before the first reconcile so the rows a previous incarnation
            // left behind are downgraded before anything reads them. A manager killed hard
            // publishes no exit observation, so without this the database keeps claiming
            // Running for workers that died with it -- and the deployment gate believes it.
            await DowngradeStaleWorkerStatesIfEnabledAsync(hostIdentity, stoppingToken);
            await RunReconcileCycleAsync(hostIdentity, stoppingToken);

            using var timer = new PeriodicTimer(refreshInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunReconcileCycleAsync(hostIdentity, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("WorkerManager cancellation requested. HostIdentity={HostIdentity}", hostIdentity);
        }
        finally
        {
            await StopAllWorkersAsync("manager shutdown", CancellationToken.None);
        }
    }

    private async Task RunReconcileCycleAsync(string hostIdentity, CancellationToken cancellationToken)
    {
        try
        {
            await TouchHostHeartbeatIfEnabledAsync(hostIdentity, cancellationToken);
            // Also every cycle, not only at startup: a worker whose own observation writes
            // keep failing (a lost SQL connection for that one publish, a row nobody owns
            // any more) goes stale while its siblings keep reporting, and the app-instance
            // summary would otherwise be dragged forward by the healthy ones (R12-D8/F10).
            await DowngradeStaleWorkerStatesIfEnabledAsync(hostIdentity, cancellationToken);
            await ReconcileWorkersAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverableWorkerManagerFailure(ex))
        {
            // A single reconcile failure must not stop the Windows service;
            // the next cycle gets a fresh catalog/runtime view and retries.
            _logger.LogError(
                ex,
                "WorkerManager reconcile cycle failed and will be retried during the next cycle. HostIdentity={HostIdentity}",
                hostIdentity);
        }
    }

    private async Task ReconcileWorkersAsync(CancellationToken cancellationToken)
    {
        var catalogWorkers = await _catalog.GetDesiredWorkersAsync(cancellationToken);
        var desiredWorkers = await ResolveDesiredWorkerArtifactsAsync(catalogWorkers, cancellationToken);

        // "Still desired" must come from the CATALOG, not from the resolved list.
        // ResolveDesiredWorkerArtifactAsync returns null on a transient artifact
        // problem (a HostAgent provisioning state that briefly leaves Succeeded, a
        // failed ensureArtifact RPC), which dropped the worker from the resolved
        // list and therefore into undesiredWorkers -- so a healthy worker was
        // stopped mid-document-job, and killed after the stop timeout, because of a
        // temporary bookkeeping failure in another service (R6-F2). A worker is
        // undesired only when the catalog no longer lists it; an unresolvable one
        // simply is not started or updated this cycle and is retried next cycle.
        var catalogIds = catalogWorkers
            .Select(worker => worker.WorkerInstanceId)
            .ToHashSet();
        var desiredById = desiredWorkers.ToDictionary(worker => worker.WorkerInstanceId);
        var unresolvedCount = catalogIds.Count - desiredById.Count;
        if (unresolvedCount > 0)
        {
            _logger.LogWarning(
                "{UnresolvedCount} desired worker(s) could not be resolved this cycle and are left as-is; they are retried next cycle.",
                unresolvedCount);
        }
        var runtimeKind = GetRuntimeKindOrNull();

        // Resolve the desired WorkerProcessHost once per cycle: it is a single TOP(1)
        // row per host key, identical for every worker (R5-F5/R6-F4 apply — no per-worker
        // fan-out). Its artifact id is compared against each running worker's frozen
        // start witness (R12-F2) so that a host upgrade recycles workers through the
        // ordinary drain path. Before this comparison existed the resolve only ran when
        // a worker STARTED, so in steady state a new host build was never noticed: every
        // healthy worker kept the old executable forever and the deployment diagnostics
        // reported a Pending drift nothing ever cleared (measured 2026-08-23, host
        // 0.3.42 running while 0.3.43 was desired).
        //
        // A failed or empty resolve means "no opinion this cycle", never "changed":
        // the ProvisioningState=2 requirement makes the resolve flap during the very
        // host deploy that triggers the comparison, and recycling a healthy worker over
        // a transient bookkeeping gap is the failure mode R6-F2 exists to prevent. The
        // eleven ordinary definition fields are still compared sharply either way.
        ResolvedWorkerProcessHost? cycleHost = null;
        try
        {
            cycleHost = await ResolveWorkerProcessHostAsync(_settings.CurrentValue, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException || IsRecoverableWorkerManagerFailure(ex))
        {
            _logger.LogWarning(
                "Could not resolve the desired WorkerProcessHost this cycle; the host-version comparison is skipped and retried next cycle. Error={Error}",
                ex.Message);
        }

        var exitedWorkers = _managedWorkers.Values
            .Where(managed => managed.NeedsExitObservation())
            .ToList();

        foreach (var managed in exitedWorkers)
        {
            if (!managed.ObserveExitIfNeeded())
            {
                continue;
            }

            _logger.LogWarning(
                "Worker process exited. AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, ExitCode={ExitCode}, StopRequested={StopRequested}",
                managed.Definition.AppInstanceId,
                managed.Definition.WorkerInstanceId,
                managed.LastExitCode,
                managed.StopRequested);

            await PublishExitObservationIfEnabledAsync(managed, runtimeKind, "worker process exited", cancellationToken);
        }

        var undesiredWorkers = _managedWorkers.Values
            .Where(worker => !catalogIds.Contains(worker.Definition.WorkerInstanceId))
            .ToList();

        foreach (var existing in undesiredWorkers)
        {
            try
            {
                await StopAndRemoveWorkerAsync(existing, runtimeKind, "worker no longer desired", cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsRecoverableWorkerManagerFailure(ex))
            {
                // Keep reconciling other workers; per-worker failures are published below.
                await HandleWorkerFailureAsync(existing, runtimeKind, "stop undesired worker", ex, cancellationToken);
            }
        }

        foreach (var desired in desiredWorkers)
        {
            if (!_managedWorkers.TryGetValue(desired.WorkerInstanceId, out var managed))
            {
                managed = new ManagedWorkerProcess(desired);
                _managedWorkers.Add(desired.WorkerInstanceId, managed);
            }

            try
            {
                // The host half of the restart decision compares what is actually
                // RUNNING (the witness frozen at AttachProcess, R12-F2) against the
                // cycle's resolved host artifact — deliberately not a definition
                // field: the definition would drift from the process exactly when it
                // matters. Gated on IsRunning() because a dead process has no witness,
                // and folded into the SAME predicate as the configuration comparison
                // so a host change inherits the whole existing chain unchanged: the
                // startability preflight (R6-F3), drain before restart, the R7-F1
                // resume, and the R5-F1 else-branch that cancels a pending drain when
                // a host rollback makes the combined predicate turn false again.
                var hostRestart = managed.IsRunning()
                    && HostRestartCheck.RequiresHostRestart(
                        managed.StartedHostArtifactId,
                        cycleHost?.ArtifactId);

                if (!managed.HasEquivalentConfiguration(desired) || hostRestart)
                {
                    // Never tear down a RUNNING worker for a configuration we already
                    // know we cannot start. Two of the compared fields (InstallRootPath,
                    // IsProvisionedFromHostArtifactCache) and the plugin path derived
                    // from them come from HostArtifactStates at query time, so a
                    // provisioning state that briefly leaves Succeeded reads as a
                    // "configuration change" back to a stale legacy path. The old code
                    // drained, stopped, adopted the new definition and only then hit
                    // ValidateReadableStartupFile -- leaving the worker stopped and
                    // unable to start until the HostAgent state recovered (R6-F3).
                    var startability = managed.IsRunning()
                        ? await IsStartableDefinitionAsync(desired, cycleHost, cancellationToken)
                        : (IsStartable: true, Problem: string.Empty);
                    if (!startability.IsStartable)
                    {
                        // This branch sits above TryDrainForConfigurationChangeAsync, so
                        // once it starts firing the drain path is never re-entered. A drain
                        // begun on an earlier cycle would stay set forever: the worker holds
                        // its process and admits zero jobs, while line below refreshes its
                        // liveness so the R5-F4 staleness check sees a healthy worker and the
                        // R6-F6 drain-timeout warning never fires again. Cancel it here so a
                        // worker whose new definition turns out to be unstartable goes back
                        // to accepting work (R7-F1).
                        if (managed.CancelDrain())
                        {
                            _logger.LogInformation(
                                "Resumed a worker that was draining for a configuration change which turned out not to be startable. WorkerInstanceId={WorkerInstanceId}",
                                desired.WorkerInstanceId);
                        }

                        _logger.LogWarning(
                            "Ignoring a worker configuration change this cycle because the new definition is not startable; the running worker is left untouched. WorkerInstanceId={WorkerInstanceId}, Problem={Problem}",
                            desired.WorkerInstanceId,
                            startability.Problem);
                        await PublishRunningObservationIfEnabledAsync(managed, runtimeKind, cancellationToken);
                        continue;
                    }

                    if (!await TryDrainForConfigurationChangeAsync(managed, desired, cancellationToken))
                    {
                        // The worker is still busy with an in-flight job. Keep the
                        // old configuration running and retry during the next cycle;
                        // a version-change restart never interrupts a running job.
                        // A draining worker publishes no heartbeat of its own, so
                        // refresh its liveness timestamp here to keep the liveness
                        // check from flagging a healthy draining worker as Stale (R5-F4).
                        await PublishRunningObservationIfEnabledAsync(managed, runtimeKind, cancellationToken);
                        continue;
                    }

                    if (hostRestart)
                    {
                        // The generic configuration-changed line below cannot say WHY;
                        // for a host recycle the operator needs both builds named to
                        // tie this restart to the Pending row in the deployment
                        // diagnostics without reading source.
                        _logger.LogInformation(
                            "Worker host build changed; recycling worker onto the desired host. AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, RunningHostArtifactId={RunningHostArtifactId}, RunningHostArtifactVersion={RunningHostArtifactVersion}, DesiredHostArtifactId={DesiredHostArtifactId}, DesiredHostArtifactVersion={DesiredHostArtifactVersion}",
                            desired.AppInstanceId,
                            desired.WorkerInstanceId,
                            managed.StartedHostArtifactId,
                            managed.StartedHostArtifactVersion,
                            cycleHost?.ArtifactId,
                            cycleHost?.Version);
                    }

                    _logger.LogInformation(
                        "Worker configuration changed. AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, WorkerTypeKey={WorkerTypeKey}",
                        desired.AppInstanceId,
                        desired.WorkerInstanceId,
                        desired.WorkerTypeKey);

                    await StopWorkerAsync(managed, runtimeKind, "worker configuration changed", cancellationToken);
                    managed.UpdateDefinition(desired);
                }
                else if (managed.CancelDrain())
                {
                    // The configuration change that started a drain was reverted to
                    // the running configuration before the in-flight job completed;
                    // resume the worker instead of letting it idle forever (R5-F1).
                    _logger.LogInformation(
                        "Worker configuration change reverted before drain completed; resuming worker. AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, WorkerTypeKey={WorkerTypeKey}",
                        desired.AppInstanceId,
                        desired.WorkerInstanceId,
                        desired.WorkerTypeKey);
                }

                await EnsureWorkerRunningAsync(managed, runtimeKind, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsRecoverableWorkerManagerFailure(ex))
            {
                // One worker can fail to stop, start, or publish status without blocking
                // reconciliation for the remaining workers in the same service cycle.
                await HandleWorkerFailureAsync(managed, runtimeKind, "reconcile desired worker", ex, cancellationToken);
            }
        }
    }

    private Task<bool> TryDrainForConfigurationChangeAsync(
        ManagedWorkerProcess managed,
        DesiredWorkerInstance desired,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Only a live worker with drain support needs draining; exited or
        // legacy workers keep the immediate stop-and-replace behavior.
        if (!managed.IsRunning() || !managed.SupportsDrain)
        {
            return Task.FromResult(true);
        }

        var nowUtc = DateTimeOffset.UtcNow;
        if (managed.BeginDrain(nowUtc))
        {
            _logger.LogInformation(
                "Worker configuration changed while the worker is running; draining before restart. AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, WorkerTypeKey={WorkerTypeKey}",
                desired.AppInstanceId,
                desired.WorkerInstanceId,
                desired.WorkerTypeKey);
        }

        if (!managed.IsBusy())
        {
            return Task.FromResult(true);
        }

        // R6-F6 made this warn periodically because the drain never ended, so a wedged
        // worker needed a recurring signal (its own heartbeat suppresses the staleness
        // detector). W6 ends the drain instead, so there is nothing left to remind about
        // and the reminder bookkeeping is gone with it.
        var drainTimeout = TimeSpan.FromSeconds(Math.Max(1, _settings.CurrentValue.DrainTimeoutSeconds));
        if (managed.IsDrainTimedOut(nowUtc, drainTimeout))
        {
            // W6. The timeout used to only log -- forever, every reminder interval, while
            // the worker admitted no jobs and the channel silently stopped delivering. A
            // deadline that never expires is not a deadline, and a worker whose busy flag
            // is stuck (a plugin leaking a JobScope is the documented case) will never
            // clear it no matter how long it is given.
            //
            // Letting the restart proceed is the safe answer, not the risky one: an
            // interrupted job is exactly what the lease mechanism exists for. The lease
            // expires, the job returns to pending and another worker picks it up. That is
            // a bounded, self-healing outcome. A permanently drained worker is not.
            _logger.LogError(
                "Worker drain exceeded the configured timeout and the worker is still reporting busy; proceeding with the restart. Any in-flight job returns to pending when its lease expires. AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, DrainTimeoutSeconds={DrainTimeoutSeconds}, DrainStartedUtc={DrainStartedUtc:O}, DrainingFor={DrainingFor}",
                desired.AppInstanceId,
                desired.WorkerInstanceId,
                _settings.CurrentValue.DrainTimeoutSeconds,
                managed.DrainStartedUtc,
                nowUtc - managed.DrainStartedUtc.GetValueOrDefault());

            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    private async Task<IReadOnlyList<DesiredWorkerInstance>> ResolveDesiredWorkerArtifactsAsync(
        IReadOnlyList<DesiredWorkerInstance> desiredWorkers,
        CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;

        // Resolve every desired worker's artifact concurrently: each HostAgent RPC
        // already carries its own timeout, so issuing them serially made the worst
        // case N x TimeoutSeconds (e.g. all workers waiting when the HostAgent is
        // down). Parallelizing bounds the wait to roughly a single call (R5-F5).
        // Order is preserved because Task.WhenAll keeps the input ordering.
        //
        // Bound the concurrency (R6-F4). Every worker instance under one app instance
        // shares the same artifact, so the fan-out issued N IDENTICAL ensureArtifact
        // RPCs at once. Serially those were free (the first provisioned, the rest hit
        // the verified-cache fast path); in parallel they all block on the same
        // per-artifact semaphore while each holds a HostAgent pipe instance. The
        // HostAgent accepts at most 8, so a rollout on a host with more workers made
        // its accept loop throw and stall for 5 s at a time -- for every caller of
        // that shared pipe, not just us.
        using var resolveConcurrency = new SemaphoreSlim(Math.Max(1, MaxConcurrentArtifactResolves));
        var resolveTasks = desiredWorkers
            .Select(async desired =>
            {
                await resolveConcurrency.WaitAsync(cancellationToken);
                try
                {
                    return await ResolveDesiredWorkerArtifactAsync(settings, desired, cancellationToken);
                }
                finally
                {
                    resolveConcurrency.Release();
                }
            })
            .ToArray();

        var resolvedResults = await Task.WhenAll(resolveTasks);

        return resolvedResults
            .Where(resolved => resolved is not null)
            .Select(resolved => resolved!)
            .ToList();
    }

    private async Task<DesiredWorkerInstance?> ResolveDesiredWorkerArtifactAsync(
        WorkerManagerSettings settings,
        DesiredWorkerInstance desired,
        CancellationToken cancellationToken)
    {
        var resolved = desired;
        var shouldAskHostAgent = ShouldRequestArtifactFromHostAgent(settings, desired);

        if (shouldAskHostAgent)
        {
            var response = await _hostAgentRpcClient.EnsureArtifactAsync(
                desired.ArtifactId!.Value,
                null,
                cancellationToken);

            if (response?.Success == true && !string.IsNullOrWhiteSpace(response.LocalPath))
            {
                resolved = desired.WithInstallRootPath(response.LocalPath);
            }
            else
            {
                _logger.LogWarning(
                    "HostAgent could not provision worker artifact. WorkerInstanceId={WorkerInstanceId}, ArtifactId={ArtifactId}, Error={Error}",
                    desired.WorkerInstanceId,
                    desired.ArtifactId,
                    response?.ErrorMessage ?? "no response");
            }
        }

        if (string.IsNullOrWhiteSpace(resolved.PluginAssemblyPath))
        {
            _logger.LogWarning(
                "Skipping desired worker with unresolved plugin path. AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, ArtifactId={ArtifactId}",
                resolved.AppInstanceId,
                resolved.WorkerInstanceId,
                resolved.ArtifactId);
            return null;
        }

        return resolved;
    }

    private async Task EnsureWorkerRunningAsync(
        ManagedWorkerProcess managed,
        string? runtimeKind,
        CancellationToken cancellationToken)
    {
        if (managed.IsRunning())
        {
            await PublishRunningObservationIfEnabledAsync(managed, runtimeKind, cancellationToken);
            return;
        }

        // The process is gone: release its handles BEFORE trying to start a
        // replacement. ObserveExitIfNeeded is what disposes the shutdown/drain/busy
        // handles, and it was only ever called from the exited-worker snapshot taken
        // once at the top of the cycle. A worker that exited AFTER that snapshot (a
        // crash, or the WorkerProcessHost memory guard recycling it) therefore still
        // had its named shutdown event held open by THIS process, so CreateShutdownEvent
        // found createdNew == false and threw "shutdown event already exists" -- a
        // self-inflicted phantom failure that published a bogus Failed observation and,
        // since R5-F3 records the attempt earlier, also burned a restart-policy slot
        // (R6-F1). Self-heals next cycle, but the failure was entirely avoidable.
        managed.ObserveExitIfNeeded();

        var settings = _settings.CurrentValue;

        var nowUtc = DateTimeOffset.UtcNow;
        var restartWindow = TimeSpan.FromSeconds(settings.RestartWindowSeconds);
        var nextAllowedStartUtc = managed.GetNextEligibleStartUtc(nowUtc, restartWindow, settings.MaxRestartsPerWindow);

        if (nextAllowedStartUtc.HasValue && nextAllowedStartUtc.Value > nowUtc)
        {
            _logger.LogWarning(
                "Worker restart delayed by restart policy. AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, NextAllowedStartUtc={NextAllowedStartUtc:O}",
                managed.Definition.AppInstanceId,
                managed.Definition.WorkerInstanceId,
                nextAllowedStartUtc.Value);
            return;
        }

        if (managed.LastExitUtc.HasValue)
        {
            var restartDelay = TimeSpan.FromSeconds(settings.RestartDelaySeconds);
            var earliestRestartUtc = managed.LastExitUtc.Value.Add(restartDelay);
            if (earliestRestartUtc > nowUtc)
            {
                return;
            }
        }

        // Record the attempt before the start sequence so a failure that happens
        // early (path resolution, file validation, named-event or process creation)
        // still counts toward the restart-backoff policy instead of retrying every
        // cycle in a tight ~15s churn (R5-F3).
        managed.RecordStartAttempt(nowUtc, restartWindow);

        var workerProcessHost = await ResolveWorkerProcessHostAsync(settings, cancellationToken);
        var workerProcessPath = workerProcessHost.Path;
        ValidateReadableStartupFile(workerProcessPath, "Resolved WorkerProcessHost executable");
        ValidateReadableStartupFile(managed.Definition.PluginAssemblyPath, "Worker plugin assembly");

        // Create the three handles one at a time and own each as soon as it exists.
        // Passing them as constructor arguments meant a throw from the second or third
        // call left the earlier handle(s) orphaned for the life of the manager process --
        // and because CreateShutdownEvent refuses a name that already exists, a leaked
        // shutdown handle made every later start attempt for that worker fail with
        // "shutdown event already exists" until the service was restarted (R7-F3).
        using var startupResources = new WorkerStartupResources();
        startupResources.SetShutdownEvent(CreateShutdownEvent(managed.Definition));
        startupResources.SetDrainEvent(CreateManagedWorkerEvent(BuildDrainEventName(managed.Definition.WorkerInstanceId)));
        startupResources.SetBusyEvent(CreateManagedWorkerEvent(BuildBusyEventName(managed.Definition.WorkerInstanceId)));

        var ompConnectionString = _configuration.GetConnectionString("OmpDb");
        if (string.IsNullOrWhiteSpace(ompConnectionString))
        {
            throw new InvalidOperationException("Missing connection string: ConnectionStrings:OmpDb");
        }

        var process = CreateWorkerProcess(
            workerProcessPath,
            managed.Definition,
            workerProcessHost,
            ompConnectionString);
        startupResources.AttachProcess(process);

        StartWorkerProcess(process, managed.Definition.WorkerInstanceId, workerProcessPath);

        managed.AttachProcess(
            process,
            startupResources.ShutdownEvent,
            nowUtc,
            workerProcessHost,
            startupResources.DrainEvent,
            startupResources.BusyEvent);
        startupResources.ReleaseOwnership();

        await PublishStartingObservationIfEnabledAsync(managed, runtimeKind, cancellationToken);

        _logger.LogInformation(
            // R12-F2. The artifact the process was started from belongs in the one line that
            // records the start: it is the only place a log reader can tie a running process
            // to a version, and it is what the runtime state row now claims in the database.
            "Started worker process. AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, WorkerTypeKey={WorkerTypeKey}, ProcessId={ProcessId}, ArtifactId={ArtifactId}, ArtifactVersion={ArtifactVersion}, HostArtifactId={HostArtifactId}, HostArtifactVersion={HostArtifactVersion}, WorkerProcessPath={WorkerProcessPath}, PluginAssemblyPath={PluginAssemblyPath}",
            managed.Definition.AppInstanceId,
            managed.Definition.WorkerInstanceId,
            managed.Definition.WorkerTypeKey,
            managed.ProcessId,
            managed.StartedArtifactId,
            managed.StartedArtifactVersion,
            managed.StartedHostArtifactId,
            managed.StartedHostArtifactVersion,
            workerProcessPath,
            managed.Definition.PluginAssemblyPath);
    }

    private async Task StopAndRemoveWorkerAsync(
        ManagedWorkerProcess managed,
        string? runtimeKind,
        string reason,
        CancellationToken cancellationToken)
    {
        await StopWorkerAsync(managed, runtimeKind, reason, cancellationToken);
        _managedWorkers.Remove(managed.Definition.WorkerInstanceId);
    }

    private static bool ShouldRequestArtifactFromHostAgent(
        WorkerManagerSettings settings,
        DesiredWorkerInstance desired)
    {
        return settings.HostAgentRpc.Enabled
            && desired.ArtifactId.HasValue
            && !string.IsNullOrWhiteSpace(desired.PluginRelativePath)
            && (!desired.IsProvisionedFromHostArtifactCache
                || string.IsNullOrWhiteSpace(desired.PluginAssemblyPath)
                || !File.Exists(desired.PluginAssemblyPath));
    }

    private async Task StopAllWorkersAsync(string reason, CancellationToken cancellationToken)
    {
        var runtimeKind = GetRuntimeKindOrNull();

        // Stop workers concurrently. Each stop waits up to StopTimeoutSeconds (15 s
        // deployed) for a graceful exit, and the host's ShutdownTimeout is 30 s, so
        // stopping serially meant two unresponsive workers consumed the entire budget:
        // the host stopped waiting and the process exited mid-loop, leaving the
        // remaining worker hosts alive as orphans. They are not in a job object, so
        // they survived until the next start's orphan cleanup killed them with their
        // whole process tree -- losing exactly the in-flight jobs that drain exists to
        // protect (R6-F5). Each worker keeps its own best-effort error handling.
        async Task StopOneAsync(ManagedWorkerProcess managed)
        {
            try
            {
                await StopWorkerAsync(managed, runtimeKind, reason, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsRecoverableWorkerManagerFailure(ex))
            {
                // Shutdown cleanup is best-effort because the service is already stopping.
                _logger.LogWarning(
                    ex,
                    "WorkerManager could not stop a managed worker during shutdown cleanup. AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, Reason={Reason}",
                    managed.Definition.AppInstanceId,
                    managed.Definition.WorkerInstanceId,
                    reason);
            }
        }

        await Task.WhenAll(_managedWorkers.Values.ToList().Select(StopOneAsync));

        _managedWorkers.Clear();
    }

    private async Task StopWorkerAsync(
        ManagedWorkerProcess managed,
        string? runtimeKind,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!managed.IsRunning() && managed.Process is null)
        {
            return;
        }

        var settings = _settings.CurrentValue;
        var stopTimeout = TimeSpan.FromSeconds(settings.StopTimeoutSeconds);

        _logger.LogInformation(
            "Stopping worker process. AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, Reason={Reason}, ProcessId={ProcessId}",
            managed.Definition.AppInstanceId,
            managed.Definition.WorkerInstanceId,
            reason,
            managed.ProcessId);

        await PublishStoppingObservationIfEnabledAsync(managed, runtimeKind, reason, cancellationToken);

        var stoppedGracefully = await managed.RequestStopAsync(stopTimeout, cancellationToken);
        if (!stoppedGracefully)
        {
            _logger.LogWarning(
                "Worker process did not stop within timeout and will be killed. AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, StopTimeoutSeconds={StopTimeoutSeconds}, ProcessId={ProcessId}",
                managed.Definition.AppInstanceId,
                managed.Definition.WorkerInstanceId,
                settings.StopTimeoutSeconds,
                managed.ProcessId);

            var killed = await managed.KillAsync(stopTimeout, cancellationToken);
            if (!killed)
            {
                throw new TimeoutException(
                    $"Worker process '{managed.Definition.WorkerInstanceId}' did not exit within {settings.StopTimeoutSeconds} seconds after kill.");
            }
        }

        await PublishExitObservationIfEnabledAsync(managed, runtimeKind, reason, cancellationToken);

        _logger.LogInformation(
            "Worker process stopped. AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, ExitCode={ExitCode}",
            managed.Definition.AppInstanceId,
            managed.Definition.WorkerInstanceId,
            managed.LastExitCode);
    }

    private async Task HandleWorkerFailureAsync(
        ManagedWorkerProcess managed,
        string? runtimeKind,
        string phase,
        Exception exception,
        CancellationToken cancellationToken)
    {
        _logger.LogError(
            exception,
            "WorkerManager failed to {Phase}. AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, WorkerTypeKey={WorkerTypeKey}",
            phase,
            managed.Definition.AppInstanceId,
            managed.Definition.WorkerInstanceId,
            managed.Definition.WorkerTypeKey);

        if (string.IsNullOrWhiteSpace(runtimeKind))
        {
            return;
        }

        var observation = CreateObservation(
            managed,
            runtimeKind,
            WorkerObservedStates.Failed,
            managed.LastStartUtc,
            DateTimeOffset.UtcNow,
            managed.LastExitUtc,
            $"WorkerManager failed to {phase}: {exception.Message}");

        await TryPublishObservationAsync(observation, touchAppInstanceHeartbeat: false, cancellationToken);
    }

    private async Task TouchHostHeartbeatIfEnabledAsync(string hostIdentity, CancellationToken cancellationToken)
    {
        if (!ShouldPublishRuntimeToOmp())
        {
            return;
        }

        try
        {
            await _runtimeRepository.TouchHostHeartbeatAsync(hostIdentity, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to publish manager heartbeat. HostIdentity={HostIdentity}", hostIdentity);
        }
    }

    /// <summary>
    /// Marks worker runtime rows on this host Unknown once nothing has written them for
    /// several refresh intervals (R12-D8/F10).
    /// </summary>
    /// <remarks>
    /// The threshold is derived from the manager's own cadence rather than configured
    /// separately: six refresh intervals, and never less than 60 s. A healthy worker is
    /// rewritten every RefreshSeconds (15 s deployed), so six intervals is five missed
    /// cycles -- far outside normal jitter, well inside the window where a stale Running
    /// row can still mislead a deploy. A separate setting would be a second place for the
    /// same number to live and drift from the loop it describes.
    /// </remarks>
    private async Task DowngradeStaleWorkerStatesIfEnabledAsync(
        string hostIdentity,
        CancellationToken cancellationToken)
    {
        if (!ShouldPublishRuntimeToOmp())
        {
            return;
        }

        var refreshSeconds = Math.Max(1, _settings.CurrentValue.RefreshSeconds);
        var staleAfterSeconds = Math.Max(60, refreshSeconds * 6);

        try
        {
            var downgraded = await _runtimeRepository.DowngradeStaleWorkerStatesAsync(
                hostIdentity,
                staleAfterSeconds,
                cancellationToken);

            if (downgraded > 0)
            {
                _logger.LogWarning(
                    "Downgraded stale worker runtime states to Unknown because nothing had written them within the staleness window. HostIdentity={HostIdentity}, DowngradedCount={DowngradedCount}, StaleAfterSeconds={StaleAfterSeconds}",
                    hostIdentity,
                    downgraded,
                    staleAfterSeconds);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort, like the heartbeat above: failing to downgrade must not take the
            // reconcile cycle with it, and the next cycle retries.
            _logger.LogWarning(
                ex,
                "Failed to downgrade stale worker runtime states. HostIdentity={HostIdentity}",
                hostIdentity);
        }
    }

    private async Task PublishStartingObservationIfEnabledAsync(
        ManagedWorkerProcess managed,
        string? runtimeKind,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runtimeKind))
        {
            return;
        }

        var observation = CreateObservation(
            managed,
            runtimeKind,
            WorkerObservedStates.Starting,
            managed.LastStartUtc,
            null,
            null,
            "worker process started");

        await TryPublishObservationAsync(observation, touchAppInstanceHeartbeat: true, cancellationToken);
    }

    private async Task PublishRunningObservationIfEnabledAsync(
        ManagedWorkerProcess managed,
        string? runtimeKind,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runtimeKind))
        {
            return;
        }

        // R7-F7. A worker draining for a pending restart heartbeats as Draining, not
        // Running: same liveness cadence, but the Portal and the deployment
        // diagnostics can finally see that the worker is parked mid-drain instead of
        // admitting work.
        var observation = CreateObservation(
            managed,
            runtimeKind,
            managed.GetRunningObservationState(),
            managed.LastStartUtc,
            DateTimeOffset.UtcNow,
            null,
            managed.IsDraining ? "worker process draining" : "worker process running");

        await TryPublishObservationAsync(observation, touchAppInstanceHeartbeat: true, cancellationToken);
    }

    private async Task PublishStoppingObservationIfEnabledAsync(
        ManagedWorkerProcess managed,
        string? runtimeKind,
        string reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runtimeKind))
        {
            return;
        }

        var observation = CreateObservation(
            managed,
            runtimeKind,
            WorkerObservedStates.Stopping,
            managed.LastStartUtc,
            null,
            null,
            reason);

        await TryPublishObservationAsync(observation, touchAppInstanceHeartbeat: false, cancellationToken);
    }

    private async Task PublishExitObservationIfEnabledAsync(
        ManagedWorkerProcess managed,
        string? runtimeKind,
        string reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runtimeKind))
        {
            return;
        }

        var observedState = managed.LastExitCode.GetValueOrDefault() == 0
            ? WorkerObservedStates.Stopped
            : WorkerObservedStates.Failed;

        var observation = CreateObservation(
            managed,
            runtimeKind,
            observedState,
            managed.LastStartUtc,
            null,
            managed.LastExitUtc,
            BuildExitMessage(managed, reason));

        await TryPublishObservationAsync(observation, touchAppInstanceHeartbeat: false, cancellationToken);
    }

    private async Task TryPublishObservationAsync(
        WorkerRuntimeObservation observation,
        bool touchAppInstanceHeartbeat,
        CancellationToken cancellationToken)
    {
        try
        {
            await _runtimeRepository.PublishObservationAsync(observation, touchAppInstanceHeartbeat, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish worker runtime observation. AppInstanceId={AppInstanceId}, WorkerInstanceId={WorkerInstanceId}, ObservedState={ObservedState}",
                observation.AppInstanceId,
                observation.WorkerInstanceId,
                observation.ObservedState);
        }
    }

    private bool ShouldPublishRuntimeToOmp()
    {
        return string.Equals(
            _settings.CurrentValue.GetCatalogMode(),
            WorkerCatalogModes.OmpDatabase,
            StringComparison.OrdinalIgnoreCase);
    }

    private string? GetRuntimeKindOrNull()
    {
        if (!ShouldPublishRuntimeToOmp())
        {
            return null;
        }

        var ompDatabase = _settings.CurrentValue.OmpDatabase;
        if (ompDatabase is null || string.IsNullOrWhiteSpace(ompDatabase.RuntimeKind))
        {
            return null;
        }

        return ompDatabase.RuntimeKind.Trim();
    }

    private static WorkerRuntimeObservation CreateObservation(
        ManagedWorkerProcess managed,
        string runtimeKind,
        byte observedState,
        DateTimeOffset? startedUtc,
        DateTimeOffset? lastSeenUtc,
        DateTimeOffset? lastExitUtc,
        string statusMessage)
    {
        // R12-F2. The artifact witness is published under exactly the same condition as
        // the process id: there has to be a live process for either to mean anything. A
        // stopped or never-started worker reports NULL, and the diagnostics scripts print
        // that as a stated unknown -- the alternative, reporting the definition's artifact
        // regardless, would have every stopped worker claiming to run the desired version.
        var isLive = managed.IsRunning();

        return new WorkerRuntimeObservation
        {
            AppInstanceId = managed.Definition.AppInstanceId,
            WorkerInstanceId = managed.Definition.WorkerInstanceId,
            WorkerInstanceKey = managed.Definition.WorkerInstanceKey,
            RuntimeKind = runtimeKind,
            WorkerTypeKey = managed.Definition.WorkerTypeKey,
            ObservedState = observedState,
            ProcessId = isLive ? managed.ProcessId : null,
            StartedUtc = startedUtc,
            LastSeenUtc = lastSeenUtc,
            LastExitUtc = lastExitUtc,
            LastExitCode = managed.LastExitCode,
            StatusMessage = statusMessage,
            RuntimeArtifactId = isLive ? managed.StartedArtifactId : null,
            RuntimeArtifactVersion = isLive ? managed.StartedArtifactVersion : null,
            RuntimeHostArtifactId = isLive ? managed.StartedHostArtifactId : null,
            RuntimeHostArtifactVersion = isLive ? managed.StartedHostArtifactVersion : null
        };
    }

    private static string BuildExitMessage(ManagedWorkerProcess managed, string reason)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? "worker process exited"
            : reason.Trim();

        return managed.LastExitCode.GetValueOrDefault() == 0
            ? normalizedReason
            : $"{normalizedReason}; exit code {managed.LastExitCode}";
    }

    private static string ResolvePath(string path)
    {
        return PathResolutionUtility.ResolvePath(path);
    }

    /// <summary>
    /// Resolves the WorkerProcessHost executable, and with it the artifact that executable
    /// came from (R12-F2).
    /// </summary>
    /// <remarks>
    /// A configured WorkerManager:WorkerProcessPath names a file and nothing else, so it
    /// yields no artifact identity. That is reported as unknown rather than guessed: the
    /// whole point of this finding is that an unverifiable version must read as
    /// unverifiable, not as agreement.
    /// </remarks>
    private async Task<ResolvedWorkerProcessHost> ResolveWorkerProcessHostAsync(
        WorkerManagerSettings settings,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(settings.WorkerProcessPath))
        {
            return new ResolvedWorkerProcessHost(ResolvePath(settings.WorkerProcessPath), null, null);
        }

        if (!string.Equals(settings.GetCatalogMode(), WorkerCatalogModes.OmpDatabase, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "WorkerManager:WorkerProcessPath must be configured unless WorkerManager:CatalogMode is 'OmpDatabase'.");
        }

        var hostKey = settings.ResolveHostKey();
        var workerProcessHost = await _runtimeRepository.ResolveWorkerProcessHostAsync(hostKey, cancellationToken);
        if (workerProcessHost is null || string.IsNullOrWhiteSpace(workerProcessHost.Path))
        {
            throw new InvalidOperationException(
                $"Could not resolve a provisioned OMP Worker Process Host artifact for HostKey '{hostKey}'.");
        }

        return workerProcessHost;
    }

    /// <summary>
    /// Preflight for a replacement definition: can we actually start it? Used to
    /// avoid stopping a healthy worker for a configuration we already know is broken
    /// (R6-F3). Same readability checks the start path performs, without throwing.
    /// </summary>
    /// <remarks>
    /// The start path validates two files; this checked only the plugin assembly, so the
    /// resolved WorkerProcessHost executable -- which comes from the same volatile
    /// HostArtifactStates join R6-F3 was written to defend against -- could still fail
    /// after the worker had already been drained and stopped. That is R6-F3's exact
    /// failure mode reproduced through the un-preflighted file, and each retry burns a
    /// restart-policy slot (R7-F2).
    /// </remarks>
    private async Task<(bool IsStartable, string Problem)> IsStartableDefinitionAsync(
        DesiredWorkerInstance desired,
        ResolvedWorkerProcessHost? preResolvedHost,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidateReadableStartupFile(desired.PluginAssemblyPath, "Worker plugin assembly");

            // The caller passes the host it already resolved this cycle so a fleet-wide
            // host rollout does not issue one identical SQL resolve per worker. The
            // FILE validation still runs on every call — readability is exactly the
            // thing that can change between the resolve and this preflight (R7-F2),
            // so only the lookup is reused, never its verdict about the file.
            var workerProcessHost = preResolvedHost
                ?? await ResolveWorkerProcessHostAsync(_settings.CurrentValue, cancellationToken);
            ValidateReadableStartupFile(workerProcessHost.Path, "Resolved WorkerProcessHost executable");
            return (true, string.Empty);
        }
        catch (InvalidOperationException ex)
        {
            return (false, ex.Message);
        }
        catch (DbException ex)
        {
            // Without this, a transient database error during the preflight escaped to
            // the per-worker catch and published a bogus Failed observation for a
            // HEALTHY running worker. Not startable this cycle is the honest verdict:
            // the worker is left untouched and the change is retried next cycle,
            // which is the same R6-F3 outcome as an unreadable file.
            return (false, ex.Message);
        }
    }

    private static void ValidateReadableStartupFile(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"{description} path is not configured.");
        }

        try
        {
            // This is a diagnostic preflight check only. Process.Start and the worker host still
            // handle the authoritative file-system state because paths can change after validation.
            using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is ArgumentException
            or DirectoryNotFoundException
            or FileNotFoundException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{description} is not readable: '{path}'.", ex);
        }
    }

    private static void StartWorkerProcess(Process process, Guid workerInstanceId, string workerProcessPath)
    {
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    $"Failed to start worker process for WorkerInstanceId '{workerInstanceId}'.");
            }
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException
            or FileNotFoundException
            or UnauthorizedAccessException
            or Win32Exception)
        {
            throw new InvalidOperationException(
                $"Failed to start WorkerProcessHost for WorkerInstanceId '{workerInstanceId}'. Path='{workerProcessPath}'.",
                ex);
        }
    }

    internal static Process CreateWorkerProcess(
        string workerProcessPath,
        DesiredWorkerInstance desired,
        ResolvedWorkerProcessHost workerProcessHost,
        string ompConnectionString)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = workerProcessPath,
            WorkingDirectory = Path.GetDirectoryName(workerProcessPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Worker plugins use their own logging providers. Redirecting these streams here would
            // require an always-drained pipe and can block noisy workers if the manager falls behind.
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };

        startInfo.ArgumentList.Add($"--WorkerProcess:AppInstanceId={desired.AppInstanceId:D}");
        startInfo.ArgumentList.Add($"--WorkerProcess:WorkerInstanceId={desired.WorkerInstanceId:D}");
        startInfo.ArgumentList.Add($"--WorkerProcess:WorkerInstanceKey={desired.WorkerInstanceKey}");
        startInfo.ArgumentList.Add($"--WorkerProcess:WorkerTypeKey={desired.WorkerTypeKey}");
        startInfo.ArgumentList.Add($"--WorkerProcess:PluginAssemblyPath={desired.PluginAssemblyPath}");
        if (!string.IsNullOrWhiteSpace(desired.InstallRootPath))
        {
            startInfo.ArgumentList.Add($"--WorkerProcess:PluginArtifactRootPath={desired.InstallRootPath}");
        }

        if (!string.IsNullOrWhiteSpace(workerProcessHost.Version))
        {
            startInfo.ArgumentList.Add("--WorkerProcess:WorkerHostComponentKey=omp-workerprocesshost");
            startInfo.ArgumentList.Add($"--WorkerProcess:WorkerHostArtifactVersion={workerProcessHost.Version}");
        }
        startInfo.ArgumentList.Add($"--WorkerProcess:ShutdownEventName={desired.ShutdownEventName}");
        var workerConfigurationJson = desired.ConfigurationJson;
        if (!string.IsNullOrWhiteSpace(workerConfigurationJson))
        {
            // Worker configuration may contain module-specific values. Keep it out of
            // process command lines and let WorkerProcessHost read it from environment config.
            startInfo.Environment["WorkerProcess__ConfigurationJson"] = workerConfigurationJson;
        }

        var normalizedOmpConnectionString = ompConnectionString.Trim();
        // Worker host and plugins are OMP-provisioned code running in the same Windows
        // service trust boundary. Environment variables can still be read by the same
        // service account or local administrators, but they avoid command-line exposure;
        // service account isolation and filesystem ACLs are the intended protection boundary.
        startInfo.Environment["ConnectionStrings__OmpDb"] = normalizedOmpConnectionString;

        return new Process
        {
            StartInfo = startInfo,
            // Exit detection is intentionally polling-based through ManagedWorkerProcess so that
            // reconciliation observes all workers in one place instead of mixing event callbacks.
            EnableRaisingEvents = false
        };
    }

    private async Task CleanupOrphanedWorkerProcessesOnStartupAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var settings = _settings.CurrentValue;

        string workerProcessPath;
        try
        {
            workerProcessPath = Path.GetFullPath((await ResolveWorkerProcessHostAsync(settings, cancellationToken)).Path);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "WorkerManager skipped startup orphan scan because the worker host executable path could not be resolved.");
            return;
        }

        var managerProcessPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(managerProcessPath))
        {
            _logger.LogWarning("WorkerManager skipped startup orphan scan because the manager executable path could not be resolved.");
            return;
        }

        IReadOnlyList<OrphanedWorkerProcess> orphanedProcesses;
        try
        {
            orphanedProcesses = FindOrphanedWorkerProcesses(
                _logger,
                workerProcessPath,
                Path.GetFullPath(managerProcessPath));
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or Win32Exception or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            _logger.LogWarning(
                ex,
                "WorkerManager skipped startup orphan scan because running worker process metadata could not be enumerated.");
            return;
        }

        if (orphanedProcesses.Count == 0)
        {
            return;
        }

        var stopTimeout = TimeSpan.FromSeconds(settings.StopTimeoutSeconds);
        var cleanedCount = 0;

        foreach (var orphan in orphanedProcesses)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var process = Process.GetProcessById(orphan.ProcessId);
                if (process.HasExited)
                {
                    cleanedCount++;
                    continue;
                }

                _logger.LogWarning(
                    "WorkerManager is stopping an orphaned worker host process discovered during startup. ProcessId={ProcessId}, ParentProcessId={ParentProcessId}, WorkerProcessPath={WorkerProcessPath}, CommandLine={CommandLine}",
                    orphan.ProcessId,
                    orphan.ParentProcessId,
                    workerProcessPath,
                    orphan.CommandLine);

                // WorkerProcessHost owns the plugin lifetime by default. The setting
                // allows hosts with intentionally independent plugin children to opt out.
                process.Kill(entireProcessTree: settings.CleanupOrphansKillProcessTree);
                if (!await WaitForProcessExitAsync(process, stopTimeout, cancellationToken))
                {
                    throw new TimeoutException(
                        $"Orphaned worker host process '{orphan.ProcessId}' did not exit within {settings.StopTimeoutSeconds} seconds.");
                }

                cleanedCount++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ArgumentException)
            {
                cleanedCount++;
            }
            catch (InvalidOperationException)
            {
                cleanedCount++;
            }
            catch (TimeoutException ex)
            {
                _logger.LogWarning(
                    ex,
                    "WorkerManager timed out while stopping an orphaned worker host process during startup. ProcessId={ProcessId}",
                    orphan.ProcessId);
            }
            catch (Exception ex) when (ex is Win32Exception or NotSupportedException)
            {
                _logger.LogWarning(
                    ex,
                    "WorkerManager could not stop an orphaned worker host process during startup. ProcessId={ProcessId}",
                    orphan.ProcessId);
            }
        }

        if (cleanedCount > 0)
        {
            _logger.LogWarning(
                "WorkerManager cleaned orphaned worker host processes during startup. Count={Count}, WorkerProcessPath={WorkerProcessPath}",
                cleanedCount,
                workerProcessPath);
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<OrphanedWorkerProcess> FindOrphanedWorkerProcesses(
        ILogger<WorkerManagerHostedService> logger,
        string workerProcessPath,
        string managerProcessPath)
    {
        var normalizedWorkerProcessPath = Path.GetFullPath(workerProcessPath);
        var result = new List<OrphanedWorkerProcess>();
        using var searcher = new ManagementObjectSearcher(CreateWorkerProcessHostQuery());
        using var processes = searcher.Get();

        foreach (ManagementObject process in processes)
        {
            using (process)
            {
                var processId = ReadManagementUInt32(process, "ProcessId");
                if (processId <= 0 || processId == Environment.ProcessId)
                {
                    continue;
                }

                var executablePath = process["ExecutablePath"] as string;
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    continue;
                }

                if (!string.Equals(
                        Path.GetFullPath(executablePath),
                        normalizedWorkerProcessPath,
                        GetPathComparison()))
                {
                    continue;
                }

                var commandLine = process["CommandLine"] as string;
                if (!HasWorkerHostOwnershipMarkers(commandLine))
                {
                    logger.LogDebug(
                        "WorkerManager skipped a process with the worker host executable name because its command line did not match expected worker ownership markers. ProcessId={ProcessId}, ExecutablePath={ExecutablePath}",
                        processId,
                        executablePath);
                    continue;
                }

                var parentProcessId = ReadManagementUInt32(process, "ParentProcessId");
                var childStartTimeUtc = ReadManagementDateTimeUtc(process, "CreationDate");
                if (IsLiveWorkerManagerParent(parentProcessId, managerProcessPath, childStartTimeUtc))
                {
                    continue;
                }

                result.Add(new OrphanedWorkerProcess(
                    processId,
                    parentProcessId,
                    executablePath,
                    FormatOrphanCommandLine(commandLine)));
            }
        }

        return result;
    }

    private static bool HasWorkerHostOwnershipMarkers(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        return commandLine.Contains("--WorkerProcess:AppInstanceId=", StringComparison.OrdinalIgnoreCase)
            && commandLine.Contains("--WorkerProcess:WorkerInstanceId=", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FormatOrphanCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var normalized = commandLine.Trim();
        const int maxLength = 500;
        return normalized.Length > maxLength
            ? normalized[..maxLength] + "..."
            : normalized;
    }

    private static string CreateWorkerProcessHostQuery()
    {
        var executableNameLiteral = CreateSafeWqlStringLiteral(WorkerProcessHostExecutableName);
        return "SELECT ProcessId, ParentProcessId, ExecutablePath, CommandLine, CreationDate FROM Win32_Process WHERE Name = "
            + executableNameLiteral;
    }

    private static string CreateSafeWqlStringLiteral(string value)
    {
        var unsupportedCharacter = value
            .Where(ch => !IsSafeWqlStringLiteralCharacter(ch))
            .Select(ch => (char?)ch)
            .FirstOrDefault();

        if (unsupportedCharacter.HasValue)
        {
            throw new InvalidOperationException(
                $"WorkerProcessHost executable name contains an unsupported WMI query character: '{value}'.");
        }

        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static bool IsSafeWqlStringLiteralCharacter(char ch)
        => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-';

    [SupportedOSPlatform("windows")]
    private static bool IsLiveWorkerManagerParent(
        int parentProcessId,
        string managerProcessPath,
        DateTimeOffset? childStartTimeUtc)
    {
        if (parentProcessId <= 0)
        {
            return false;
        }

        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            if (parent.HasExited)
            {
                return false;
            }

            // Guard against parent-PID reuse: Windows recycles process ids, so the
            // process now holding the recorded ParentProcessId may be a newer,
            // unrelated process. A genuine parent always starts before its child, so
            // a "parent" that started after the worker host cannot be its real parent
            // and the worker host is therefore orphaned (R5-F2).
            if (childStartTimeUtc.HasValue
                && parent.StartTime.ToUniversalTime() > childStartTimeUtc.Value.UtcDateTime)
            {
                return false;
            }

            var executablePath = parent.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return false;
            }

            // Match on the WorkerManager executable NAME, not this manager's exact
            // install path: a second WorkerManager installed at a different path
            // (blue/green or side-by-side upgrade) is a legitimate live parent for
            // its own worker hosts, and matching only our own path made this
            // manager classify those healthy hosts as orphans and kill them
            // (R4-F5). Same-path coexistence still matches by name.
            var managerFileName = Path.GetFileName(managerProcessPath);
            return string.Equals(
                Path.GetFileName(executablePath),
                managerFileName,
                GetPathComparison());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int ReadManagementUInt32(ManagementBaseObject process, string propertyName)
    {
        return process[propertyName] switch
        {
            uint value => checked((int)value),
            int value => value,
            _ => 0
        };
    }

    [SupportedOSPlatform("windows")]
    private static DateTimeOffset? ReadManagementDateTimeUtc(ManagementBaseObject process, string propertyName)
    {
        // Win32_Process exposes CreationDate as a CIM_DATETIME string; a missing or
        // unparseable value simply disables the start-time PID-reuse guard (R5-F2).
        if (process[propertyName] is not string rawValue || string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        try
        {
            return ManagementDateTimeConverter.ToDateTime(rawValue).ToUniversalTime();
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException or FormatException)
        {
            return null;
        }
    }

    private static StringComparison GetPathComparison()
        => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static async Task<bool> WaitForProcessExitAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(waitCts.Token);
            return true;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static bool IsRecoverableWorkerManagerFailure(Exception exception)
        => exception is InvalidOperationException
            or IOException
            or DbException
            or UnauthorizedAccessException
            or TimeoutException
            or ArgumentException
            or ManagementException
            or Win32Exception
            or NotSupportedException
            // System.Management surfaces DCOM faults as COMException, not ManagementException:
            // RPC_S_SERVER_UNAVAILABLE when the WMI service restarts, 0x80041033 during
            // shutdown. Only one of the five WMI call sites in the solution listed it, and the
            // startup orphan scan runs outside this cycle guard entirely -- so a COMException
            // there faulted ExecuteAsync and crash-looped the service (R8-P4-6).
            or System.Runtime.InteropServices.COMException;

    private static EventWaitHandle CreateShutdownEvent(DesiredWorkerInstance definition)
    {
        // The named event is a cooperative same-host shutdown signal between WorkerManager and
        // the OMP-provisioned WorkerProcessHost. CurrentUserOnly scopes the object to the service
        // identity, and createdNew protects against stale or pre-created handles with the same
        // deterministic worker-instance name.
        var shutdownEvent = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.ManualReset,
            name: definition.ShutdownEventName,
            options: ShutdownEventOptions,
            createdNew: out var createdNew);

        if (createdNew)
        {
            return shutdownEvent;
        }

        shutdownEvent.Dispose();
        throw new InvalidOperationException(
            $"Worker shutdown event already exists for WorkerInstanceId '{definition.WorkerInstanceId}'. Refusing to reuse named event '{definition.ShutdownEventName}'.");
    }

    // Keep these names in sync with WorkerProcessSettings.BuildDrainEventName /
    // BuildBusyEventName in OpenModulePlatform.WorkerProcessHost.
    private static string BuildDrainEventName(Guid workerInstanceId)
        => $"OpenModulePlatform.WorkerDrain.{workerInstanceId:N}";

    private static string BuildBusyEventName(Guid workerInstanceId)
        => $"OpenModulePlatform.WorkerBusy.{workerInstanceId:N}";

    private static EventWaitHandle CreateManagedWorkerEvent(string eventName)
    {
        // Same ownership rules as the shutdown event: the manager creates the
        // named object and refuses pre-existing handles with the same name.
        var namedEvent = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.ManualReset,
            name: eventName,
            options: ShutdownEventOptions,
            createdNew: out var createdNew);

        if (createdNew)
        {
            return namedEvent;
        }

        namedEvent.Dispose();
        throw new InvalidOperationException(
            $"Managed worker event already exists. Refusing to reuse named event '{eventName}'.");
    }

    private sealed class WorkerStartupResources : IDisposable
    {
        private bool _ownsResources = true;

        private EventWaitHandle? _shutdownEvent;
        private EventWaitHandle? _drainEvent;
        private EventWaitHandle? _busyEvent;

        public EventWaitHandle ShutdownEvent
            => _shutdownEvent ?? throw new InvalidOperationException("The shutdown event has not been created yet.");

        public EventWaitHandle DrainEvent
            => _drainEvent ?? throw new InvalidOperationException("The drain event has not been created yet.");

        public EventWaitHandle BusyEvent
            => _busyEvent ?? throw new InvalidOperationException("The busy event has not been created yet.");

        public void SetShutdownEvent(EventWaitHandle handle) => _shutdownEvent = handle;

        public void SetDrainEvent(EventWaitHandle handle) => _drainEvent = handle;

        public void SetBusyEvent(EventWaitHandle handle) => _busyEvent = handle;

        public Process? Process { get; private set; }

        public void AttachProcess(Process process)
        {
            Process = process;
        }

        public void ReleaseOwnership()
        {
            _ownsResources = false;
        }

        public void Dispose()
        {
            if (!_ownsResources)
            {
                return;
            }

            Process?.Dispose();
            _shutdownEvent?.Dispose();
            _drainEvent?.Dispose();
            _busyEvent?.Dispose();
        }
    }

    private sealed record OrphanedWorkerProcess(
        int ProcessId,
        int ParentProcessId,
        string ExecutablePath,
        string? CommandLine);
}

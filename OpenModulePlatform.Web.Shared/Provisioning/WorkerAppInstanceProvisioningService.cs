// File: OpenModulePlatform.Web.Shared/Provisioning/WorkerAppInstanceProvisioningService.cs
using System.Data;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenModulePlatform.Web.Shared.Navigation;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Web.Shared.Provisioning;

/// <summary>
/// Creates one logical worker app instance plus one worker instance per selected host, for
/// module web apps that must not write SQL against platform tables themselves.
/// </summary>
/// <remarks>
/// <para>
/// The whole operation runs in one transaction under an application lock keyed on the module
/// and app instance key, so a failure leaves nothing behind and two concurrent calls for the
/// same key serialise: the second one finds the first one's rows and reuses them.
/// </para>
/// <para>
/// A repeated call with the same key reuses the matching app instance and only adds workers
/// for hosts that have none. Existing app and worker rows are never updated.
/// </para>
/// <para>
/// Each new worker's <c>ConfigurationJson</c> carries the HostAgent RPC settings of its own
/// host, resolved by <see cref="ResolveHostAgentRpcAsync"/>: the host's active host
/// configuration <c>values.hostAgentRpc</c>, else the pipe an active HostAgent registered.
/// When any host has neither, the call fails and nothing is created; so does an active host
/// configuration that cannot be read. A HostAgent older than the
/// <c>omp.HostAgentRuntimeStates.RpcPipeName</c> registration never registers a pipe, so its
/// host needs <c>values.hostAgentRpc.pipeName</c> set explicitly. After a downgrade to such an
/// agent the pipe the newer agent registered stays trusted; it is still the host's pipe.
/// </para>
/// <para>
/// Authorization is <c>OMP.Portal.Admin</c>, or the request's module permission when an
/// enabled app of the same module declares it. Who holds that permission is RBAC's answer:
/// a permission granted only through an ambient role (Everyone, AuthenticatedUsers) is an
/// installation policy decision that this service does not override.
/// </para>
/// </remarks>
public sealed class WorkerAppInstanceProvisioningService
{
    /// <summary>Default pipe name prefix used by HostAgent when its RpcPipeName setting is empty.</summary>
    /// <remarks>
    /// Informational only: the service never derives a worker's pipe from it, because a
    /// HostAgent may serve a custom pipe. See <see cref="ResolveHostAgentRpcAsync"/>.
    /// </remarks>
    public const string DefaultHostAgentPipePrefix = "OpenModulePlatform.HostAgent.";

    /// <summary>
    /// The shortest freshness window for a HostAgent registration: how recently the agent
    /// must have published its runtime state for its registered RPC pipe to count, twenty
    /// times the default 30-second HostAgent refresh interval.
    /// </summary>
    /// <remarks>
    /// An agent that publishes its <c>RefreshSeconds</c> gets
    /// <see cref="HostAgentRegistrationRefreshMultiplier"/> times that interval when it is
    /// longer, so a healthy agent with a slow cycle never looks stale. See
    /// <see cref="GetRegistrationMaxAgeSeconds"/>.
    /// </remarks>
    public const int HostAgentRegistrationMaxAgeSeconds = 600;

    /// <summary>
    /// How many of its own refresh intervals a HostAgent may miss before its registration is
    /// stale, for an agent whose interval makes that longer than
    /// <see cref="HostAgentRegistrationMaxAgeSeconds"/>.
    /// </summary>
    public const int HostAgentRegistrationRefreshMultiplier = 3;

    private const int AppInstanceKeyMaxLength = 100;
    private const int DisplayNameMaxLength = 200;
    private const int WorkerInstanceKeyMaxLength = 150;
    private const int LockTimeoutMilliseconds = 30000;

    private static readonly Regex InstanceKeyPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9_.-]*$",
        RegexOptions.CultureInvariant);

    private static readonly Regex ConfigurationSegmentPattern = new(
        "^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant);

    private readonly SqlConnectionFactory _db;
    private readonly Func<ClaimsPrincipal, CancellationToken, Task<IReadOnlySet<string>>> _permissions;
    private readonly ILogger _logger;

    public WorkerAppInstanceProvisioningService(SqlConnectionFactory db, RbacService rbac)
        : this(db, rbac, NullLogger<WorkerAppInstanceProvisioningService>.Instance)
    {
    }

    public WorkerAppInstanceProvisioningService(
        SqlConnectionFactory db,
        RbacService rbac,
        ILogger<WorkerAppInstanceProvisioningService> logger)
        : this(db, async (user, ct) => await rbac.GetUserPermissionsAsync(user, ct), logger)
    {
    }

    internal WorkerAppInstanceProvisioningService(
        SqlConnectionFactory db,
        Func<ClaimsPrincipal, CancellationToken, Task<IReadOnlySet<string>>> permissions,
        ILogger? logger = null)
    {
        _db = db;
        _permissions = permissions;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Provisions (or completes) a worker app instance for the caller.
    /// </summary>
    /// <exception cref="WorkerAppInstanceProvisioningException">
    /// The request was refused; nothing from this call was written.
    /// </exception>
    public async Task<WorkerAppInstanceProvisioningResult> ProvisionWorkerAppInstanceAsync(
        ClaimsPrincipal caller,
        WorkerAppInstanceProvisioningRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);

        var input = ValidateRequest(request);

        // Permissions come from RBAC before the transaction: RBAC opens its own connection.
        var permissions = caller.Identity?.IsAuthenticated == true
            ? await _permissions(caller, ct)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!permissions.Contains(PortalAdminNavigation.AdminPermission)
            && (input.ModuleAdministrationPermission is null || !permissions.Contains(input.ModuleAdministrationPermission)))
        {
            throw Refuse(
                WorkerAppInstanceProvisioningError.Unauthorized,
                $"The caller may not provision app instances of module '{input.ModuleKey}'.");
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        try
        {
            var result = await ProvisionInTransactionAsync(conn, tx, input, permissions, ct);
            await tx.CommitAsync(ct);
            return result;
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            await RollbackAsync(tx, input);
            throw new WorkerAppInstanceProvisioningException(
                WorkerAppInstanceProvisioningError.KeyCollision,
                $"App instance key '{input.AppInstanceKey}' or one of its worker keys is already used. Nothing was created. ({ex.Message})");
        }
        catch
        {
            await RollbackAsync(tx, input);
            throw;
        }
    }

    private async Task RollbackAsync(SqlTransaction tx, ValidatedRequest input)
    {
        try
        {
            await tx.RollbackAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The original exception is the one worth reporting, so this never replaces it.
            // An already finished transaction (SQL Server rolled a doomed transaction back, or
            // the connection broke, which also discards it) is the expected case; anything
            // else is logged so it does not vanish.
            _logger.LogWarning(
                ex,
                "Rolling back worker app instance provisioning failed. ModuleKey={ModuleKey}, AppInstanceKey={AppInstanceKey}",
                input.ModuleKey,
                input.AppInstanceKey);
        }
    }

    private async Task<WorkerAppInstanceProvisioningResult> ProvisionInTransactionAsync(
        SqlConnection conn,
        SqlTransaction tx,
        ValidatedRequest input,
        IReadOnlySet<string> permissions,
        CancellationToken ct)
    {
        await AcquireLockAsync(conn, tx, input, ct);

        var app = await ReadAppAsync(conn, tx, input, ct);
        await AuthorizeAsync(conn, tx, input, app, permissions, ct);

        var template = input.TemplateAppInstanceId is Guid templateId
            ? await ReadTemplateAsync(conn, tx, templateId, app, ct)
            : null;

        var moduleInstance = await ResolveModuleInstanceAsync(conn, tx, input, app, template, ct);
        var existing = await ReadExistingAppInstanceAsync(conn, tx, moduleInstance.ModuleInstanceId, input.AppInstanceKey, ct);
        if (existing is not null && (existing.AppId != app.AppId || existing.HostId.HasValue || existing.TargetHostTemplateId.HasValue))
        {
            throw Refuse(
                WorkerAppInstanceProvisioningError.KeyCollision,
                $"App instance key '{input.AppInstanceKey}' is already used by another app instance in this module instance that is not a host-neutral instance of app '{input.AppKey}'.");
        }

        int? artifactId;
        string? installationName;
        if (existing is not null)
        {
            // A reused instance keeps its own binding: provisioning never rewrites existing rows.
            // Without an artifact WorkerManager never starts its workers, so new workers bound
            // to it would be a success report for something that cannot run.
            if (existing.ArtifactId is null)
            {
                throw Refuse(
                    WorkerAppInstanceProvisioningError.NoCompatibleArtifact,
                    $"Existing app instance '{input.AppInstanceKey}' has no artifact, so workers added to it would never start. " +
                    "Bind an artifact to it in the Portal, or use another app instance key. Nothing was created.");
            }

            artifactId = existing.ArtifactId;
            installationName = existing.InstallationName;
        }
        else
        {
            installationName = template is not null
                ? template.InstallationName
                : await ResolveInstallationNameAsync(conn, tx, moduleInstance.ModuleInstanceId, app, ct);
            artifactId = template is not null
                ? await RequireCompatibleTemplateArtifactAsync(conn, tx, app, template, ct)
                : await ResolveLatestCompatibleArtifactAsync(conn, tx, app, ct);
        }

        var hosts = await ReadHostsAsync(conn, tx, input, moduleInstance.InstanceId, ct);
        var existingWorkers = existing is null
            ? []
            : await ReadWorkersAsync(conn, tx, existing.AppInstanceId, ct);

        var appInstanceId = existing?.AppInstanceId ?? Guid.NewGuid();
        var plans = new List<WorkerPlan>();
        var missingRpc = new List<string>();
        foreach (var host in hosts)
        {
            var reused = existingWorkers.FirstOrDefault(worker => worker.HostId == host.HostId);
            if (reused is not null)
            {
                plans.Add(new WorkerPlan(host, reused.WorkerInstanceId, reused.WorkerInstanceKey, null, null));
                continue;
            }

            var (rpc, reason) = await ResolveHostAgentRpcAsync(conn, tx, host, input.DefaultRpcTimeoutSeconds, ct);
            if (rpc is null)
            {
                missingRpc.Add($"'{host.HostKey}' ({reason})");
                continue;
            }

            var key = BuildWorkerInstanceKey(input.AppInstanceKey, host);
            plans.Add(new WorkerPlan(host, Guid.NewGuid(), key, rpc, BuildConfigurationJson(input.RpcPath, rpc)));
        }

        if (missingRpc.Count > 0)
        {
            throw Refuse(
                WorkerAppInstanceProvisioningError.HostAgentRpcUnavailable,
                $"The platform has no HostAgent RPC settings for host(s) {string.Join(", ", missingRpc)}. " +
                "Start a HostAgent that registers its RPC pipe on the host, or record values.hostAgentRpc.pipeName in the host's active host configuration " +
                "(required for a HostAgent older than the RpcPipeName registration). Nothing was created.");
        }

        var newKeys = plans.Where(plan => plan.ConfigurationJson is not null).Select(plan => plan.WorkerInstanceKey).ToList();
        var clash = newKeys
            .GroupBy(key => key, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Concat(newKeys.Where(key => existingWorkers.Any(worker => string.Equals(worker.WorkerInstanceKey, key, StringComparison.OrdinalIgnoreCase))))
            .FirstOrDefault();
        if (clash is not null)
        {
            throw Refuse(
                WorkerAppInstanceProvisioningError.KeyCollision,
                $"Worker instance key '{clash}' is already used by another worker of app instance '{input.AppInstanceKey}'. Nothing was created.");
        }

        if (existing is null)
        {
            await InsertAppInstanceAsync(conn, tx, appInstanceId, moduleInstance.ModuleInstanceId, app.AppId, input, installationName, artifactId, ct);
        }

        foreach (var plan in plans.Where(plan => plan.ConfigurationJson is not null))
        {
            await InsertWorkerAsync(conn, tx, appInstanceId, artifactId, input.DisplayName, plan, ct);
        }

        return new WorkerAppInstanceProvisioningResult(
            appInstanceId,
            existing is null,
            moduleInstance.ModuleInstanceId,
            app.AppId,
            artifactId,
            installationName,
            plans.Select(plan => new ProvisionedWorkerInstance(
                    plan.WorkerInstanceId,
                    plan.Host.HostId,
                    plan.Host.HostKey,
                    plan.WorkerInstanceKey,
                    plan.ConfigurationJson is not null,
                    plan.Rpc?.PipeName))
                .ToList());
    }

    /// <summary>
    /// Resolves a host's HostAgent RPC settings from platform data. Returns the endpoint, or
    /// null plus the reason when the platform holds no trustworthy pipe for the host.
    /// </summary>
    /// <remarks>
    /// Sources, in order:
    /// <list type="number">
    /// <item>The host's active host configuration: of the <c>omp.HostConfigurationDocuments</c>
    /// rows for its HostKey with <c>IsActive = 1</c>, the one with the highest
    /// <c>ConfigurationVersion</c> (the platform's artifact version order, the same rule the
    /// package import uses for "latest"; document id breaks ties). When it has a
    /// <c>values.hostAgentRpc</c> object, <c>enabled: false</c> means the host has no usable
    /// RPC and the call fails, and its <c>pipeName</c> and <c>timeoutSeconds</c> win.</item>
    /// <item>Otherwise, or when that object has no <c>pipeName</c>, the pipe an active
    /// HostAgent registered for the host: <c>omp.HostAgentRuntimeStates.RpcPipeName</c> of a
    /// row with <c>IsActive = 1</c> whose <c>LastSeenUtc</c> lies within that row's freshness
    /// window, <see cref="GetRegistrationMaxAgeSeconds"/> of its published
    /// <c>RefreshSeconds</c>.</item>
    /// <item>Otherwise the host has no RPC settings and the whole call fails.</item>
    /// </list>
    /// A pipe is never derived from the host key alone: a HostAgent can serve a custom
    /// <c>RpcPipeName</c>, and a runtime-state row also survives a disabled or uninstalled
    /// agent. A HostAgent older than the <c>RpcPipeName</c> column never registers its pipe, so
    /// its host must have <c>values.hostAgentRpc.pipeName</c> set explicitly. Another host's
    /// pipe or another worker's configuration is never used. An active host configuration that
    /// is not a readable JSON object refuses the call with
    /// <see cref="WorkerAppInstanceProvisioningError.HostConfigurationInvalid"/> instead of
    /// falling back to the registration.
    /// <para>
    /// Accepted after a downgrade: an agent older than the registration leaves the
    /// <c>RpcPipeName</c> a newer agent wrote on its row and keeps refreshing
    /// <c>LastSeenUtc</c>, so that pipe stays trusted. It is still the pipe the host serves,
    /// because the pipe name comes from <c>HostAgent:RpcPipeName</c> (or the host key), never
    /// from the agent version. The log entry and the refusal name the registering agent's
    /// version so such a case stays visible.
    /// </para>
    /// </remarks>
    private async Task<(HostAgentRpcEndpoint? Endpoint, string? Reason)> ResolveHostAgentRpcAsync(
        SqlConnection conn,
        SqlTransaction tx,
        HostRow host,
        int defaultTimeoutSeconds,
        CancellationToken ct)
    {
        const string configurationSql = @"
SELECT d.HostConfigurationDocumentId, d.ConfigurationVersion, d.ConfigurationJson
FROM omp.HostConfigurationDocuments d
WHERE d.HostKey = @hostKey
  AND d.IsActive = 1;";

        // Dynamic SQL: against a database that predates RpcPipeName the batch must still
        // compile, and no registered pipe exists there. RefreshSeconds is newer still; without
        // it every row gets the default window. SQL Server measures the age, on the same clock
        // that wrote LastSeenUtc.
        const string registrationSql = @"
IF COL_LENGTH(N'omp.HostAgentRuntimeStates', N'RpcPipeName') IS NOT NULL
BEGIN
    DECLARE @sql nvarchar(max) = N'SELECT s.ServiceName, s.Version, s.RpcPipeName, '
        + CASE WHEN COL_LENGTH(N'omp.HostAgentRuntimeStates', N'RefreshSeconds') IS NULL
               THEN N'CAST(NULL AS int)'
               ELSE N's.RefreshSeconds'
          END
        + N', DATEDIFF_BIG(SECOND, s.LastSeenUtc, SYSUTCDATETIME())
          FROM omp.HostAgentRuntimeStates s
          WHERE s.HostId = @hostId
            AND s.IsActive = 1
          ORDER BY s.LastSeenUtc DESC, s.ServiceName;';
    EXEC sys.sp_executesql @sql, N'@hostId uniqueidentifier', @hostId = @hostId;
END;";

        var documents = new List<(int Id, string Version, string Json)>();
        await using (var cmd = new SqlCommand(configurationSql, conn, tx))
        {
            cmd.Parameters.Add("@hostKey", SqlDbType.NVarChar, 128).Value = host.HostKey;
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                documents.Add((rdr.GetInt32(0), rdr.GetString(1), rdr.GetString(2)));
            }
        }

        var active = documents
            .OrderByDescending(document => document.Version, VersionComparer.Instance)
            .ThenByDescending(document => document.Id)
            .Select(document => document.Json)
            .FirstOrDefault();
        var recorded = ReadRecordedRpc(active, host.HostKey);
        if (recorded is { Enabled: false })
        {
            return (null, "values.hostAgentRpc.enabled is false in its active host configuration");
        }

        var timeoutSeconds = recorded?.TimeoutSeconds ?? defaultTimeoutSeconds;
        if (!string.IsNullOrWhiteSpace(recorded?.PipeName))
        {
            return (new HostAgentRpcEndpoint(recorded.PipeName.Trim(), timeoutSeconds), null);
        }

        var registrations = new List<HostAgentRegistration>();
        await using (var cmd = new SqlCommand(registrationSql, conn, tx))
        {
            cmd.Parameters.Add("@hostId", SqlDbType.UniqueIdentifier).Value = host.HostId;
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                registrations.Add(new HostAgentRegistration(
                    rdr.GetString(0),
                    rdr.IsDBNull(1) ? null : rdr.GetString(1),
                    rdr.IsDBNull(2) ? null : rdr.GetString(2),
                    rdr.IsDBNull(3) ? null : rdr.GetInt32(3),
                    rdr.IsDBNull(4) ? null : rdr.GetInt64(4)));
            }
        }

        var trusted = registrations.FirstOrDefault(registration => registration.HasPipe && registration.IsFresh);
        if (trusted is null)
        {
            return (null, $"no values.hostAgentRpc.pipeName in its active host configuration, and {DescribeUntrustedRegistration(registrations)}");
        }

        var pipeName = trusted.PipeName!.Trim();
        _logger.LogInformation(
            "Resolved the HostAgent RPC pipe of host {HostKey} from the registration of HostAgent {ServiceName} version {AgentVersion}, last seen {AgeSeconds} s ago (window {WindowSeconds} s). PipeName={PipeName}",
            host.HostKey,
            trusted.ServiceName,
            trusted.Version ?? "unknown",
            trusted.AgeSeconds,
            trusted.MaxAgeSeconds,
            pipeName);
        return (new HostAgentRpcEndpoint(pipeName, timeoutSeconds), null);
    }

    /// <summary>
    /// The freshness window of a HostAgent registration: <see cref="HostAgentRegistrationMaxAgeSeconds"/>,
    /// or <see cref="HostAgentRegistrationRefreshMultiplier"/> times the agent's published
    /// <c>RefreshSeconds</c> when that is longer. A row without <c>RefreshSeconds</c> (an agent
    /// older than that column) gets the default window.
    /// </summary>
    public static long GetRegistrationMaxAgeSeconds(int? refreshSeconds)
        => refreshSeconds is int seconds && seconds > 0
            ? Math.Max(HostAgentRegistrationMaxAgeSeconds, (long)seconds * HostAgentRegistrationRefreshMultiplier)
            : HostAgentRegistrationMaxAgeSeconds;

    private static string DescribeUntrustedRegistration(IReadOnlyList<HostAgentRegistration> registrations)
    {
        // Rows come newest LastSeenUtc first, so the first one is the agent that matters.
        var latest = registrations.FirstOrDefault();
        if (latest is null)
        {
            return "no active HostAgent has registered for it";
        }

        var agent = $"its active HostAgent '{latest.ServiceName}' (version {latest.Version ?? "unknown"})";
        if (!latest.HasPipe)
        {
            return $"{agent} has registered no RPC pipe (an agent older than the RpcPipeName registration, or one with RPC disabled or not started)";
        }

        return latest.AgeSeconds is long age
            ? $"{agent} was last seen {age} s ago, outside its {latest.MaxAgeSeconds} s freshness window"
            : $"{agent} has never published a LastSeenUtc";
    }

    private static RecordedRpc? ReadRecordedRpc(string? configurationJson, string hostKey)
    {
        if (string.IsNullOrWhiteSpace(configurationJson))
        {
            return null;
        }

        // A configuration that cannot be read is not "no configuration": falling back to the
        // registration would hide a broken pipe pin or RPC opt-out. The table's ISJSON check
        // does not cover everything: it accepts duplicate property names (which JsonObject
        // rejects when the object is first read) and a JSON array, and a row may predate it.
        JsonObject? rpc;
        try
        {
            if (JsonNode.Parse(configurationJson) is not JsonObject document)
            {
                throw new FormatException("the document is not a JSON object");
            }

            rpc = GetProperty(GetProperty(document, "values") as JsonObject, "hostAgentRpc") as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            throw Refuse(
                WorkerAppInstanceProvisioningError.HostConfigurationInvalid,
                $"The active host configuration of host '{hostKey}' cannot be read ({ex.Message}). Fix or replace that host configuration. Nothing was created.");
        }

        if (rpc is null)
        {
            return null;
        }

        try
        {
            var enabled = GetProperty(rpc, "enabled") is JsonValue enabledValue ? enabledValue.GetValue<bool>() : true;
            var pipeName = GetProperty(rpc, "pipeName") is JsonValue pipeValue ? pipeValue.GetValue<string>() : null;
            int? timeout = GetProperty(rpc, "timeoutSeconds") is JsonValue timeoutValue ? timeoutValue.GetValue<int>() : null;
            if (timeout is < 1)
            {
                throw new FormatException("timeoutSeconds must be at least 1.");
            }

            return new RecordedRpc(enabled, pipeName, timeout);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
        {
            throw Refuse(
                WorkerAppInstanceProvisioningError.HostAgentRpcUnavailable,
                $"The host configuration of host '{hostKey}' has an invalid values.hostAgentRpc object ({ex.Message}). Nothing was created.");
        }
    }

    private static JsonNode? GetProperty(JsonObject? obj, string name)
    {
        if (obj is null)
        {
            return null;
        }

        foreach (var property in obj)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    internal static string BuildConfigurationJson(IReadOnlyList<string> path, HostAgentRpcEndpoint rpc)
    {
        JsonObject node = new()
        {
            ["Enabled"] = true,
            ["PipeName"] = rpc.PipeName,
            ["TimeoutSeconds"] = rpc.TimeoutSeconds
        };

        for (var index = path.Count - 1; index >= 0; index--)
        {
            node = new JsonObject { [path[index]] = node };
        }

        return node.ToJsonString();
    }

    internal static string BuildWorkerInstanceKey(string appInstanceKey, HostRow host)
    {
        var builder = new StringBuilder();
        foreach (var ch in host.HostKey.Trim().ToLowerInvariant())
        {
            builder.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '_');
        }

        var hostPart = builder.ToString().Trim('_');
        var key = $"{appInstanceKey}_{hostPart}";
        if (hostPart.Length > 0 && key.Length <= WorkerInstanceKeyMaxLength)
        {
            return key;
        }

        // Too long (or no usable characters): the host id keeps the key unique and stable.
        var suffix = host.HostId.ToString("N")[..12];
        var prefixLength = Math.Min(appInstanceKey.Length, WorkerInstanceKeyMaxLength - suffix.Length - 1);
        return $"{appInstanceKey[..prefixLength]}_{suffix}";
    }

    private static ValidatedRequest ValidateRequest(WorkerAppInstanceProvisioningRequest request)
    {
        static string Required(string? value, string name, int maxLength)
        {
            var trimmed = value?.Trim() ?? "";
            if (trimmed.Length == 0)
            {
                throw Refuse(WorkerAppInstanceProvisioningError.InvalidRequest, $"{name} is required.");
            }

            if (trimmed.Length > maxLength)
            {
                throw Refuse(WorkerAppInstanceProvisioningError.InvalidRequest, $"{name} may be at most {maxLength} characters.");
            }

            return trimmed;
        }

        var moduleKey = Required(request.ModuleKey, nameof(request.ModuleKey), 100);
        var appKey = Required(request.AppKey, nameof(request.AppKey), 100);
        var appInstanceKey = Required(request.AppInstanceKey, nameof(request.AppInstanceKey), AppInstanceKeyMaxLength);
        if (!InstanceKeyPattern.IsMatch(appInstanceKey))
        {
            throw Refuse(
                WorkerAppInstanceProvisioningError.InvalidRequest,
                "AppInstanceKey may contain only letters, digits, '_', '.' and '-', and must start with a letter or digit.");
        }

        var displayName = Required(request.DisplayName, nameof(request.DisplayName), DisplayNameMaxLength);
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description is { Length: > 500 })
        {
            throw Refuse(WorkerAppInstanceProvisioningError.InvalidRequest, "Description may be at most 500 characters.");
        }

        var hostIds = request.HostIds ?? [];
        if (hostIds.Count == 0)
        {
            throw Refuse(WorkerAppInstanceProvisioningError.InvalidRequest, "Select at least one host.");
        }

        if (hostIds.Any(id => id == Guid.Empty) || hostIds.Distinct().Count() != hostIds.Count)
        {
            throw Refuse(WorkerAppInstanceProvisioningError.InvalidRequest, "HostIds must be distinct, non-empty host ids.");
        }

        var rpcPath = (request.HostAgentRpcConfigurationPath ?? "").Split(':');
        if (rpcPath.Length is < 1 or > 8 || rpcPath.Any(segment => !ConfigurationSegmentPattern.IsMatch(segment)))
        {
            throw Refuse(
                WorkerAppInstanceProvisioningError.InvalidRequest,
                "HostAgentRpcConfigurationPath must be a colon-separated configuration path such as 'MyModule:HostAgentRpc'.");
        }

        if (request.DefaultHostAgentRpcTimeoutSeconds < 1)
        {
            throw Refuse(WorkerAppInstanceProvisioningError.InvalidRequest, "DefaultHostAgentRpcTimeoutSeconds must be at least 1.");
        }

        return new ValidatedRequest(
            moduleKey,
            appKey,
            appInstanceKey,
            displayName,
            description,
            hostIds.ToList(),
            request.TemplateAppInstanceId,
            request.ModuleInstanceId,
            rpcPath,
            request.DefaultHostAgentRpcTimeoutSeconds,
            string.IsNullOrWhiteSpace(request.ModuleAdministrationPermission) ? null : request.ModuleAdministrationPermission.Trim());
    }

    private static async Task AcquireLockAsync(SqlConnection conn, SqlTransaction tx, ValidatedRequest input, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("sp_getapplock", conn, tx)
        {
            CommandType = CommandType.StoredProcedure
        };
        cmd.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value =
            $"omp.provision-worker-app-instance/{input.ModuleKey}/{input.AppInstanceKey}".ToLowerInvariant();
        cmd.Parameters.Add("@LockMode", SqlDbType.NVarChar, 32).Value = "Exclusive";
        cmd.Parameters.Add("@LockOwner", SqlDbType.NVarChar, 32).Value = "Transaction";
        cmd.Parameters.Add("@LockTimeout", SqlDbType.Int).Value = LockTimeoutMilliseconds;
        var returnValue = cmd.Parameters.Add("@ReturnValue", SqlDbType.Int);
        returnValue.Direction = ParameterDirection.ReturnValue;
        await cmd.ExecuteNonQueryAsync(ct);

        // sp_getapplock: 0/1 granted, -1 timeout, -2 cancelled, -3 deadlock victim, -999 error.
        switch ((int)returnValue.Value)
        {
            case >= 0:
                return;
            case -1:
                throw Refuse(
                    WorkerAppInstanceProvisioningError.Busy,
                    $"Another provisioning call for app instance '{input.AppInstanceKey}' is still running. Try again.");
            case -3:
                throw Refuse(
                    WorkerAppInstanceProvisioningError.LockFailed,
                    $"The provisioning lock for app instance '{input.AppInstanceKey}' was refused because SQL Server chose this call as a deadlock victim. Nothing was created; try again.");
            case var code:
                throw Refuse(
                    WorkerAppInstanceProvisioningError.LockFailed,
                    $"The provisioning lock for app instance '{input.AppInstanceKey}' could not be taken (sp_getapplock returned {code}: {(code == -2 ? "the request was cancelled" : "parameter or call error")}). Nothing was created.");
        }
    }

    private static async Task<AppRow> ReadAppAsync(SqlConnection conn, SqlTransaction tx, ValidatedRequest input, CancellationToken ct)
    {
        const string sql = @"
SELECT m.ModuleId,
       a.AppId,
       a.AppType,
       CAST(CASE WHEN m.IsEnabled = 1 AND a.IsEnabled = 1 THEN 1 ELSE 0 END AS bit),
       omp.IsArtifactPackageCompatibleWithAppType(N'worker', a.AppType)
FROM omp.Apps a
INNER JOIN omp.Modules m ON m.ModuleId = a.ModuleId
WHERE m.ModuleKey = @moduleKey
  AND a.AppKey = @appKey;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@moduleKey", SqlDbType.NVarChar, 100).Value = input.ModuleKey;
        cmd.Parameters.Add("@appKey", SqlDbType.NVarChar, 100).Value = input.AppKey;
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct) || !rdr.GetBoolean(3))
        {
            throw Refuse(
                WorkerAppInstanceProvisioningError.AppNotFound,
                $"App '{input.AppKey}' of module '{input.ModuleKey}' does not exist or is disabled.");
        }

        if (!rdr.GetBoolean(4))
        {
            throw Refuse(
                WorkerAppInstanceProvisioningError.NotAWorkerApp,
                $"App '{input.AppKey}' is of type '{rdr.GetString(2)}', not a worker app.");
        }

        return new AppRow(rdr.GetInt32(0), rdr.GetInt32(1), input.AppKey);
    }

    private static async Task AuthorizeAsync(
        SqlConnection conn,
        SqlTransaction tx,
        ValidatedRequest input,
        AppRow app,
        IReadOnlySet<string> permissions,
        CancellationToken ct)
    {
        if (permissions.Contains(PortalAdminNavigation.AdminPermission))
        {
            return;
        }

        if (input.ModuleAdministrationPermission is { } modulePermission && permissions.Contains(modulePermission))
        {
            const string sql = @"
SELECT CAST(CASE WHEN EXISTS
(
    SELECT 1
    FROM omp.AppPermissions ap
    INNER JOIN omp.Apps a ON a.AppId = ap.AppId
    INNER JOIN omp.Permissions p ON p.PermissionId = ap.PermissionId
    WHERE a.ModuleId = @moduleId
      AND a.IsEnabled = 1
      AND p.Name = @permission
) THEN 1 ELSE 0 END AS bit);";

            await using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.Add("@moduleId", SqlDbType.Int).Value = app.ModuleId;
            cmd.Parameters.Add("@permission", SqlDbType.NVarChar, 200).Value = modulePermission;
            if ((bool)(await cmd.ExecuteScalarAsync(ct))!)
            {
                return;
            }
        }

        throw Refuse(
            WorkerAppInstanceProvisioningError.Unauthorized,
            $"The caller may not provision app instances of module '{input.ModuleKey}'.");
    }

    private static async Task<AppInstanceRow> ReadTemplateAsync(
        SqlConnection conn,
        SqlTransaction tx,
        Guid templateId,
        AppRow app,
        CancellationToken ct)
    {
        var template = await ReadAppInstanceAsync(conn, tx, "ai.AppInstanceId = @appInstanceId", cmd =>
            cmd.Parameters.Add("@appInstanceId", SqlDbType.UniqueIdentifier).Value = templateId, ct);
        if (template is null || template.AppId != app.AppId)
        {
            throw Refuse(
                WorkerAppInstanceProvisioningError.TemplateMismatch,
                $"Template app instance '{templateId}' does not exist or is not an instance of app '{app.AppKey}'.");
        }

        return template;
    }

    private static Task<AppInstanceRow?> ReadExistingAppInstanceAsync(
        SqlConnection conn,
        SqlTransaction tx,
        Guid moduleInstanceId,
        string appInstanceKey,
        CancellationToken ct)
        => ReadAppInstanceAsync(conn, tx, "ai.ModuleInstanceId = @moduleInstanceId AND ai.AppInstanceKey = @appInstanceKey", cmd =>
        {
            cmd.Parameters.Add("@moduleInstanceId", SqlDbType.UniqueIdentifier).Value = moduleInstanceId;
            cmd.Parameters.Add("@appInstanceKey", SqlDbType.NVarChar, AppInstanceKeyMaxLength).Value = appInstanceKey;
        }, ct);

    private static async Task<AppInstanceRow?> ReadAppInstanceAsync(
        SqlConnection conn,
        SqlTransaction tx,
        string predicate,
        Action<SqlCommand> bind,
        CancellationToken ct)
    {
        var sql = $@"
SELECT ai.AppInstanceId,
       ai.ModuleInstanceId,
       ai.AppId,
       ai.HostId,
       ai.TargetHostTemplateId,
       ai.InstallationName,
       ai.ArtifactId
FROM omp.AppInstances ai WITH (UPDLOCK, HOLDLOCK)
WHERE {predicate};";

        await using var cmd = new SqlCommand(sql, conn, tx);
        bind(cmd);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new AppInstanceRow(
            rdr.GetGuid(0),
            rdr.GetGuid(1),
            rdr.GetInt32(2),
            rdr.IsDBNull(3) ? null : rdr.GetGuid(3),
            rdr.IsDBNull(4) ? null : rdr.GetInt32(4),
            rdr.IsDBNull(5) ? null : rdr.GetString(5),
            rdr.IsDBNull(6) ? null : rdr.GetInt32(6));
    }

    private static async Task<ModuleInstanceRow> ResolveModuleInstanceAsync(
        SqlConnection conn,
        SqlTransaction tx,
        ValidatedRequest input,
        AppRow app,
        AppInstanceRow? template,
        CancellationToken ct)
    {
        var requested = template?.ModuleInstanceId ?? input.ModuleInstanceId;
        if (template is not null && input.ModuleInstanceId is Guid explicitId && explicitId != template.ModuleInstanceId)
        {
            throw Refuse(
                WorkerAppInstanceProvisioningError.TemplateMismatch,
                "The template app instance belongs to a different module instance than the requested one.");
        }

        const string sql = @"
SELECT mi.ModuleInstanceId, mi.InstanceId
FROM omp.ModuleInstances mi
INNER JOIN omp.Instances i ON i.InstanceId = mi.InstanceId
WHERE mi.ModuleId = @moduleId
  AND mi.IsEnabled = 1
  AND i.IsEnabled = 1
  AND (@moduleInstanceId IS NULL OR mi.ModuleInstanceId = @moduleInstanceId);";

        var rows = new List<ModuleInstanceRow>();
        await using (var cmd = new SqlCommand(sql, conn, tx))
        {
            cmd.Parameters.Add("@moduleId", SqlDbType.Int).Value = app.ModuleId;
            cmd.Parameters.Add("@moduleInstanceId", SqlDbType.UniqueIdentifier).Value = requested.HasValue ? requested.Value : DBNull.Value;
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                rows.Add(new ModuleInstanceRow(rdr.GetGuid(0), rdr.GetGuid(1)));
            }
        }

        return rows.Count switch
        {
            1 => rows[0],
            0 => throw Refuse(
                WorkerAppInstanceProvisioningError.ModuleInstanceUnresolved,
                requested.HasValue
                    ? $"Module instance '{requested}' is not an enabled instance of module '{input.ModuleKey}'."
                    : $"Module '{input.ModuleKey}' has no enabled module instance."),
            _ => throw Refuse(
                WorkerAppInstanceProvisioningError.ModuleInstanceUnresolved,
                $"Module '{input.ModuleKey}' has several enabled module instances. Pass ModuleInstanceId or a template app instance.")
        };
    }

    private static async Task<string?> ResolveInstallationNameAsync(
        SqlConnection conn,
        SqlTransaction tx,
        Guid moduleInstanceId,
        AppRow app,
        CancellationToken ct)
    {
        const string sql = @"
SELECT DISTINCT ai.InstallationName
FROM omp.AppInstances ai
WHERE ai.ModuleInstanceId = @moduleInstanceId
  AND ai.AppId = @appId
  AND NULLIF(LTRIM(RTRIM(ai.InstallationName)), N'') IS NOT NULL;";

        var names = new List<string>();
        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@moduleInstanceId", SqlDbType.UniqueIdentifier).Value = moduleInstanceId;
        cmd.Parameters.Add("@appId", SqlDbType.Int).Value = app.AppId;
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            names.Add(rdr.GetString(0));
        }

        return names.Count switch
        {
            0 => null,
            1 => names[0],
            _ => throw Refuse(
                WorkerAppInstanceProvisioningError.InstallationNameUnresolved,
                $"Existing instances of app '{app.AppKey}' use different installation names ({string.Join(", ", names)}). Pass a template app instance.")
        };
    }

    private static async Task<int> RequireCompatibleTemplateArtifactAsync(
        SqlConnection conn,
        SqlTransaction tx,
        AppRow app,
        AppInstanceRow template,
        CancellationToken ct)
    {
        if (template.ArtifactId is not int artifactId)
        {
            throw Refuse(
                WorkerAppInstanceProvisioningError.IncompatibleArtifact,
                $"Template app instance '{template.AppInstanceId}' has no artifact to copy.");
        }

        var candidates = await ReadCompatibleArtifactsAsync(conn, tx, app, artifactId, ct);
        if (candidates.Count == 0)
        {
            throw Refuse(
                WorkerAppInstanceProvisioningError.IncompatibleArtifact,
                $"Artifact {artifactId} of the template app instance is disabled or not compatible with app '{app.AppKey}' and the applied module definition.");
        }

        return artifactId;
    }

    private static async Task<int> ResolveLatestCompatibleArtifactAsync(
        SqlConnection conn,
        SqlTransaction tx,
        AppRow app,
        CancellationToken ct)
    {
        var candidates = await ReadCompatibleArtifactsAsync(conn, tx, app, null, ct);
        if (candidates.Count == 0)
        {
            throw Refuse(
                WorkerAppInstanceProvisioningError.NoCompatibleArtifact,
                $"App '{app.AppKey}' has no enabled worker artifact that the applied module definition accepts. Import the worker artifact first.");
        }

        return candidates
            .OrderByDescending(candidate => candidate.Version, VersionComparer.Instance)
            .ThenByDescending(candidate => candidate.ArtifactId)
            .First()
            .ArtifactId;
    }

    /// <summary>
    /// Enabled runtime artifacts of the app whose package type fits the app type and, when the
    /// module has an applied module definition, that fall inside one of its compatibility slots.
    /// </summary>
    private static async Task<List<ArtifactRow>> ReadCompatibleArtifactsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        AppRow app,
        int? onlyArtifactId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT ar.ArtifactId, ar.Version, ar.PackageType, ar.TargetName
FROM omp.Artifacts ar
INNER JOIN omp.Apps a ON a.AppId = ar.AppId
WHERE ar.AppId = @appId
  AND ar.IsEnabled = 1
  AND ar.PackageType <> N'channel-type'
  AND omp.IsArtifactPackageCompatibleWithAppType(ar.PackageType, a.AppType) = 1
  AND (@artifactId IS NULL OR ar.ArtifactId = @artifactId);

SELECT TOP (1) d.ModuleDefinitionDocumentId
FROM omp.ModuleDefinitionDocuments d
INNER JOIN omp.Modules m ON m.ModuleKey = d.ModuleKey
WHERE m.ModuleId = @moduleId
  AND d.IsApplied = 1
ORDER BY d.AppliedUtc DESC, d.UpdatedUtc DESC, d.ModuleDefinitionDocumentId DESC;";

        var artifacts = new List<ArtifactRow>();
        int? definitionId = null;
        await using (var cmd = new SqlCommand(sql, conn, tx))
        {
            cmd.Parameters.Add("@appId", SqlDbType.Int).Value = app.AppId;
            cmd.Parameters.Add("@moduleId", SqlDbType.Int).Value = app.ModuleId;
            cmd.Parameters.Add("@artifactId", SqlDbType.Int).Value = onlyArtifactId.HasValue ? onlyArtifactId.Value : DBNull.Value;
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                artifacts.Add(new ArtifactRow(
                    rdr.GetInt32(0),
                    rdr.GetString(1),
                    rdr.GetString(2),
                    rdr.IsDBNull(3) ? null : rdr.GetString(3)));
            }

            await rdr.NextResultAsync(ct);
            if (await rdr.ReadAsync(ct))
            {
                definitionId = rdr.GetInt32(0);
            }
        }

        if (definitionId is null || artifacts.Count == 0)
        {
            return artifacts;
        }

        const string slotSql = @"
SELECT PackageType, TargetName, MinArtifactVersion, MaxArtifactVersion
FROM omp.ModuleDefinitionArtifactCompatibility
WHERE ModuleDefinitionDocumentId = @definitionId
  AND AppKey = @appKey;";

        var slots = new List<(string PackageType, string? TargetName, string? Min, string? Max)>();
        await using (var cmd = new SqlCommand(slotSql, conn, tx))
        {
            cmd.Parameters.Add("@definitionId", SqlDbType.Int).Value = definitionId.Value;
            cmd.Parameters.Add("@appKey", SqlDbType.NVarChar, 100).Value = app.AppKey;
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                slots.Add((
                    rdr.GetString(0),
                    rdr.IsDBNull(1) ? null : rdr.GetString(1),
                    rdr.IsDBNull(2) ? null : rdr.GetString(2),
                    rdr.IsDBNull(3) ? null : rdr.GetString(3)));
            }
        }

        return artifacts
            .Where(artifact => slots.Any(slot =>
                string.Equals(slot.PackageType, artifact.PackageType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(slot.TargetName ?? "", artifact.TargetName ?? "", StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(slot.Min) || VersionComparer.Instance.Compare(artifact.Version, slot.Min) >= 0)
                && (string.IsNullOrWhiteSpace(slot.Max) || VersionComparer.Instance.Compare(artifact.Version, slot.Max) <= 0)))
            .ToList();
    }

    private static async Task<List<HostRow>> ReadHostsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        ValidatedRequest input,
        Guid instanceId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT h.HostId, h.InstanceId, h.HostKey, h.DisplayName, h.IsEnabled
FROM omp.Hosts h
WHERE h.HostId IN (SELECT CAST([value] AS uniqueidentifier) FROM OPENJSON(@hostIds));";

        var found = new Dictionary<Guid, (Guid InstanceId, string HostKey, string? DisplayName, bool IsEnabled)>();
        await using (var cmd = new SqlCommand(sql, conn, tx))
        {
            cmd.Parameters.Add("@hostIds", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(input.HostIds);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                found[rdr.GetGuid(0)] = (rdr.GetGuid(1), rdr.GetString(2), rdr.IsDBNull(3) ? null : rdr.GetString(3), rdr.GetBoolean(4));
            }
        }

        var hosts = new List<HostRow>();
        foreach (var hostId in input.HostIds)
        {
            if (!found.TryGetValue(hostId, out var host))
            {
                throw Refuse(WorkerAppInstanceProvisioningError.HostNotFound, $"Host '{hostId}' does not exist.");
            }

            if (!host.IsEnabled)
            {
                throw Refuse(WorkerAppInstanceProvisioningError.HostDisabled, $"Host '{host.HostKey}' is disabled.");
            }

            if (host.InstanceId != instanceId)
            {
                throw Refuse(
                    WorkerAppInstanceProvisioningError.HostOutsideInstallation,
                    $"Host '{host.HostKey}' belongs to another installation than the module instance.");
            }

            hosts.Add(new HostRow(hostId, host.HostKey, host.DisplayName));
        }

        return hosts;
    }

    private static async Task<List<WorkerRow>> ReadWorkersAsync(
        SqlConnection conn,
        SqlTransaction tx,
        Guid appInstanceId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT wi.WorkerInstanceId, wi.HostId, wi.WorkerInstanceKey
FROM omp.WorkerInstances wi WITH (UPDLOCK, HOLDLOCK)
WHERE wi.AppInstanceId = @appInstanceId;";

        var workers = new List<WorkerRow>();
        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@appInstanceId", SqlDbType.UniqueIdentifier).Value = appInstanceId;
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            workers.Add(new WorkerRow(rdr.GetGuid(0), rdr.IsDBNull(1) ? null : rdr.GetGuid(1), rdr.GetString(2)));
        }

        return workers;
    }

    private static async Task InsertAppInstanceAsync(
        SqlConnection conn,
        SqlTransaction tx,
        Guid appInstanceId,
        Guid moduleInstanceId,
        int appId,
        ValidatedRequest input,
        string? installationName,
        int? artifactId,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO omp.AppInstances
(
    AppInstanceId, ModuleInstanceId, HostId, TargetHostTemplateId, AppId, AppInstanceKey,
    DisplayName, Description, InstallationName, ArtifactId, IsEnabled, IsAllowed, DesiredState, SortOrder
)
VALUES
(
    @appInstanceId, @moduleInstanceId, NULL, NULL, @appId, @appInstanceKey,
    @displayName, @description, @installationName, @artifactId, 1, 1, 1, 0
);";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@appInstanceId", SqlDbType.UniqueIdentifier).Value = appInstanceId;
        cmd.Parameters.Add("@moduleInstanceId", SqlDbType.UniqueIdentifier).Value = moduleInstanceId;
        cmd.Parameters.Add("@appId", SqlDbType.Int).Value = appId;
        cmd.Parameters.Add("@appInstanceKey", SqlDbType.NVarChar, AppInstanceKeyMaxLength).Value = input.AppInstanceKey;
        cmd.Parameters.Add("@displayName", SqlDbType.NVarChar, DisplayNameMaxLength).Value = input.DisplayName;
        cmd.Parameters.Add("@description", SqlDbType.NVarChar, 500).Value = (object?)input.Description ?? DBNull.Value;
        cmd.Parameters.Add("@installationName", SqlDbType.NVarChar, 150).Value = (object?)installationName ?? DBNull.Value;
        cmd.Parameters.Add("@artifactId", SqlDbType.Int).Value = artifactId.HasValue ? artifactId.Value : DBNull.Value;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertWorkerAsync(
        SqlConnection conn,
        SqlTransaction tx,
        Guid appInstanceId,
        int? artifactId,
        string appDisplayName,
        WorkerPlan plan,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO omp.WorkerInstances
(
    WorkerInstanceId, AppInstanceId, HostId, ArtifactId, WorkerInstanceKey, DisplayName,
    ConfigurationJson, IsEnabled, IsAllowed, DesiredState, SortOrder
)
VALUES
(
    @workerInstanceId, @appInstanceId, @hostId, @artifactId, @workerInstanceKey, @displayName,
    @configurationJson, 1, 1, 1, 0
);";

        var displayName = $"{appDisplayName} {plan.Host.DisplayName ?? plan.Host.HostKey}";
        if (displayName.Length > DisplayNameMaxLength)
        {
            displayName = displayName[..DisplayNameMaxLength];
        }

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@workerInstanceId", SqlDbType.UniqueIdentifier).Value = plan.WorkerInstanceId;
        cmd.Parameters.Add("@appInstanceId", SqlDbType.UniqueIdentifier).Value = appInstanceId;
        cmd.Parameters.Add("@hostId", SqlDbType.UniqueIdentifier).Value = plan.Host.HostId;
        cmd.Parameters.Add("@artifactId", SqlDbType.Int).Value = artifactId.HasValue ? artifactId.Value : DBNull.Value;
        cmd.Parameters.Add("@workerInstanceKey", SqlDbType.NVarChar, WorkerInstanceKeyMaxLength).Value = plan.WorkerInstanceKey;
        cmd.Parameters.Add("@displayName", SqlDbType.NVarChar, DisplayNameMaxLength).Value = displayName;
        cmd.Parameters.Add("@configurationJson", SqlDbType.NVarChar, -1).Value = plan.ConfigurationJson!;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static WorkerAppInstanceProvisioningException Refuse(WorkerAppInstanceProvisioningError error, string message)
        => new(error, message);

    internal sealed record HostRow(Guid HostId, string HostKey, string? DisplayName);

    internal sealed record HostAgentRpcEndpoint(string PipeName, int TimeoutSeconds);

    private sealed record RecordedRpc(bool Enabled, string? PipeName, int? TimeoutSeconds);

    private sealed record HostAgentRegistration(
        string ServiceName,
        string? Version,
        string? PipeName,
        int? RefreshSeconds,
        long? AgeSeconds)
    {
        public bool HasPipe => !string.IsNullOrWhiteSpace(PipeName);

        public long MaxAgeSeconds => GetRegistrationMaxAgeSeconds(RefreshSeconds);

        // A LastSeenUtc slightly in the future (negative age) still counts as fresh.
        public bool IsFresh => AgeSeconds is long age && age <= MaxAgeSeconds;
    }

    private sealed record ValidatedRequest(
        string ModuleKey,
        string AppKey,
        string AppInstanceKey,
        string DisplayName,
        string? Description,
        IReadOnlyList<Guid> HostIds,
        Guid? TemplateAppInstanceId,
        Guid? ModuleInstanceId,
        IReadOnlyList<string> RpcPath,
        int DefaultRpcTimeoutSeconds,
        string? ModuleAdministrationPermission);

    private sealed record AppRow(int ModuleId, int AppId, string AppKey);

    private sealed record ModuleInstanceRow(Guid ModuleInstanceId, Guid InstanceId);

    private sealed record AppInstanceRow(
        Guid AppInstanceId,
        Guid ModuleInstanceId,
        int AppId,
        Guid? HostId,
        int? TargetHostTemplateId,
        string? InstallationName,
        int? ArtifactId);

    private sealed record ArtifactRow(int ArtifactId, string Version, string PackageType, string? TargetName);

    private sealed record WorkerRow(Guid WorkerInstanceId, Guid? HostId, string WorkerInstanceKey);

    private sealed record WorkerPlan(
        HostRow Host,
        Guid WorkerInstanceId,
        string WorkerInstanceKey,
        HostAgentRpcEndpoint? Rpc,
        string? ConfigurationJson);

    /// <summary>
    /// Orders artifact versions numerically (prerelease and build suffixes ignored), falling
    /// back to ordinal text; the same rule as OpenModulePlatform.Artifacts.ArtifactVersionComparer,
    /// which this library does not reference.
    /// </summary>
    private sealed class VersionComparer : IComparer<string>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (TryParse(x, out var left) && TryParse(y, out var right))
            {
                return left.CompareTo(right);
            }

            return string.Compare(x?.Trim(), y?.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParse(string? value, out Version version)
        {
            var text = value?.Trim() ?? "";
            var suffix = text.IndexOfAny(['-', '+']);
            if (suffix >= 0)
            {
                text = text[..suffix];
            }

            return Version.TryParse(text, out version!);
        }
    }
}

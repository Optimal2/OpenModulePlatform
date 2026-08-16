// File: OpenModulePlatform.Web.Shared/Telemetry/OmpPerformanceTelemetryMiddleware.cs
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace OpenModulePlatform.Web.Shared.Telemetry;

/// <summary>
/// Times every request and records the result for later analysis.
/// </summary>
/// <remarks>
/// Registered next to the correlation middleware so it wraps the whole pipeline, including
/// error handling -- a page that fails slowly is exactly the one worth knowing about.
///
/// It records four things per request: total duration, an outcome counter by status class,
/// and (when a page has reported them) the topbar's own duration and database round-trip
/// count. The last two exist because the standing open question is whether the topbar's
/// per-request database work is worth optimising, and that cannot be answered from the
/// total alone.
/// </remarks>
public static class OmpPerformanceTelemetryMiddleware
{
    /// <summary>HttpContext.Items key a page sets to report how long its topbar build took.</summary>
    public const string TopBarDurationItemKey = "OmpTopBarDurationMs";

    /// <summary>HttpContext.Items key a page sets to report its topbar database round trips.</summary>
    public const string TopBarDbCallsItemKey = "OmpTopBarDbCalls";

    public const string MetricRequestDuration = "request.duration.ms";
    public const string MetricRequestCount = "request.count";
    public const string MetricTopBarDuration = "topbar.duration.ms";
    public const string MetricTopBarDbCalls = "topbar.db.calls";

    /// <summary>
    /// The application key this process reports under, published by the call that installs
    /// the middleware.
    /// </summary>
    /// <remarks>
    /// A process hosts one OMP web application, so this is a constant for its lifetime. It is
    /// published here because work that happens outside the request pipeline still has to
    /// report under the same key -- a Blazor circuit rebuilding the topbar has no HttpContext
    /// to hand a measurement to, and deriving the key a second time somewhere else is exactly
    /// how two truths for one value start (R12-E4).
    /// </remarks>
    public static string ConfiguredAppKey { get; private set; } = "unknown";

    public static IApplicationBuilder UseOmpPerformanceTelemetry(this IApplicationBuilder app, string appKey)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!string.IsNullOrWhiteSpace(appKey))
        {
            ConfiguredAppKey = appKey;
        }

        return app.Use(async (context, next) =>
        {
            var telemetry = context.RequestServices.GetService<OmpPerformanceTelemetry>();
            var options = context.RequestServices.GetService<OmpPerformanceTelemetryOptions>();

            if (telemetry is null || options is null || !options.Enabled)
            {
                await next();
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                await next();
            }
            finally
            {
                stopwatch.Stop();

                // Telemetry must never turn a served page into a failed one. Anything that
                // goes wrong while recording is swallowed here, deliberately and narrowly:
                // the request has already been handled by the time this runs.
                //
                // Swallowed, but not silent. The first version caught everything and did
                // nothing at all, which would have hidden a defect in RecordRequest itself
                // for as long as it existed -- and the whole point of this middleware is to
                // make things visible. It is reported once per process: enough to find,
                // not enough to flood a log on every request.
                try
                {
                    RecordRequest(context, telemetry, options, appKey, stopwatch.Elapsed.TotalMilliseconds);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    ReportRecordingFailureOnce(context, ex);
                }
            }
        });
    }

    /// <summary>Set once the first recording failure has been logged.</summary>
    private static int _recordingFailureReported;

    /// <summary>
    /// Logs the first recording failure this process sees, and nothing after it.
    /// </summary>
    /// <remarks>
    /// Reporting must not become its own failure, so the logger lookup is inside the try:
    /// during shutdown the request services can already be disposed, and throwing from a
    /// catch block would turn a swallowed telemetry problem into a failed request -- the
    /// exact outcome this middleware is written to avoid.
    /// </remarks>
    private static void ReportRecordingFailureOnce(HttpContext context, Exception exception)
    {
        if (Interlocked.Exchange(ref _recordingFailureReported, 1) != 0)
        {
            return;
        }

        try
        {
            context.RequestServices
                .GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(OmpPerformanceTelemetryMiddleware))
                .LogWarning(
                    exception,
                    "Performance telemetry could not record a request. Further occurrences are not logged. Measurements are incomplete until this is fixed.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Nothing left to try. The request itself has already been served.
        }
    }

    private static void RecordRequest(
        HttpContext context,
        OmpPerformanceTelemetry telemetry,
        OmpPerformanceTelemetryOptions options,
        string appKey,
        double durationMs)
    {
        var scope = ResolveScope(context);
        var statusClass = context.Response.StatusCode / 100;

        telemetry.Record(appKey, scope, $"{MetricRequestCount}.{statusClass}xx", 1);

        // Long-lived connections are counted but never timed. A SignalR long poll is held
        // open on purpose and measured almost seven seconds on the very first verification
        // run -- recorded as a duration it would dominate both the mean and the maximum and
        // make the whole metric read as "the application is slow" when nothing is.
        if (durationMs >= options.MinimumDurationMsToRecord && !IsLongLivedConnection(context))
        {
            telemetry.Record(appKey, scope, MetricRequestDuration, durationMs);
        }

        if (context.Items.TryGetValue(TopBarDurationItemKey, out var topBarDuration)
            && topBarDuration is double topBarMs)
        {
            telemetry.Record(appKey, scope, MetricTopBarDuration, topBarMs);
        }

        if (context.Items.TryGetValue(TopBarDbCallsItemKey, out var topBarCalls)
            && topBarCalls is int calls)
        {
            telemetry.Record(appKey, scope, MetricTopBarDbCalls, calls);
        }
    }

    /// <summary>
    /// True for connections whose duration measures how long a client stayed connected
    /// rather than how long the server took.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WebSockets and the SignalR long-poll and server-sent-event transports are all held
    /// open deliberately. Their duration is a property of the client's session, not of any
    /// work the server did, so it belongs in no latency figure. They are still counted, so
    /// the traffic remains visible.
    /// </para>
    /// <para>
    /// R12-A11 and R12-E8, which are the two directions of the same mistake. The test was
    /// <c>path.Contains("/updates")</c>: too wide, because any module page under a path
    /// containing that segment -- an update list, an "/artifacts/updates" view -- silently
    /// stopped contributing to the latency figure, and nothing said so; and at the same time
    /// too narrow, because this platform mounts <em>two</em> hubs and only one of them has
    /// "/updates" in its path, so every long poll on the push-event hub was timed as if it
    /// were a slow page. The hub paths are now taken from the constants the hubs are mapped
    /// with, so adding a hub cannot leave this behind again.
    /// </para>
    /// </remarks>
    private static bool IsLongLivedConnection(HttpContext context)
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            return true;
        }

        var path = context.Request.Path.Value;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return IsHubPath(path, Notifications.TopBarNotificationHub.Path)
            || IsHubPath(path, Notifications.TopBarNotificationHub.PushEventPath)
            // A negotiate always belongs to SignalR, including hubs a consuming application
            // mounts on paths this assembly does not know.
            || path.EndsWith("/negotiate", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the request path is a hub path or one of the transport suffixes SignalR
    /// appends to it -- and false for a page that merely starts with the same characters.
    /// </summary>
    private static bool IsHubPath(string path, string hubPath)
        => path.Equals(hubPath, StringComparison.OrdinalIgnoreCase)
            || (path.Length > hubPath.Length
                && path.StartsWith(hubPath, StringComparison.OrdinalIgnoreCase)
                && path[hubPath.Length] == '/');

    /// <summary>
    /// Reduces a request to a coarse, bounded label.
    /// </summary>
    /// <remarks>
    /// Never the raw path. A raw path carries record ids, user ids and search terms, which
    /// would put identifying data into an operational table and give the metric key space
    /// no ceiling at all -- one key per document id. The Razor Pages route is already a
    /// template ("/Admin/Artifacts"), so it is used when available; otherwise the first
    /// path segment is enough to tell "the portal is slow" from "one module is slow".
    /// </remarks>
    private static string ResolveScope(HttpContext context)
    {
        if (context.GetEndpoint() is Microsoft.AspNetCore.Routing.RouteEndpoint routeEndpoint)
        {
            var pattern = routeEndpoint.RoutePattern.RawText;
            if (!string.IsNullOrWhiteSpace(pattern))
            {
                return pattern;
            }
        }

        var path = context.Request.Path.Value;
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return "/";
        }

        var firstSegmentEnd = path.IndexOf('/', 1);
        return firstSegmentEnd < 0 ? path : path[..firstSegmentEnd];
    }
}

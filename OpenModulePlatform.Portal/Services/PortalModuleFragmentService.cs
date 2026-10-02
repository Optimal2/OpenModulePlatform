// File: OpenModulePlatform.Portal/Services/PortalModuleFragmentService.cs
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Options;
using OpenModulePlatform.Web.Shared.Localization;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;
using OpenModulePlatform.Web.Shared.Web;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;

namespace OpenModulePlatform.Portal.Services;

/// <summary>
/// Loads <c>module-fragment</c> dashboard widgets: resolves the module's registered web
/// app, fetches the fragment server-side as the current user, sanitizes it and caches
/// the result per user, active role, culture and widget.
/// </summary>
/// <remarks>
/// Identity is forwarded the way every OMP web app already authenticates: the shared
/// OMP cookie (plus the active-role and culture cookies) is copied from the incoming
/// request onto the server-side request, and the module validates it exactly as it
/// would for the browser. No other cookie or header of the user is forwarded.
///
/// The request goes to <see cref="ModuleFragmentWidgetOptions.InternalBaseUrl"/> when
/// configured, to the module's registered absolute address when it has one, and
/// for HTTPS to the operator-configured absolute HTTPS Portal base URL when available.
/// GetPublicBaseUrl() is NOT a trusted configured origin: it includes the incoming
/// Host header. Using it as a network destination would allow Host injection/SSRF and
/// disclose the forwarded auth cookie. Without a configured origin, keep the network
/// destination on localhost; the Host header selects the local virtual host and .NET
/// also uses it for TLS SNI/certificate name validation. It never selects a remote host.
/// Certificate validation is never bypassed. TLS-terminating proxies or bindings that
/// are not reachable locally require an operator-configured origin or InternalBaseUrl.
/// </remarks>
public sealed class PortalModuleFragmentService
{
    public const string HttpClientName = "OmpPortal.ModuleFragment";
    public const string FragmentRequestHeader = "X-OMP-Dashboard-Fragment";

    private const string CacheKeyPrefix = "omp:portal:module-fragment:";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<ModuleFragmentWidgetOptions> _options;
    private readonly IOptions<OmpAuthOptions> _authOptions;
    private readonly IOptions<WebAppOptions> _webAppOptions;
    private readonly ModuleFragmentEndpointDiagnostics _endpointDiagnostics;
    private readonly ILogger<PortalModuleFragmentService> _logger;

    public PortalModuleFragmentService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IOptionsMonitor<ModuleFragmentWidgetOptions> options,
        IOptions<OmpAuthOptions> authOptions,
        IOptions<WebAppOptions> webAppOptions,
        ModuleFragmentEndpointDiagnostics endpointDiagnostics,
        ILogger<PortalModuleFragmentService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _options = options;
        _authOptions = authOptions;
        _webAppOptions = webAppOptions;
        _endpointDiagnostics = endpointDiagnostics;
        _logger = logger;
    }

    /// <summary>
    /// Loads the fragment for one widget. Returns <see cref="ModuleFragmentResult.Unavailable"/>
    /// without any request when the widget is not in <paramref name="accessibleWidgetIds"/>
    /// or its module web app is not in <paramref name="accessibleApps"/>.
    /// <paramref name="fresh"/> asks the module even when a result is cached (a dashboard
    /// refresh wants what the module shows now); the result renews the cache. A cached
    /// timeout still holds, so a slow module is shielded all the same.
    /// </summary>
    public async Task<ModuleFragmentResult> GetFragmentAsync(
        HttpContext httpContext,
        int widgetId,
        string? payload,
        IReadOnlySet<int> accessibleWidgetIds,
        IReadOnlyList<PortalAppEntry> accessibleApps,
        CancellationToken ct,
        bool fresh = false)
    {
        var results = await GetFragmentsAsync(
            httpContext,
            [new ModuleFragmentWidgetRequest(widgetId, payload)],
            accessibleWidgetIds,
            accessibleApps,
            ct,
            fresh);
        return results[widgetId];
    }

    /// <summary>
    /// Loads several fragments. Everything that reads <paramref name="httpContext"/> runs
    /// sequentially; only the module requests themselves run in parallel, each bounded by
    /// the configured timeout. <paramref name="fresh"/> is as for
    /// <see cref="GetFragmentAsync"/>.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, ModuleFragmentResult>> GetFragmentsAsync(
        HttpContext httpContext,
        IReadOnlyList<ModuleFragmentWidgetRequest> widgets,
        IReadOnlySet<int> accessibleWidgetIds,
        IReadOnlyList<PortalAppEntry> accessibleApps,
        CancellationToken ct,
        bool fresh = false)
    {
        var options = _options.CurrentValue;
        var cacheDuration = options.GetCacheDuration();
        var timeoutCacheDuration = options.GetTimeoutCacheDuration();
        var results = new Dictionary<int, ModuleFragmentResult>();
        var pending = new List<PreparedFetch>();
        string? cookieHeader = null;
        string? correlationId = null;

        foreach (var widget in widgets)
        {
            if (results.ContainsKey(widget.WidgetId)
                || pending.Any(item => item.WidgetId == widget.WidgetId))
            {
                continue;
            }

            if (!accessibleWidgetIds.Contains(widget.WidgetId))
            {
                results[widget.WidgetId] = ModuleFragmentResult.Unavailable;
                continue;
            }

            if (ModuleFragmentWidget.TryParsePayload(widget.Payload) is not { } config)
            {
                DashboardWidgetPayloadDiagnostics.WarnOnce(_logger, widget.WidgetId, ModuleFragmentWidget.WidgetType, widget.Payload);
                results[widget.WidgetId] = ModuleFragmentResult.Unavailable;
                continue;
            }

            DashboardWidgetPayloadDiagnostics.Forget(widget.WidgetId);

            var app = accessibleApps
                .Where(item => string.Equals(item.AppKey, config.AppKey, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            var moduleBaseHref = app is null ? null : AppLinkBuilder.ResolveHref(httpContext.Request, app);
            if (string.IsNullOrWhiteSpace(moduleBaseHref))
            {
                results[widget.WidgetId] = ModuleFragmentResult.Unavailable;
                continue;
            }

            var cacheKey = BuildCacheKey(httpContext, widget.WidgetId, widget.Payload);
            if (_cache.TryGetValue(cacheKey, out ModuleFragmentResult? cached)
                && cached is not null
                && (cached.FailureReason == ModuleFragmentFailureReason.Timeout
                    ? timeoutCacheDuration : cacheDuration) > TimeSpan.Zero
                && !(fresh && cached.FailureReason != ModuleFragmentFailureReason.Timeout))
            {
                results[widget.WidgetId] = cached;
                continue;
            }

            var target = ResolveTarget(httpContext, moduleBaseHref, config.FragmentPath, options,
                _webAppOptions.Value.PortalTopBar.PortalBaseUrl, out var endpointIssue);
            if (endpointIssue != ModuleFragmentEndpointIssue.None)
            {
                _endpointDiagnostics.Report(endpointIssue);
                results[widget.WidgetId] = ModuleFragmentResult.EndpointBlocked(endpointIssue);
                continue;
            }

            if (target is null)
            {
                _logger.LogWarning(
                    "Dashboard module fragment for widget {WidgetId} (app {AppKey}) has no usable request address.",
                    widget.WidgetId,
                    config.AppKey);
                results[widget.WidgetId] = ModuleFragmentResult.Unavailable;
                continue;
            }

            cookieHeader ??= BuildForwardedCookieHeader(
                httpContext.Request.Headers.Cookie.ToString(),
                GetAuthCookieName());
            correlationId ??= httpContext.Items.TryGetValue(OmpRequestCorrelationMiddleware.ItemKey, out var item)
                && item is string correlation
                    ? correlation
                    : string.Empty;
            pending.Add(new PreparedFetch(
                widget.WidgetId,
                config.AppKey,
                moduleBaseHref,
                target,
                cookieHeader,
                correlationId,
                cacheKey));
        }

        if (pending.Count == 0)
        {
            return results;
        }

        var fetched = await Task.WhenAll(pending.Select(async fetch =>
            (Fetch: fetch, Result: await FetchAsync(fetch, options, ct))));
        foreach (var (fetch, result) in fetched)
        {
            var resultCacheDuration = result.FailureReason == ModuleFragmentFailureReason.Timeout
                ? timeoutCacheDuration : cacheDuration;
            if (resultCacheDuration > TimeSpan.Zero)
            {
                // Briefly shield a slow module while allowing bounded cold-start retries.
                _cache.Set(fetch.CacheKey, result, resultCacheDuration);
            }

            results[fetch.WidgetId] = result;
        }

        return results;
    }

    private async Task<ModuleFragmentResult> FetchAsync(
        PreparedFetch fetch,
        ModuleFragmentWidgetOptions options,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.GetTimeout());
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, fetch.Target.RequestUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            request.Headers.TryAddWithoutValidation(FragmentRequestHeader, "1");
            if (fetch.Target.HostHeader is not null)
            {
                request.Headers.Host = fetch.Target.HostHeader;
            }

            if (!string.IsNullOrWhiteSpace(fetch.CorrelationId))
            {
                request.Headers.TryAddWithoutValidation(OmpRequestCorrelationMiddleware.HeaderName, fetch.CorrelationId);
            }

            if (fetch.CookieHeader.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("Cookie", fetch.CookieHeader);
            }

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Dashboard module fragment for widget {WidgetId} (app {AppKey}) returned HTTP {StatusCode}.",
                    fetch.WidgetId,
                    OmpLogSanitizer.ForLog(fetch.AppKey),
                    (int)response.StatusCode);
                return ModuleFragmentResult.Unavailable;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Dashboard module fragment for widget {WidgetId} (app {AppKey}) was not text/html.",
                    fetch.WidgetId,
                    OmpLogSanitizer.ForLog(fetch.AppKey));
                return ModuleFragmentResult.Unavailable;
            }

            var html = await ReadLimitedAsync(response.Content, options.GetMaxResponseBytes(), timeout.Token);
            if (html is null)
            {
                _logger.LogWarning(
                    "Dashboard module fragment for widget {WidgetId} (app {AppKey}) exceeded the size limit.",
                    fetch.WidgetId,
                    OmpLogSanitizer.ForLog(fetch.AppKey));
                return ModuleFragmentResult.Unavailable;
            }

            return ModuleFragmentHtmlSanitizer.Sanitize(html, fetch.ModuleBaseHref);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Dashboard module fragment for widget {WidgetId} (app {AppKey}) timed out.",
                fetch.WidgetId,
                OmpLogSanitizer.ForLog(fetch.AppKey));
            return ModuleFragmentResult.Unavailable with { FailureReason = ModuleFragmentFailureReason.Timeout };
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            _logger.LogWarning(
                "Dashboard module fragment for widget {WidgetId} (app {AppKey}) failed TLS/certificate validation at {Authority} (TLS host {TlsHost}). Reason: {Reason}",
                fetch.WidgetId,
                OmpLogSanitizer.ForLog(fetch.AppKey),
                OmpLogSanitizer.ForLog(fetch.Target.RequestUri.GetLeftPart(UriPartial.Authority)),
                OmpLogSanitizer.ForLog(fetch.Target.HostHeader ?? fetch.Target.RequestUri.Authority),
                OmpLogSanitizer.ForLog(ex.GetBaseException().Message));
            return ModuleFragmentResult.Unavailable;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                "Dashboard module fragment for widget {WidgetId} (app {AppKey}) could not be requested. Error: {Error}. Reason: {Reason}",
                fetch.WidgetId,
                OmpLogSanitizer.ForLog(fetch.AppKey),
                ex.HttpRequestError,
                OmpLogSanitizer.ForLog(ex.GetBaseException().Message));
            return ModuleFragmentResult.Unavailable;
        }
    }

    /// <summary>
    /// Keeps only the cookies a module needs to recognise the user: the shared OMP auth
    /// cookie (including its chunks), the active role and the culture selection.
    /// </summary>
    internal static string BuildForwardedCookieHeader(string? rawCookieHeader, string authCookieName)
    {
        if (string.IsNullOrWhiteSpace(rawCookieHeader))
        {
            return string.Empty;
        }

        var kept = new List<string>();
        foreach (var part in rawCookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0 && IsForwardedCookie(part[..separator], authCookieName))
            {
                kept.Add(part);
            }
        }

        return string.Join("; ", kept);
    }

    private static bool IsForwardedCookie(string name, string authCookieName)
        => string.Equals(name, authCookieName, StringComparison.Ordinal)
            || IsAuthCookieChunk(name, authCookieName)
            || string.Equals(name, ActiveRoleCookie.CookieName, StringComparison.Ordinal)
            || string.Equals(name, CultureSelectionService.PreferredCultureCookieName, StringComparison.Ordinal)
            || string.Equals(name, CookieRequestCultureProvider.DefaultCookieName, StringComparison.Ordinal);

    // ASP.NET Core's chunking cookie manager splits a large auth cookie into
    // <name>C1, <name>C2, ... next to the <name> cookie that holds the chunk count.
    private static bool IsAuthCookieChunk(string name, string authCookieName)
        => name.Length > authCookieName.Length + 1
            && name.StartsWith(authCookieName + "C", StringComparison.Ordinal)
            && name[(authCookieName.Length + 1)..].All(char.IsAsciiDigit);

    internal static FragmentRequestTarget? BuildTarget(
        HttpContext httpContext,
        string moduleBaseHref,
        string fragmentPath,
        ModuleFragmentWidgetOptions options,
        string? configuredPortalBaseUrl = null)
        => ResolveTarget(httpContext, moduleBaseHref, fragmentPath, options, configuredPortalBaseUrl, out _);

    /// <summary>
    /// Builds the fragment request target. Returns null with a non-None
    /// <paramref name="endpointIssue"/> when the configured InternalBaseUrl blocks the request.
    /// </summary>
    internal static FragmentRequestTarget? ResolveTarget(
        HttpContext httpContext,
        string moduleBaseHref,
        string fragmentPath,
        ModuleFragmentWidgetOptions options,
        string? configuredPortalBaseUrl,
        out ModuleFragmentEndpointIssue endpointIssue)
    {
        endpointIssue = ModuleFragmentEndpointIssue.None;
        var request = httpContext.Request;
        string basePath;
        Uri? registeredOrigin = null;
        if (moduleBaseHref.StartsWith('/') && !moduleBaseHref.StartsWith("//", StringComparison.Ordinal))
        {
            basePath = moduleBaseHref;
        }
        else if (Uri.TryCreate(moduleBaseHref, UriKind.Absolute, out var absolute)
            && absolute.Scheme is "http" or "https")
        {
            basePath = absolute.AbsolutePath;
            // AppLinkBuilder builds relative registrations from the request's own scheme
            // and host; only an address that differs from those came from the module's
            // registration (omp.AppInstances / omp.Hosts) and may be requested directly.
            // Normalize BOTH authorities: Uri removes explicit default ports. Comparing
            // against raw Host would misclassify a client host ending in :443/:80 as a
            // registered remote origin and let it steer the authenticated request.
            var requestOrigin = Uri.TryCreate(request.GetPublicBaseUrl(), UriKind.Absolute, out var incoming)
                ? incoming.GetLeftPart(UriPartial.Authority)
                : null;
            if (!string.Equals(
                    absolute.GetLeftPart(UriPartial.Authority),
                    requestOrigin,
                    StringComparison.OrdinalIgnoreCase))
            {
                registeredOrigin = new Uri(absolute.GetLeftPart(UriPartial.Authority));
            }
        }
        else
        {
            return null;
        }

        var relative = basePath.TrimEnd('/') + fragmentPath;
        // An unsafe explicit override fails closed and never falls through to another
        // destination; the caller reports the reason.
        endpointIssue = ModuleFragmentEndpointCheck.Check(options, request.IsHttps, out var internalBase);
        if (endpointIssue != ModuleFragmentEndpointIssue.None)
        {
            return null;
        }

        if (internalBase is not null)
        {
            return new FragmentRequestTarget(
                new Uri(internalBase.GetLeftPart(UriPartial.Authority) + relative),
                HostHeader: null);
        }

        if (registeredOrigin is not null)
        {
            return new FragmentRequestTarget(new Uri(registeredOrigin, relative), HostHeader: null);
        }

        // The shared options validator also allows relative URLs and non-HTTP schemes
        // for navigation. Only an explicit HTTPS origin can replace local HTTPS here;
        // never downgrade identity cookies to HTTP or use a client-supplied authority.
        if (request.IsHttps
            && Uri.TryCreate(configuredPortalBaseUrl, UriKind.Absolute, out var publicBase)
            && publicBase.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(publicBase.UserInfo))
        {
            return new FragmentRequestTarget(
                new Uri(publicBase.GetLeftPart(UriPartial.Authority) + relative), HostHeader: null);
        }

        var localPort = httpContext.Connection.LocalPort;
        if (localPort <= 0 || !request.Host.HasValue)
        {
            return null;
        }

        var scheme = request.IsHttps ? "https" : "http";
        return new FragmentRequestTarget(
            new Uri(string.Create(CultureInfo.InvariantCulture, $"{scheme}://localhost:{localPort}{relative}")),
            request.Host.Value);
    }

    private static string BuildCacheKey(HttpContext httpContext, int widgetId, string? payload)
    {
        var user = httpContext.User;
        var userKey = user.FindFirstValue(OmpAuthDefaults.UserIdClaimType)
            ?? user.Identity?.Name
            ?? "anonymous";
        var activeRole = httpContext.Request.Cookies[ActiveRoleCookie.CookieName] ?? string.Empty;
        var culture = CultureInfo.CurrentUICulture.Name;
        var payloadHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(payload ?? string.Empty)))[..16];
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{CacheKeyPrefix}{widgetId}:{payloadHash}:{userKey}:{activeRole}:{culture}");
    }

    private string GetAuthCookieName()
    {
        var configured = _authOptions.Value.CookieName;
        return string.IsNullOrWhiteSpace(configured) ? OmpAuthDefaults.CookieName : configured.Trim();
    }

    private static async Task<string?> ReadLimitedAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength is { } length && length > maxBytes)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var scratch = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(scratch.AsMemory(0, scratch.Length), ct);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(scratch, 0, read);
        }

        var encoding = Encoding.UTF8;
        var charset = content.Headers.ContentType?.CharSet;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
                encoding = Encoding.UTF8;
            }
        }

        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    internal sealed record FragmentRequestTarget(Uri RequestUri, string? HostHeader);

    private sealed record PreparedFetch(
        int WidgetId,
        string AppKey,
        string ModuleBaseHref,
        FragmentRequestTarget Target,
        string CookieHeader,
        string CorrelationId,
        string CacheKey);
}

/// <summary>
/// One module-fragment widget to load: its definition id and stored payload.
/// </summary>
public sealed record ModuleFragmentWidgetRequest(int WidgetId, string? Payload);

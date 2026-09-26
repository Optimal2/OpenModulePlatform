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
/// otherwise to this server's own local endpoint with the public host name only as the
/// Host header - never to a host taken from the incoming request, which a client
/// controls.
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
    private readonly ILogger<PortalModuleFragmentService> _logger;

    public PortalModuleFragmentService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IOptionsMonitor<ModuleFragmentWidgetOptions> options,
        IOptions<OmpAuthOptions> authOptions,
        ILogger<PortalModuleFragmentService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _options = options;
        _authOptions = authOptions;
        _logger = logger;
    }

    /// <summary>
    /// Loads the fragment for one widget. Returns <see cref="ModuleFragmentResult.Unavailable"/>
    /// without any request when the widget is not in <paramref name="accessibleWidgetIds"/>
    /// or its module web app is not in <paramref name="accessibleApps"/>.
    /// </summary>
    public async Task<ModuleFragmentResult> GetFragmentAsync(
        HttpContext httpContext,
        int widgetId,
        string? payload,
        IReadOnlySet<int> accessibleWidgetIds,
        IReadOnlyList<PortalAppEntry> accessibleApps,
        CancellationToken ct)
    {
        var results = await GetFragmentsAsync(
            httpContext,
            [new ModuleFragmentWidgetRequest(widgetId, payload)],
            accessibleWidgetIds,
            accessibleApps,
            ct);
        return results[widgetId];
    }

    /// <summary>
    /// Loads several fragments. Everything that reads <paramref name="httpContext"/> runs
    /// sequentially; only the module requests themselves run in parallel, each bounded by
    /// the configured timeout.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, ModuleFragmentResult>> GetFragmentsAsync(
        HttpContext httpContext,
        IReadOnlyList<ModuleFragmentWidgetRequest> widgets,
        IReadOnlySet<int> accessibleWidgetIds,
        IReadOnlyList<PortalAppEntry> accessibleApps,
        CancellationToken ct)
    {
        var options = _options.CurrentValue;
        var cacheDuration = options.GetCacheDuration();
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

            if (!accessibleWidgetIds.Contains(widget.WidgetId)
                || ModuleFragmentWidget.TryParsePayload(widget.Payload) is not { } config)
            {
                results[widget.WidgetId] = ModuleFragmentResult.Unavailable;
                continue;
            }

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
            if (cacheDuration > TimeSpan.Zero
                && _cache.TryGetValue(cacheKey, out ModuleFragmentResult? cached)
                && cached is not null)
            {
                results[widget.WidgetId] = cached;
                continue;
            }

            var target = BuildTarget(httpContext, moduleBaseHref, config.FragmentPath, options);
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
            if (cacheDuration > TimeSpan.Zero)
            {
                // Failures are cached too, so a slow or broken module is asked at most
                // once per user and widget per cache window instead of on every load.
                _cache.Set(fetch.CacheKey, result, cacheDuration);
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
                    fetch.AppKey,
                    (int)response.StatusCode);
                return ModuleFragmentResult.Unavailable;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Dashboard module fragment for widget {WidgetId} (app {AppKey}) was not text/html.",
                    fetch.WidgetId,
                    fetch.AppKey);
                return ModuleFragmentResult.Unavailable;
            }

            var html = await ReadLimitedAsync(response.Content, options.GetMaxResponseBytes(), timeout.Token);
            if (html is null)
            {
                _logger.LogWarning(
                    "Dashboard module fragment for widget {WidgetId} (app {AppKey}) exceeded the size limit.",
                    fetch.WidgetId,
                    fetch.AppKey);
                return ModuleFragmentResult.Unavailable;
            }

            return ModuleFragmentHtmlSanitizer.Sanitize(html, fetch.ModuleBaseHref);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Dashboard module fragment for widget {WidgetId} (app {AppKey}) timed out.",
                fetch.WidgetId,
                fetch.AppKey);
            return ModuleFragmentResult.Unavailable;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "Dashboard module fragment for widget {WidgetId} (app {AppKey}) could not be requested.",
                fetch.WidgetId,
                fetch.AppKey);
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
        ModuleFragmentWidgetOptions options)
    {
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
            if (!string.Equals(
                    absolute.GetLeftPart(UriPartial.Authority),
                    request.GetPublicBaseUrl(),
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
        if (!string.IsNullOrWhiteSpace(options.InternalBaseUrl)
            && Uri.TryCreate(options.InternalBaseUrl.Trim(), UriKind.Absolute, out var internalBase)
            && internalBase.Scheme is "http" or "https")
        {
            return new FragmentRequestTarget(
                new Uri(internalBase.GetLeftPart(UriPartial.Authority) + relative),
                HostHeader: null);
        }

        if (registeredOrigin is not null)
        {
            return new FragmentRequestTarget(new Uri(registeredOrigin, relative), HostHeader: null);
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

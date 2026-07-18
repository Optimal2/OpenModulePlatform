// File: OpenModulePlatform.Web.Shared/Web/OmpRequestCorrelationMiddleware.cs
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OpenModulePlatform.Web.Shared.Web;

/// <summary>
/// Request-correlation middleware for every OMP web application.
/// </summary>
/// <remarks>
/// <para>
/// Resolves (or generates) a correlation id per request, exposes it as the
/// <c>X-Correlation-ID</c> response header and via <see cref="HttpContext.Items"/>
/// under <see cref="ItemKey"/>, and opens a logging scope carrying the id so every log
/// line emitted while the request is handled can render it (see the NLog
/// <c>${scopeproperty:item=CorrelationId}</c> layout used by the module web apps).
/// </para>
/// <para>
/// The middleware is registered as early as possible in <c>UseOmpWebDefaults</c> so the
/// scope wraps the whole request, including error handling. A caller-supplied
/// <c>X-Correlation-ID</c> is honoured only when it is a short, safe token; otherwise a
/// fresh id is generated so the value that reaches the logs is never attacker-controlled
/// beyond a bounded token shape. ${aspnet-TraceIdentifier} alone is per-process and does
/// not propagate across service boundaries (WebClient -&gt; ODVGateway, Portal -&gt; module);
/// an inbound correlation id does.
/// </para>
/// </remarks>
public static class OmpRequestCorrelationMiddleware
{
    /// <summary>The request/response header carrying the correlation id.</summary>
    public const string HeaderName = "X-Correlation-ID";

    /// <summary>The <see cref="HttpContext.Items"/> key holding the resolved correlation id.</summary>
    public const string ItemKey = "CorrelationId";

    /// <summary>The logging-scope property name (matches the NLog layout the sinks render).</summary>
    private const string ScopeKey = "CorrelationId";

    /// <summary>Upper bound on an accepted inbound correlation id.</summary>
    private const int MaxLength = 128;

    /// <summary>
    /// Adds the request-correlation middleware. Register it early (after the security
    /// headers and before request localization) so the logging scope covers the request.
    /// </summary>
    public static IApplicationBuilder UseOmpRequestCorrelation(this IApplicationBuilder app)
    {
        var loggerFactory = app.ApplicationServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("OpenModulePlatform.Web.Shared.RequestCorrelation");

        return app.Use(async (context, next) =>
        {
            var correlationId = ResolveCorrelationId(context.Request.Headers[HeaderName].ToString());
            context.Items[ItemKey] = correlationId;

            context.Response.OnStarting(static state =>
            {
                var httpContext = (HttpContext)state;
                if (httpContext.Items[ItemKey] is string id
                    && id.Length > 0
                    && !httpContext.Response.Headers.ContainsKey(HeaderName))
                {
                    httpContext.Response.Headers[HeaderName] = id;
                }

                return Task.CompletedTask;
            }, context);

            using (logger.BeginScope(new Dictionary<string, object> { [ScopeKey] = correlationId }))
            {
                await next(context);
            }
        });
    }

    private static string ResolveCorrelationId(string? candidate)
        => IsValidCorrelationId(candidate) ? candidate!.Trim() : Guid.NewGuid().ToString("N");

    private static bool IsValidCorrelationId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxLength)
        {
            return false;
        }

        foreach (var ch in trimmed)
        {
            var isToken = ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                or '.' or '_' or '-';
            if (!isToken)
            {
                return false;
            }
        }

        return true;
    }
}

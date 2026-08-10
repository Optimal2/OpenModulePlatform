using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Core.Infrastructure;
using Microsoft.AspNetCore.Mvc.Filters;

namespace OpenModulePlatform.Web.Shared.Mvc;

/// <summary>
/// Replaces the framework's bare antiforgery 400 with a redirect back to the
/// posted page. A stale token (expired session, a tab left open across a
/// deploy) otherwise produces an empty chunk-framed 400 that a scanning
/// middlebox can surface as a lone "0" page; the redirect reloads the page
/// with a fresh token instead, and redirect responses already announce an
/// explicit Content-Length: 0.
/// </summary>
public sealed class AntiforgeryFailureRedirectFilter : IAsyncAlwaysRunResultFilter
{
    public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is IAntiforgeryValidationFailedResult)
        {
            // The query string is dropped on purpose: it points at the failed
            // POST handler (?handler=...) and must not follow into the page
            // reload. PathBase keeps the app prefix on hosts that mount the
            // app under a sub-path.
            var request = context.HttpContext.Request;
            var target = request.PathBase.Add(request.Path);
            context.Result = new RedirectResult(target.HasValue ? target.Value! : "/");
        }

        return next();
    }
}

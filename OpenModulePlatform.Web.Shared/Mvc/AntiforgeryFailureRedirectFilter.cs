using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Core.Infrastructure;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;

namespace OpenModulePlatform.Web.Shared.Mvc;

/// <summary>
/// Replaces the framework's bare antiforgery 400 with a redirect back to the
/// posted page. A stale token (expired session, a tab left open across a
/// deploy) otherwise produces an empty chunk-framed 400 that a scanning
/// middlebox can surface as a lone "0" page; the redirect reloads the page
/// with a fresh token instead, and redirect responses already announce an
/// explicit Content-Length: 0.
/// </summary>
/// <remarks>
/// R12-C2. The reload alone is a silent data loss: the posted form is discarded, the page
/// comes back looking exactly as it did, and the operator has every reason to believe the
/// change was saved. Two things fix that here, and neither of them can live in a page:
/// <list type="bullet">
/// <item>The reload carries <see cref="TokenExpiredQueryKey"/>, so the app can tell the
/// operator their change was NOT saved. A query flag rather than TempData because the
/// antiforgery result comes from an authorization filter - the page, and therefore its
/// TempData, never runs.</item>
/// <item>The rest of the query string is preserved. Dropping all of it also dropped the
/// page's own state: on the IbsPackager settings page the reload lost <c>?selectedKey=</c>
/// and jumped to a different setting than the one the operator had been editing, so the
/// value they saw afterwards belonged to something else entirely.</item>
/// </list>
/// </remarks>
public sealed class AntiforgeryFailureRedirectFilter : IAsyncAlwaysRunResultFilter
{
    /// <summary>Query key that marks a reload as "your POST was rejected, nothing was saved".</summary>
    public const string TokenExpiredQueryKey = "ompTokenExpired";

    /// <summary>
    /// The one query key that must not survive: it names the POST handler that just
    /// failed, and carrying it into a GET reload would invoke the wrong handler.
    /// </summary>
    private const string HandlerQueryKey = "handler";

    public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is IAntiforgeryValidationFailedResult)
        {
            // Not during a status-page re-execution. UseStatusCodePagesWithReExecute replays
            // the pipeline on /status/{code} while keeping the original result, so without
            // this guard the filter fires a second time and builds its redirect from the
            // STATUS page's path -- producing /status/400?ompTokenExpired=1. That is the wrong
            // page to flag: the operator is told "nothing was saved" on an error page instead
            // of on the form they were editing, and the reload never returns to their work.
            // Caught by TopbarAntiforgeryValidationTests, which pins the rejection shape for
            // the topbar endpoints (R3-E2).
            if (context.HttpContext.Features.Get<IStatusCodeReExecuteFeature>() is not null)
            {
                return next();
            }

            // PathBase keeps the app prefix on hosts that mount the app under a sub-path.
            var request = context.HttpContext.Request;
            var target = request.PathBase.Add(request.Path);
            var path = target.HasValue ? target.Value! : "/";

            var query = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var parameter in request.Query)
            {
                if (string.Equals(parameter.Key, HandlerQueryKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                query[parameter.Key] = parameter.Value.ToString();
            }

            query[TokenExpiredQueryKey] = "1";

            context.Result = new RedirectResult(QueryHelpers.AddQueryString(path, query));
        }

        return next();
    }
}

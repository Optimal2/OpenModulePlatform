// File: OpenModulePlatform.Web.iFrameWebAppModule/Services/IFrameActivityLog.cs
using System.Globalization;
using System.Security.Claims;
using OpenModulePlatform.Web.Shared.ActivityLog;
using OpenModulePlatform.Web.Shared.Security;

namespace OpenModulePlatform.Web.iFrameWebAppModule.Services;

/// <summary>
/// The user log entries the iFrame module writes, shared by the embedded and
/// the standalone page. Writing never affects the page: the writer swallows
/// storage errors, and no cancellation token is passed so an aborted request
/// cannot throw out of a page that already rendered.
/// </summary>
public static class IFrameActivityLog
{
    /// <summary>
    /// One entry per opened target: which configured URL the user saw, or that
    /// the active role was not allowed to see it. The address itself is
    /// configuration, not something the user typed, so the entry names the row
    /// by its id and display name and leaves the URL out. A principal without
    /// an OMP user id (a Windows or AD identity without an account row, or the
    /// anonymous mode) is not a person the log can name, so an ordinary open
    /// is not written for them; a refused open is, since a denial is worth a
    /// record even without a name (the writer keeps it with a null user id).
    /// </summary>
    public static Task WriteOpenedAsync(
        ActivityLogWriter activityLog,
        ClaimsPrincipal? user,
        int urlId,
        string displayName,
        string? setKey,
        bool standalone,
        string outcome)
    {
        if (outcome == ActivityOutcomes.Ok && OmpUserIdentity.TryGetOmpUserId(user) is null)
        {
            return Task.CompletedTask;
        }

        return activityLog.WriteAsync(new ActivityEntry
        {
            Event = "iframe_url.opened",
            Summary = outcome == ActivityOutcomes.Ok
                ? $"Opened \"{displayName}\""
                : $"Was not allowed to open \"{displayName}\"",
            Outcome = outcome,
            Subject = new ActivitySubject("iframe_url", urlId.ToString(CultureInfo.InvariantCulture), displayName),
            Data = new Dictionary<string, object?>
            {
                ["setKey"] = setKey,
                ["standalone"] = standalone
            }
        }, user, CancellationToken.None);
    }
}

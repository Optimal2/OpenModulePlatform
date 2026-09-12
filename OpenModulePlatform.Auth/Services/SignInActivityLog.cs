// File: OpenModulePlatform.Auth/Services/SignInActivityLog.cs
using System.Globalization;
using OpenModulePlatform.Auth.Models;
using OpenModulePlatform.Web.Shared.ActivityLog;

namespace OpenModulePlatform.Auth.Services;

/// <summary>
/// The user log entries the Auth app writes, shared by every sign-in path (local
/// password, Windows, OIDC). Writing never affects the sign-in itself: the writer
/// swallows storage errors, and the calls pass no cancellation token so an
/// aborted request cannot throw out of a sign-in callback.
/// </summary>
public static class SignInActivityLog
{
    /// <summary>
    /// One entry per sign-in: it belongs to the user who signed in and says which
    /// provider let them in. A user without an OMP id (an AD principal resolved
    /// without an account row) is not a person the log can name, so nothing is
    /// written for them.
    /// </summary>
    public static Task WriteSignedInAsync(ActivityLogWriter activityLog, OmpAuthenticatedUser user)
    {
        if (user.UserId is not int userId)
        {
            return Task.CompletedTask;
        }

        return activityLog.WriteAsync(new ActivityEntry
        {
            Event = "session.signed_in",
            Summary = $"Signed in via {user.Provider}",
            Subject = SubjectFor(userId, user),
            Data = new Dictionary<string, object?> { ["provider"] = user.Provider }
        }, userId, CancellationToken.None);
    }

    /// <summary>Self-registration created the account; the sign-in that follows is logged separately.</summary>
    public static Task WriteRegisteredAsync(ActivityLogWriter activityLog, OmpAuthenticatedUser user)
    {
        if (user.UserId is not int userId)
        {
            return Task.CompletedTask;
        }

        return activityLog.WriteAsync(new ActivityEntry
        {
            Event = "user.registered",
            Summary = $"Registered a local account ({user.Provider})",
            Subject = SubjectFor(userId, user),
            Data = new Dictionary<string, object?> { ["provider"] = user.Provider }
        }, userId, CancellationToken.None);
    }

    private static ActivitySubject SubjectFor(int userId, OmpAuthenticatedUser user)
        => new("user", userId.ToString(CultureInfo.InvariantCulture), user.DisplayName);
}

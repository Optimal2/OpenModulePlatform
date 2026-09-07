// File: OpenModulePlatform.Web.Shared/ActivityLog/ActivityEntry.cs
namespace OpenModulePlatform.Web.Shared.ActivityLog;

/// <summary>
/// One human-attributable thing that happened in a module: who did what to which
/// object, with an outcome and a one-line English summary. Modules build one of
/// these per event and hand it to <see cref="ActivityLogWriter"/>, which stores it
/// as the versioned JSON envelope in the module's own ActivityLog table.
/// </summary>
/// <remarks>
/// The envelope is generic across every module on purpose: everything except
/// <see cref="Data"/> means the same thing everywhere, so the Portal's activity
/// viewer can list, filter and search entries from all modules with one reader.
/// <see cref="Event"/> is a lower-case dotted "object.verb-in-past-tense" key from
/// the module's own vocabulary (manual_review.approved, job.requeued,
/// search_job.created); <see cref="Subject"/> names the primary object with the same
/// object word as the event so "everything about object X" is one filter.
/// </remarks>
public sealed record ActivityEntry
{
    /// <summary>Lower-case dotted event key, e.g. <c>manual_review.approved</c>.</summary>
    public required string Event { get; init; }

    /// <summary>
    /// One English line a reader can scan in a list. Version 1 stores plain text;
    /// a message key plus arguments for localization is a later envelope version.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>What happened to the request: <see cref="ActivityOutcomes"/>.</summary>
    public string Outcome { get; init; } = ActivityOutcomes.Ok;

    /// <summary>The primary object the event is about, if any.</summary>
    public ActivitySubject? Subject { get; init; }

    /// <summary>
    /// Who acted. Left null for web requests: the writer fills it from the signed-in
    /// principal (kind "user", the display name as a snapshot). Background services
    /// set kind "system" with the service name.
    /// </summary>
    public ActivityActor? Actor { get; init; }

    /// <summary>
    /// Ties a web request to the work it caused elsewhere (the request correlation
    /// id, a job id). Free text, optional.
    /// </summary>
    public string? Correlation { get; init; }

    /// <summary>
    /// Event-specific details. The viewer shows them as key/value pairs and never
    /// interprets them, so a module may put whatever helps a reader here. Keep it
    /// flat and free of customer content.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Data { get; init; }
}

/// <summary>The object an <see cref="ActivityEntry"/> is about.</summary>
/// <param name="Type">Object word matching the event key, e.g. <c>manual_review_item</c>.</param>
/// <param name="Id">The object's identifier as text.</param>
/// <param name="Label">Optional human label (a channel key, a title) shown next to the id.</param>
public sealed record ActivitySubject(string Type, string Id, string? Label = null);

/// <summary>Who performed the action: a snapshot for display, not the identity of record.</summary>
/// <param name="Kind"><see cref="ActivityActorKinds"/>.</param>
/// <param name="Name">Display name at the time of the event.</param>
public sealed record ActivityActor(string Kind, string? Name);

/// <summary>Well-known values for <see cref="ActivityEntry.Outcome"/>.</summary>
public static class ActivityOutcomes
{
    public const string Ok = "ok";
    public const string Failed = "failed";
    public const string Denied = "denied";
}

/// <summary>Well-known values for <see cref="ActivityActor.Kind"/>.</summary>
public static class ActivityActorKinds
{
    /// <summary>A signed-in OMP user; the OmpUserId column carries the identity of record.</summary>
    public const string User = "user";

    /// <summary>A background service or worker acting on its own schedule.</summary>
    public const string System = "system";
}

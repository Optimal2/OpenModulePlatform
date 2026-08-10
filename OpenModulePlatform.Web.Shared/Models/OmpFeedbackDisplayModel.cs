namespace OpenModulePlatform.Web.Shared.Models;

/// <summary>
/// Pre-localized feedback for the OmpFeedback view component: a success
/// message, an error message, and optionally the current ModelState errors.
/// The caller localizes the texts; the component only renders them with the
/// correct roles and the scroll anchor.
/// </summary>
public sealed class OmpFeedbackDisplayModel
{
    public string? Message { get; init; }

    public string? Error { get; init; }

    /// <summary>Render the page's ModelState errors as a summary list.</summary>
    public bool IncludeValidationSummary { get; init; } = true;
}

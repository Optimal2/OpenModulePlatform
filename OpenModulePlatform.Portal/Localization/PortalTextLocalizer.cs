// File: OpenModulePlatform.Portal/Localization/PortalTextLocalizer.cs
using System.Text.RegularExpressions;
using Microsoft.Extensions.Localization;

namespace OpenModulePlatform.Portal.Localization;

/// <summary>
/// Localizes human-facing text that originates from exceptions surfaced by
/// Portal actions - primarily SQL THROW messages from the admin repositories
/// and the known service exception texts. The stored/thrown text remains
/// stable and machine-readable; only the display text is translated. Unknown
/// texts fall through unchanged so no information is lost.
/// </summary>
public static partial class PortalTextLocalizer
{
    public static string Display(IStringLocalizer localizer, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        var text = value.Trim();

        // ArgumentException appends " (Parameter 'name')" to the message.
        // The parameter name is a developer detail, so display only the
        // localized message text.
        if (ArgumentParameterSuffixRegex().Match(text) is { Success: true } withParameter)
        {
            return Display(localizer, withParameter.Groups["text"].Value);
        }

        var exact = text switch
        {
            // Portal admin repository THROW messages (SqlException.Message).
            "Host app deployment identity repair columns are not installed. Run module definition SQL repair first." => localizer["Host app deployment identity repair columns are not installed. Run module definition SQL repair first."],
            "The selected service-app deployment state was not found." => localizer["The selected service-app deployment state was not found."],
            "Module definition document was not found." => localizer["Module definition document was not found."],
            "HostAgent job queue is not available. Apply the core OMP schema before queueing HostAgent jobs." => localizer["HostAgent job queue is not available. Apply the core OMP schema before queueing HostAgent jobs."],
            "HostAgent job queue is not available. Apply the core OMP schema before queueing maintenance jobs." => localizer["HostAgent job queue is not available. Apply the core OMP schema before queueing maintenance jobs."],
            "Maintenance findings are not available. Apply the core OMP schema before queueing a maintenance scan." => localizer["Maintenance findings are not available. Apply the core OMP schema before queueing a maintenance scan."],
            "Portal user setting definition is missing: Portal/AdminMetricsCollapsed." => localizer["Portal user setting definition is missing: Portal/AdminMetricsCollapsed."],
            "Portal user setting definition is missing: Portal/TopbarDropdownsOpenOnHover." => localizer["Portal user setting definition is missing: Portal/TopbarDropdownsOpenOnHover."],
            // Core schema THROW messages raised while saving web app
            // instances from the app instance editors.
            "Do not mix active host-neutral and targeted web app instances for the same module instance and web app definition." => localizer["Do not mix active host-neutral and targeted web app instances for the same module instance and web app definition."],
            "Do not mix active host-role and overlapping host-specific web app instances for the same module instance and web app definition." => localizer["Do not mix active host-role and overlapping host-specific web app instances for the same module instance and web app definition."],
            "Duplicate active host-neutral web app instances exist. Keep only one active desired host-neutral row per module instance and web app definition." => localizer["Duplicate active host-neutral web app instances exist. Keep only one active desired host-neutral row per module instance and web app definition."],
            "Duplicate active host-role web app instances exist. Keep only one active desired row per module instance, web app definition and host role." => localizer["Duplicate active host-role web app instances exist. Keep only one active desired row per module instance, web app definition and host role."],
            "Duplicate active host-specific web app instances exist. Keep only one active desired row per module instance, web app definition and host." => localizer["Duplicate active host-specific web app instances exist. Keep only one active desired row per module instance, web app definition and host."],
            "Only one active desired web app instance is allowed per module instance, web app definition and host placement." => localizer["Only one active desired web app instance is allowed per module instance, web app definition and host placement."],
            // Template editor variants of the same rules.
            "Do not mix active host-neutral and targeted template web app rows for the same template module and web app definition." => localizer["Do not mix active host-neutral and targeted template web app rows for the same template module and web app definition."],
            "Do not mix active host-role and overlapping host-specific template web app rows for the same template module and web app definition." => localizer["Do not mix active host-role and overlapping host-specific template web app rows for the same template module and web app definition."],
            "Duplicate active host-neutral template web app rows exist. Keep only one active desired host-neutral row per template module and web app definition." => localizer["Duplicate active host-neutral template web app rows exist. Keep only one active desired host-neutral row per template module and web app definition."],
            "Duplicate active host-role template web app rows exist. Keep only one active desired row per template module, web app definition and host role." => localizer["Duplicate active host-role template web app rows exist. Keep only one active desired row per template module, web app definition and host role."],
            "Duplicate active host-specific template web app rows exist. Keep only one active desired row per template module, web app definition and template host." => localizer["Duplicate active host-specific template web app rows exist. Keep only one active desired row per template module, web app definition and template host."],
            "Only one active desired template web app row is allowed per template module, web app definition and host placement." => localizer["Only one active desired template web app row is allowed per template module, web app definition and host placement."],
            // Host deployment and template materialization THROW messages.
            "Host deployment request requires HostKey." => localizer["Host deployment request requires HostKey."],
            "Host deployment request host was not found or is disabled." => localizer["Host deployment request host was not found or is disabled."],
            "Host deployment request host template was not found or is disabled." => localizer["Host deployment request host template was not found or is disabled."],
            "Host deployment request host template is not actively assigned to the host." => localizer["Host deployment request host template is not actively assigned to the host."],
            "Template materialization host was not found or is disabled." => localizer["Template materialization host was not found or is disabled."],
            "Template materialization host does not have the requested active host template assignment." => localizer["Template materialization host does not have the requested active host template assignment."],
            // MessageService exception texts surfaced on the Messages pages.
            "Direct conversations require two distinct OMP users." => localizer["Direct conversations require two distinct OMP users."],
            "Group conversations require at least two OMP users." => localizer["Group conversations require at least two OMP users."],
            "A message must contain text or at least one attachment." => localizer["A message must contain text or at least one attachment."],
            "OMP messages tables are not installed." => localizer["OMP messages tables are not installed."],
            "OMP messages are disabled." => localizer["OMP messages are disabled."],
            "OMP user does not exist or is not active." => localizer["OMP user does not exist or is not active."],
            "Attachment content type is not allowed." => localizer["Attachment content type is not allowed."],
            // Dashboard widget upload service exception texts (music player
            // and blank widget JSON handlers on the dashboard).
            "Upload one MP3 file." => localizer["Upload one MP3 file."],
            "Upload a zip file." => localizer["Upload a zip file."],
            "The zip file does not contain any MP3 files." => localizer["The zip file does not contain any MP3 files."],
            "The music player widget definition is missing." => localizer["The music player widget definition is missing."],
            "Upload one image or GIF file." => localizer["Upload one image or GIF file."],
            "The zip file does not contain any image or GIF files." => localizer["The zip file does not contain any image or GIF files."],
            "The blank widget definition is missing." => localizer["The blank widget definition is missing."],
            "Upload a GIF, PNG, JPG, or JPEG file." => localizer["Upload a GIF, PNG, JPG, or JPEG file."],
            "The uploaded file is not a supported image format." => localizer["The uploaded file is not a supported image format."],
            _ => null
        };

        if (exact is not null)
        {
            return exact;
        }

        if (AttachmentCountRegex().Match(text) is { Success: true } attachmentCount)
        {
            return localizer["A message can include at most {0} attachments.", attachmentCount.Groups["count"].Value];
        }

        if (AttachmentTooLargeRegex().Match(text) is { Success: true } attachmentTooLarge)
        {
            return localizer["Attachment file is too large. The current limit is {0}.", attachmentTooLarge.Groups["limit"].Value];
        }

        if (ZipLimitRegex().Match(text) is { Success: true } zipLimit)
        {
            return localizer["The zip file exceeds the limit of {0} bytes.", zipLimit.Groups["bytes"].Value];
        }

        if (NamedMp3LimitRegex().Match(text) is { Success: true } namedMp3Limit)
        {
            return localizer["The MP3 file '{0}' exceeds the limit of {1} bytes.", namedMp3Limit.Groups["name"].Value, namedMp3Limit.Groups["bytes"].Value];
        }

        if (Mp3LimitRegex().Match(text) is { Success: true } mp3Limit)
        {
            return localizer["The MP3 file exceeds the limit of {0} bytes.", mp3Limit.Groups["bytes"].Value];
        }

        if (NamedImageLimitRegex().Match(text) is { Success: true } namedImageLimit)
        {
            return localizer["The image file '{0}' exceeds the limit of {1} bytes.", namedImageLimit.Groups["name"].Value, namedImageLimit.Groups["bytes"].Value];
        }

        if (ImageLimitRegex().Match(text) is { Success: true } imageLimit)
        {
            return localizer["The image file exceeds the limit of {0} bytes.", imageLimit.Groups["bytes"].Value];
        }

        if (UploadLimitRegex().Match(text) is { Success: true } uploadLimit)
        {
            return localizer["The uploaded file exceeds the limit of {0} bytes.", uploadLimit.Groups["bytes"].Value];
        }

        return text;
    }

    [GeneratedRegex(@"^(?<text>.+?) \(Parameter '(?<name>[^']*)'\)$", RegexOptions.CultureInvariant)]
    private static partial Regex ArgumentParameterSuffixRegex();

    [GeneratedRegex(@"^A message can include at most (?<count>\d+) attachments\.$", RegexOptions.CultureInvariant)]
    private static partial Regex AttachmentCountRegex();

    [GeneratedRegex(@"^Attachment file is too large\. The current limit is (?<limit>.+)\.$", RegexOptions.CultureInvariant)]
    private static partial Regex AttachmentTooLargeRegex();

    [GeneratedRegex(@"^The zip file exceeds the limit of (?<bytes>\d+) bytes\.$", RegexOptions.CultureInvariant)]
    private static partial Regex ZipLimitRegex();

    [GeneratedRegex(@"^The MP3 file '(?<name>[^']*)' exceeds the limit of (?<bytes>\d+) bytes\.$", RegexOptions.CultureInvariant)]
    private static partial Regex NamedMp3LimitRegex();

    [GeneratedRegex(@"^The MP3 file exceeds the limit of (?<bytes>\d+) bytes\.$", RegexOptions.CultureInvariant)]
    private static partial Regex Mp3LimitRegex();

    [GeneratedRegex(@"^The image file '(?<name>[^']*)' exceeds the limit of (?<bytes>\d+) bytes\.$", RegexOptions.CultureInvariant)]
    private static partial Regex NamedImageLimitRegex();

    [GeneratedRegex(@"^The image file exceeds the limit of (?<bytes>\d+) bytes\.$", RegexOptions.CultureInvariant)]
    private static partial Regex ImageLimitRegex();

    [GeneratedRegex(@"^The uploaded file exceeds the limit of (?<bytes>\d+) bytes\.$", RegexOptions.CultureInvariant)]
    private static partial Regex UploadLimitRegex();
}

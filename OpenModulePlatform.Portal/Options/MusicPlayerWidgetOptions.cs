// File: OpenModulePlatform.Portal/Options/MusicPlayerWidgetOptions.cs
namespace OpenModulePlatform.Portal.Options;

/// <summary>
/// Configuration for the dashboard music player widget. Mode selects the
/// player surface: "webamp" (Winamp-style player, vendored bundle under
/// wwwroot/lib/webamp — see PROVENANCE.md there) or "classic" (the plain
/// audio-element player). The classic markup is always rendered as fallback;
/// webamp takes over at bind time when enabled and supported, so an
/// unsupported browser or a missing bundle degrades to classic automatically.
/// </summary>
public sealed class MusicPlayerWidgetOptions
{
    public const string SectionName = "MusicPlayerWidget";
    public const string ModeWebamp = "webamp";
    public const string ModeClassic = "classic";

    public string Mode { get; set; } = ModeWebamp;
}

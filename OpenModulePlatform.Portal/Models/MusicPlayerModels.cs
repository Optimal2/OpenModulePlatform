// File: OpenModulePlatform.Portal/Models/MusicPlayerModels.cs
namespace OpenModulePlatform.Portal.Models;

/// <summary>
/// Client-facing playlist for the built-in Portal dashboard music player.
/// </summary>
public sealed record MusicPlayerPlaylist(IReadOnlyList<MusicPlayerTrack> Tracks);

/// <summary>
/// One music player track with server-generated media URL and attribution metadata.
/// </summary>
public sealed record MusicPlayerTrack(
    long Id,
    string Title,
    string Artist,
    string Src,
    string Attribution,
    string Source,
    string Description,
    string FileName,
    string ContentType,
    string ContentHash);

/// <summary>
/// Admin-provided metadata for a newly uploaded music player track.
/// </summary>
public sealed record MusicPlayerTrackInput(
    string? Title,
    string? Artist,
    string? Attribution,
    string? Source,
    string? Description);

/// <summary>
/// Binary media loaded from Portal widget media storage.
/// </summary>
public sealed record MusicPlayerBinaryFile(string FileName, string ContentType, byte[] Data);

/// <summary>
/// Result from adding or importing music player media.
/// </summary>
public sealed record MusicPlayerMutationResult(int AddedTracks, int ReusedTracks);

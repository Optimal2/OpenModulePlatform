// File: OpenModulePlatform.Portal/Models/BlankWidgetModels.cs
namespace OpenModulePlatform.Portal.Models;

/// <summary>
/// Client-facing list of database-backed images available to blank widgets.
/// </summary>
public sealed record BlankWidgetImageList(IReadOnlyList<BlankWidgetImage> Images);

/// <summary>
/// One blank widget image with a server-generated media URL.
/// </summary>
public sealed record BlankWidgetImage(
    long Id,
    string DisplayName,
    string Src,
    string FileName,
    string ContentType,
    string ContentHash);

/// <summary>
/// Binary image media loaded from Portal widget media storage.
/// </summary>
public sealed record BlankWidgetBinaryFile(string FileName, string ContentType, byte[] Data);

/// <summary>
/// Result from adding or importing blank widget media.
/// </summary>
public sealed record BlankWidgetMutationResult(
    int AddedImages,
    int ReusedImages,
    IReadOnlyList<long> ImageIds);

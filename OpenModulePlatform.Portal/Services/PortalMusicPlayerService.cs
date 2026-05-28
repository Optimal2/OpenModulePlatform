// File: OpenModulePlatform.Portal/Services/PortalMusicPlayerService.cs
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Web.Shared.Services;
using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenModulePlatform.Portal.Services;

/// <summary>
/// Stores dashboard music player metadata and media in the Portal database.
/// </summary>
public sealed class PortalMusicPlayerService
{
    private const string WidgetKey = "music-player";
    private const string DataKey = "music-player";
    private const string OwnerRef = "widget:music-player";
    private const string DocumentFormat = "omp.portal.music-player.v1";
    private const long MaxTrackBytes = 50L * 1024L * 1024L;
    private const long MaxZipBytes = 512L * 1024L * 1024L;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly SqlConnectionFactory _db;

    public PortalMusicPlayerService(SqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<MusicPlayerPlaylist> GetPlaylistAsync(Func<long, string> trackUrlFactory, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        var document = await ReadDocumentAsync(conn, tx: null, ct);
        if (document.Tracks.Count == 0)
        {
            return new MusicPlayerPlaylist([]);
        }

        var media = await GetEnabledMediaMapAsync(conn, document.Tracks.Select(track => track.BinaryDataId), ct);
        var tracks = document.Tracks
            .Where(track => media.ContainsKey(track.BinaryDataId))
            .Select(track => new MusicPlayerTrack(
                track.BinaryDataId,
                CleanForDisplay(track.Title, 200) ?? media[track.BinaryDataId].FileName,
                CleanForDisplay(track.Artist, 200) ?? string.Empty,
                trackUrlFactory(track.BinaryDataId),
                CleanForDisplay(track.Attribution, 500) ?? string.Empty,
                CleanForDisplay(track.Source, 1000) ?? string.Empty,
                CleanForDisplay(track.Description, 1000) ?? string.Empty,
                media[track.BinaryDataId].FileName,
                media[track.BinaryDataId].ContentType))
            .ToArray();

        return new MusicPlayerPlaylist(tracks);
    }

    public async Task<MusicPlayerBinaryFile?> GetTrackFileAsync(long binaryDataId, CancellationToken ct)
    {
        const string sql = @"
SELECT file_name,
       content_type,
       data_value
FROM omp_portal.widget_binary_data
WHERE binary_data_id = @binary_data_id
  AND owner_ref = @owner_ref
  AND is_enabled = 1;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@binary_data_id", SqlDbType.BigInt).Value = binaryDataId;
        cmd.Parameters.Add("@owner_ref", SqlDbType.NVarChar, 128).Value = OwnerRef;

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new MusicPlayerBinaryFile(
            rdr.GetString(0),
            rdr.GetString(1),
            (byte[])rdr.GetValue(2));
    }

    public async Task<MusicPlayerMutationResult> AddTrackAsync(
        IFormFile file,
        MusicPlayerTrackInput input,
        int? userId,
        CancellationToken ct)
    {
        if (!IsMp3Upload(file))
        {
            throw new InvalidOperationException("Upload one MP3 file.");
        }

        var bytes = await ReadUploadBytesAsync(file, MaxTrackBytes, ct);
        var track = new ImportedTrack(
            CleanForDisplay(input.Title, 200) ?? GetFileStem(file.FileName),
            CleanForDisplay(input.Artist, 200) ?? string.Empty,
            CleanForDisplay(input.Attribution, 500) ?? string.Empty,
            CleanForDisplay(input.Source, 1000) ?? string.Empty,
            CleanForDisplay(input.Description, 1000) ?? string.Empty,
            CleanFileName(file.FileName),
            bytes);

        return await ImportTracksAsync([track], userId, ct);
    }

    public async Task<MusicPlayerMutationResult> ImportZipAsync(IFormFile file, int? userId, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            throw new InvalidOperationException("Upload a zip file.");
        }

        if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Upload a zip file.");
        }

        if (file.Length > MaxZipBytes)
        {
            throw new InvalidOperationException($"The zip file exceeds the limit of {MaxZipBytes.ToString(CultureInfo.InvariantCulture)} bytes.");
        }

        await using var stream = file.OpenReadStream();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var mp3Entries = archive.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name)
                && entry.Name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (mp3Entries.Length == 0)
        {
            throw new InvalidOperationException("The zip file does not contain any MP3 files.");
        }

        var playlistMetadata = ReadPlaylistJsonMetadata(archive);
        var songsTextMetadata = ReadSongsTextMetadata(archive);
        var imported = new List<ImportedTrack>();
        foreach (var entry in mp3Entries)
        {
            if (entry.Length > MaxTrackBytes)
            {
                throw new InvalidOperationException($"The MP3 file '{entry.Name}' exceeds the limit of {MaxTrackBytes.ToString(CultureInfo.InvariantCulture)} bytes.");
            }

            var metadata = FindMetadata(entry.Name, playlistMetadata, songsTextMetadata);
            await using var entryStream = entry.Open();
            var bytes = await ReadStreamBytesAsync(entryStream, MaxTrackBytes, ct);
            imported.Add(new ImportedTrack(
                metadata?.Title ?? GetFileStem(entry.Name),
                metadata?.Artist ?? string.Empty,
                metadata?.Attribution ?? string.Empty,
                metadata?.Source ?? string.Empty,
                metadata?.Description ?? string.Empty,
                CleanFileName(entry.Name),
                bytes));
        }

        return await ImportTracksAsync(imported, userId, ct);
    }

    private async Task<MusicPlayerMutationResult> ImportTracksAsync(
        IReadOnlyList<ImportedTrack> tracks,
        int? userId,
        CancellationToken ct)
    {
        if (tracks.Count == 0)
        {
            return new MusicPlayerMutationResult(0, 0);
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            var widgetId = await GetWidgetIdAsync(conn, tx, ct)
                ?? throw new InvalidOperationException("The music player widget definition is missing.");
            var document = await ReadDocumentAsync(conn, tx, ct);
            var existingTrackIds = document.Tracks
                .Select(track => track.BinaryDataId)
                .ToHashSet();

            var addedTracks = 0;
            var reusedTracks = 0;
            foreach (var track in tracks)
            {
                var hash = SHA256.HashData(track.Bytes);
                var binaryDataId = await FindExistingBinaryDataIdAsync(conn, tx, hash, track.Bytes.LongLength, track.FileName, ct);
                if (!binaryDataId.HasValue)
                {
                    binaryDataId = await InsertBinaryDataAsync(conn, tx, track, hash, userId, ct);
                    addedTracks++;
                }
                else
                {
                    reusedTracks++;
                }

                if (existingTrackIds.Add(binaryDataId.Value))
                {
                    document.Tracks.Add(new MusicPlayerTrackDocumentItem
                    {
                        BinaryDataId = binaryDataId.Value,
                        Title = track.Title,
                        Artist = track.Artist,
                        Attribution = track.Attribution,
                        Source = track.Source,
                        Description = track.Description,
                        FileName = track.FileName
                    });
                }
            }

            await UpsertDocumentAsync(conn, tx, widgetId, document, ct);
            await tx.CommitAsync(ct);
            return new MusicPlayerMutationResult(addedTracks, reusedTracks);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static TrackMetadata? FindMetadata(
        string fileName,
        IReadOnlyDictionary<string, TrackMetadata> playlistMetadata,
        IReadOnlyList<TrackMetadata> songsTextMetadata)
    {
        var cleanFileName = CleanFileName(fileName);
        if (playlistMetadata.TryGetValue(cleanFileName, out var playlistTrack))
        {
            return playlistTrack;
        }

        var normalizedStem = NormalizeMatchText(GetFileStem(fileName));
        return songsTextMetadata.FirstOrDefault(track =>
            !string.IsNullOrWhiteSpace(track.Title)
            && !string.IsNullOrWhiteSpace(track.Artist)
            && normalizedStem.Contains(NormalizeMatchText(track.Title), StringComparison.Ordinal)
            && normalizedStem.Contains(NormalizeMatchText(track.Artist), StringComparison.Ordinal))
            ?? songsTextMetadata.FirstOrDefault(track =>
                !string.IsNullOrWhiteSpace(track.Title)
                && normalizedStem.Contains(NormalizeMatchText(track.Title), StringComparison.Ordinal));
    }

    private static IReadOnlyDictionary<string, TrackMetadata> ReadPlaylistJsonMetadata(ZipArchive archive)
    {
        var entry = archive.Entries.FirstOrDefault(item =>
            string.Equals(item.Name, "playlist.json", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return new Dictionary<string, TrackMetadata>(StringComparer.OrdinalIgnoreCase);
        }

        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("tracks", out var tracks)
            || tracks.ValueKind != JsonValueKind.Array)
        {
            return new Dictionary<string, TrackMetadata>(StringComparer.OrdinalIgnoreCase);
        }

        var metadata = new Dictionary<string, TrackMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in tracks.EnumerateArray())
        {
            var src = GetJsonString(item, "src") ?? GetJsonString(item, "url");
            if (string.IsNullOrWhiteSpace(src))
            {
                continue;
            }

            var fileName = CleanFileName(src);
            metadata[fileName] = new TrackMetadata(
                CleanForDisplay(GetJsonString(item, "title"), 200),
                CleanForDisplay(GetJsonString(item, "artist"), 200),
                CleanForDisplay(GetJsonString(item, "attribution"), 500),
                CleanForDisplay(GetJsonString(item, "source") ?? GetJsonString(item, "sourceUrl"), 1000),
                CleanForDisplay(GetJsonString(item, "description"), 1000));
        }

        return metadata;
    }

    private static IReadOnlyList<TrackMetadata> ReadSongsTextMetadata(ZipArchive archive)
    {
        var entry = archive.Entries.FirstOrDefault(item =>
            string.Equals(item.Name, "Songs.txt", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return [];
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var blocks = reader.ReadToEnd()
            .Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tracks = new List<TrackMetadata>();
        foreach (var lines in blocks.Select(block =>
                     block.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .ToArray()))
        {
            var musicLine = lines.FirstOrDefault(line => line.StartsWith("Music track:", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(musicLine))
            {
                continue;
            }

            var trackCredit = musicLine["Music track:".Length..].Trim();
            var title = trackCredit;
            var artist = string.Empty;
            var byIndex = title.LastIndexOf(" by ", StringComparison.OrdinalIgnoreCase);
            if (byIndex > 0)
            {
                artist = title[(byIndex + 4)..].Trim();
                title = title[..byIndex].Trim();
            }

            var source = lines.FirstOrDefault(line => line.StartsWith("Source:", StringComparison.OrdinalIgnoreCase));
            var description = lines.FirstOrDefault(line =>
                !line.StartsWith("Music track:", StringComparison.OrdinalIgnoreCase)
                && !line.StartsWith("Source:", StringComparison.OrdinalIgnoreCase));
            tracks.Add(new TrackMetadata(
                CleanForDisplay(title, 200),
                CleanForDisplay(artist, 200),
                CleanForDisplay(trackCredit, 500),
                CleanForDisplay(source?["Source:".Length..], 1000),
                CleanForDisplay(description, 1000)));
        }

        return tracks;
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private async Task<int?> GetWidgetIdAsync(SqlConnection conn, SqlTransaction tx, CancellationToken ct)
    {
        const string sql = @"
SELECT widget_id
FROM omp_portal.widgets
WHERE widget_key = @widget_key;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@widget_key", SqlDbType.NVarChar, 200).Value = WidgetKey;
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null || result == DBNull.Value
            ? null
            : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private async Task<MusicPlayerWidgetDataDocument> ReadDocumentAsync(
        SqlConnection conn,
        SqlTransaction? tx,
        CancellationToken ct)
    {
        const string sql = @"
SELECT wd.json_data
FROM omp_portal.widget_data wd
INNER JOIN omp_portal.widgets w ON w.widget_id = wd.widget_id
WHERE w.widget_key = @widget_key
  AND wd.data_key = @data_key;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@widget_key", SqlDbType.NVarChar, 200).Value = WidgetKey;
        cmd.Parameters.Add("@data_key", SqlDbType.NVarChar, 128).Value = DataKey;
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrWhiteSpace(json))
        {
            return new MusicPlayerWidgetDataDocument();
        }

        try
        {
            var document = JsonSerializer.Deserialize<MusicPlayerWidgetDataDocument>(json, JsonOptions)
                ?? new MusicPlayerWidgetDataDocument();
            document.Format = DocumentFormat;
            document.Tracks ??= [];
            return document;
        }
        catch (JsonException)
        {
            return new MusicPlayerWidgetDataDocument();
        }
    }

    private static async Task<IReadOnlyDictionary<long, MusicPlayerMediaRow>> GetEnabledMediaMapAsync(
        SqlConnection conn,
        IEnumerable<long> binaryDataIds,
        CancellationToken ct)
    {
        var ids = binaryDataIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<long, MusicPlayerMediaRow>();
        }

        var parameters = ids
            .Select((_, index) => $"@id{index.ToString(CultureInfo.InvariantCulture)}")
            .ToArray();
        var sql = $@"
SELECT binary_data_id,
       file_name,
       content_type
FROM omp_portal.widget_binary_data
WHERE owner_ref = @owner_ref
  AND is_enabled = 1
  AND binary_data_id IN ({string.Join(", ", parameters)});";

        var media = new Dictionary<long, MusicPlayerMediaRow>();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@owner_ref", SqlDbType.NVarChar, 128).Value = OwnerRef;
        for (var i = 0; i < ids.Length; i++)
        {
            cmd.Parameters.Add(parameters[i], SqlDbType.BigInt).Value = ids[i];
        }

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            media[rdr.GetInt64(0)] = new MusicPlayerMediaRow(rdr.GetString(1), rdr.GetString(2));
        }

        return media;
    }

    private static async Task<long?> FindExistingBinaryDataIdAsync(
        SqlConnection conn,
        SqlTransaction tx,
        byte[] hash,
        long contentLength,
        string fileName,
        CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1) binary_data_id
FROM omp_portal.widget_binary_data
WHERE owner_ref = @owner_ref
  AND content_hash = @content_hash
  AND content_length = @content_length
  AND file_name = @file_name
  AND is_enabled = 1
ORDER BY binary_data_id;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@owner_ref", SqlDbType.NVarChar, 128).Value = OwnerRef;
        cmd.Parameters.Add("@content_hash", SqlDbType.VarBinary, 32).Value = hash;
        cmd.Parameters.Add("@content_length", SqlDbType.BigInt).Value = contentLength;
        cmd.Parameters.Add("@file_name", SqlDbType.NVarChar, 260).Value = fileName;
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null || result == DBNull.Value
            ? null
            : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<long> InsertBinaryDataAsync(
        SqlConnection conn,
        SqlTransaction tx,
        ImportedTrack track,
        byte[] hash,
        int? userId,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO omp_portal.widget_binary_data
(
    owner_ref,
    file_name,
    content_type,
    content_length,
    content_hash,
    data_value,
    is_enabled,
    created_by_user_id,
    created_at,
    updated_at
)
OUTPUT INSERTED.binary_data_id
VALUES
(
    @owner_ref,
    @file_name,
    @content_type,
    @content_length,
    @content_hash,
    @data_value,
    1,
    @created_by_user_id,
    SYSUTCDATETIME(),
    SYSUTCDATETIME()
);";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@owner_ref", SqlDbType.NVarChar, 128).Value = OwnerRef;
        cmd.Parameters.Add("@file_name", SqlDbType.NVarChar, 260).Value = track.FileName;
        cmd.Parameters.Add("@content_type", SqlDbType.NVarChar, 128).Value = "audio/mpeg";
        cmd.Parameters.Add("@content_length", SqlDbType.BigInt).Value = track.Bytes.LongLength;
        cmd.Parameters.Add("@content_hash", SqlDbType.VarBinary, 32).Value = hash;
        cmd.Parameters.Add("@data_value", SqlDbType.VarBinary, -1).Value = track.Bytes;
        cmd.Parameters.Add("@created_by_user_id", SqlDbType.Int).Value = userId.HasValue ? userId.Value : DBNull.Value;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task UpsertDocumentAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int widgetId,
        MusicPlayerWidgetDataDocument document,
        CancellationToken ct)
    {
        const string sql = @"
MERGE omp_portal.widget_data AS target
USING (SELECT @widget_id AS widget_id, @data_key AS data_key) AS source
ON target.widget_id = source.widget_id
AND target.data_key = source.data_key
WHEN MATCHED THEN
    UPDATE SET json_data = @json_data,
               updated_at = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(widget_id, data_key, json_data, updated_at)
    VALUES(@widget_id, @data_key, @json_data, SYSUTCDATETIME());";

        document.Format = DocumentFormat;
        document.Tracks = document.Tracks
            .GroupBy(track => track.BinaryDataId)
            .Select(group => group.Last())
            .ToList();

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@widget_id", SqlDbType.Int).Value = widgetId;
        cmd.Parameters.Add("@data_key", SqlDbType.NVarChar, 128).Value = DataKey;
        cmd.Parameters.Add("@json_data", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(document, JsonOptions);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static bool IsMp3Upload(IFormFile file)
        => file is not null
        && file.Length > 0
        && (string.Equals(file.ContentType, "audio/mpeg", StringComparison.OrdinalIgnoreCase)
            || file.FileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase));

    private static async Task<byte[]> ReadUploadBytesAsync(IFormFile file, long maxBytes, CancellationToken ct)
    {
        if (file.Length > maxBytes)
        {
            throw new InvalidOperationException($"The MP3 file exceeds the limit of {maxBytes.ToString(CultureInfo.InvariantCulture)} bytes.");
        }

        await using var stream = file.OpenReadStream();
        return await ReadStreamBytesAsync(stream, maxBytes, ct);
    }

    private static async Task<byte[]> ReadStreamBytesAsync(Stream stream, long maxBytes, CancellationToken ct)
    {
        await using var target = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                throw new InvalidOperationException($"The uploaded file exceeds the limit of {maxBytes.ToString(CultureInfo.InvariantCulture)} bytes.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return target.ToArray();
    }

    private static string CleanFileName(string value)
    {
        var fileName = Path.GetFileName(value.Replace('\\', '/'));
        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        var cleaned = string.Concat(fileName.Where(ch => !invalidFileNameChars.Contains(ch))).Trim();
        return string.IsNullOrWhiteSpace(cleaned)
            ? "track.mp3"
            : cleaned.Length > 260 ? cleaned[..260] : cleaned;
    }

    private static string GetFileStem(string value)
    {
        var stem = Path.GetFileNameWithoutExtension(CleanFileName(value)).Trim();
        return string.IsNullOrWhiteSpace(stem) ? "Track" : stem;
    }

    private static string? CleanForDisplay(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string NormalizeMatchText(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.ToLowerInvariant().Where(char.IsLetterOrDigit))
        {
            builder.Append(ch);
        }

        return builder.ToString();
    }

    private sealed record TrackMetadata(
        string? Title,
        string? Artist,
        string? Attribution,
        string? Source,
        string? Description);

    private sealed record ImportedTrack(
        string Title,
        string Artist,
        string Attribution,
        string Source,
        string Description,
        string FileName,
        byte[] Bytes);

    private sealed record MusicPlayerMediaRow(string FileName, string ContentType);

    private sealed class MusicPlayerWidgetDataDocument
    {
        public string Format { get; set; } = DocumentFormat;

        public List<MusicPlayerTrackDocumentItem> Tracks { get; set; } = [];
    }

    private sealed class MusicPlayerTrackDocumentItem
    {
        public long BinaryDataId { get; set; }

        public string Title { get; set; } = string.Empty;

        public string Artist { get; set; } = string.Empty;

        public string Attribution { get; set; } = string.Empty;

        public string Source { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public string FileName { get; set; } = string.Empty;
    }
}

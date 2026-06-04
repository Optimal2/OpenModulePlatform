using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Web.Shared.Services;
using System.Data;

namespace OpenModulePlatform.Portal.Services;

public sealed class UserProfileImageService
{
    public const int MaxProfileImageBytes = 2 * 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, string> ContentTypesByExtension =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".webp"] = "image/webp"
        };

    private readonly SqlConnectionFactory _db;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<UserProfileImageService> _log;

    public UserProfileImageService(
        SqlConnectionFactory db,
        IWebHostEnvironment environment,
        ILogger<UserProfileImageService> log)
    {
        _db = db;
        _environment = environment;
        _log = log;
    }

    public async Task<UserProfileImage?> GetProfileImageAsync(int userId, CancellationToken ct)
    {
        if (userId <= 0)
        {
            return null;
        }

        const string sql = @"
SELECT profile_image_file_name,
       profile_image_storage_key
FROM omp.users
WHERE user_id = @user_id
  AND account_status = 1;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        var fileName = rdr.IsDBNull(0) ? null : rdr.GetString(0);
        var storageKey = rdr.IsDBNull(1) ? null : rdr.GetString(1);
        return string.IsNullOrWhiteSpace(storageKey)
            ? null
            : new UserProfileImage(fileName, storageKey);
    }

    public async Task<UserProfileImageFile?> GetProfileImageFileAsync(int userId, CancellationToken ct)
    {
        var image = await GetProfileImageAsync(userId, ct);
        if (image is null || !TryResolveStoragePath(image.StorageKey, out var path))
        {
            return null;
        }

        if (!File.Exists(path))
        {
            return null;
        }

        var contentType = GetContentType(image.FileName) ?? GetContentType(image.StorageKey);
        if (contentType is null)
        {
            return null;
        }

        return new UserProfileImageFile(
            image.FileName ?? image.StorageKey,
            contentType,
            await File.ReadAllBytesAsync(path, ct));
    }

    public async Task<ProfileImageSaveResult> SaveProfileImageAsync(int userId, IFormFile? file, CancellationToken ct)
    {
        if (userId <= 0)
        {
            return ProfileImageSaveResult.UserNotFound;
        }

        if (file is null || file.Length == 0)
        {
            return ProfileImageSaveResult.MissingFile;
        }

        if (file.Length > MaxProfileImageBytes)
        {
            return ProfileImageSaveResult.TooLarge;
        }

        var extension = Path.GetExtension(file.FileName);
        if (!ContentTypesByExtension.TryGetValue(extension, out var expectedContentType)
            || !string.Equals(NormalizeContentType(file.ContentType), expectedContentType, StringComparison.OrdinalIgnoreCase)
            || !await HasExpectedSignatureAsync(file, expectedContentType, ct))
        {
            return ProfileImageSaveResult.InvalidType;
        }

        var storageKey = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        var safeFileName = SanitizeDisplayFileName(file.FileName, storageKey);
        var path = GetStoragePathForNewKey(storageKey);

        await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await file.CopyToAsync(target, ct);
        }

        string? oldStorageKey = null;
        try
        {
            const string sql = @"
DECLARE @old_storage_key nvarchar(260);

SELECT @old_storage_key = profile_image_storage_key
FROM omp.users
WHERE user_id = @user_id
  AND account_status = 1;

UPDATE omp.users
SET profile_image_file_name = @file_name,
    profile_image_storage_key = @storage_key,
    updated_at = SYSUTCDATETIME()
WHERE user_id = @user_id
  AND account_status = 1;

SELECT @old_storage_key;";

            await using var conn = _db.Create();
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
            cmd.Parameters.Add("@file_name", SqlDbType.NVarChar, 260).Value = safeFileName;
            cmd.Parameters.Add("@storage_key", SqlDbType.NVarChar, 260).Value = storageKey;

            oldStorageKey = await cmd.ExecuteScalarAsync(ct) as string;
            if ((await GetProfileImageAsync(userId, ct))?.StorageKey != storageKey)
            {
                DeleteStorageKeyBestEffort(storageKey);
                return ProfileImageSaveResult.UserNotFound;
            }
        }
        catch
        {
            DeleteStorageKeyBestEffort(storageKey);
            throw;
        }

        DeleteStorageKeyBestEffort(oldStorageKey);
        return ProfileImageSaveResult.Saved;
    }

    public async Task<bool> RemoveProfileImageAsync(int userId, CancellationToken ct)
    {
        if (userId <= 0)
        {
            return false;
        }

        const string sql = @"
DECLARE @old_storage_key nvarchar(260);

SELECT @old_storage_key = profile_image_storage_key
FROM omp.users
WHERE user_id = @user_id
  AND account_status = 1;

UPDATE omp.users
SET profile_image_file_name = NULL,
    profile_image_storage_key = NULL,
    updated_at = SYSUTCDATETIME()
WHERE user_id = @user_id
  AND account_status = 1;

SELECT @old_storage_key;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;

        var oldStorageKey = await cmd.ExecuteScalarAsync(ct) as string;
        DeleteStorageKeyBestEffort(oldStorageKey);
        return true;
    }

    private static string NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return string.Empty;
        }

        var separatorIndex = contentType.IndexOf(';', StringComparison.Ordinal);
        return (separatorIndex >= 0 ? contentType[..separatorIndex] : contentType).Trim();
    }

    private static async Task<bool> HasExpectedSignatureAsync(
        IFormFile file,
        string expectedContentType,
        CancellationToken ct)
    {
        var header = new byte[12];
        await using var stream = file.OpenReadStream();
        var read = await stream.ReadAsync(header.AsMemory(0, header.Length), ct);

        return expectedContentType switch
        {
            "image/png" => read >= 8
                && header[0] == 0x89
                && header[1] == 0x50
                && header[2] == 0x4E
                && header[3] == 0x47
                && header[4] == 0x0D
                && header[5] == 0x0A
                && header[6] == 0x1A
                && header[7] == 0x0A,
            "image/jpeg" => read >= 3
                && header[0] == 0xFF
                && header[1] == 0xD8
                && header[2] == 0xFF,
            "image/webp" => read >= 12
                && header[0] == 0x52
                && header[1] == 0x49
                && header[2] == 0x46
                && header[3] == 0x46
                && header[8] == 0x57
                && header[9] == 0x45
                && header[10] == 0x42
                && header[11] == 0x50,
            _ => false
        };
    }

    private static string SanitizeDisplayFileName(string? fileName, string fallback)
    {
        var cleaned = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return fallback;
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            cleaned = cleaned.Replace(invalid, '_');
        }

        return cleaned.Length <= 260 ? cleaned : cleaned[^260..];
    }

    private static string? GetContentType(string? fileName)
        => !string.IsNullOrWhiteSpace(fileName)
           && ContentTypesByExtension.TryGetValue(Path.GetExtension(fileName), out var contentType)
            ? contentType
            : null;

    private string GetStoragePathForNewKey(string storageKey)
    {
        var root = GetStorageRoot();
        Directory.CreateDirectory(root);
        return Path.Join(root, Path.GetFileName(storageKey));
    }

    private bool TryResolveStoragePath(string? storageKey, out string path)
    {
        path = string.Empty;
        var fileName = Path.GetFileName(storageKey);
        if (string.IsNullOrWhiteSpace(storageKey)
            || string.IsNullOrWhiteSpace(fileName)
            || !string.Equals(fileName, storageKey, StringComparison.Ordinal)
            || !ContentTypesByExtension.ContainsKey(Path.GetExtension(fileName)))
        {
            return false;
        }

        path = Path.Join(GetStorageRoot(), fileName);
        return true;
    }

    private string GetStorageRoot()
        => Path.Join(_environment.ContentRootPath, "App_Data", "ProfileImages");

    private void DeleteStorageKeyBestEffort(string? storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey) || !TryResolveStoragePath(storageKey, out var path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _log.LogWarning(ex, "Failed to delete old profile image storage key {StorageKey}.", storageKey);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.LogWarning(ex, "Failed to delete old profile image storage key {StorageKey}.", storageKey);
        }
    }
}

public sealed record UserProfileImage(string? FileName, string StorageKey);

public sealed record UserProfileImageFile(string FileName, string ContentType, byte[] Data);

public enum ProfileImageSaveResult
{
    Saved,
    MissingFile,
    InvalidType,
    TooLarge,
    UserNotFound
}

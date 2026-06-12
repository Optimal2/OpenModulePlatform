using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Web.Shared.Services;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;

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
        if (image is null || !IsValidStorageKey(image.StorageKey))
        {
            return null;
        }

        var ownerRef = BuildOwnerRef(userId, image.StorageKey);
        const string sql = @"
SELECT TOP (1)
       file_name,
       content_type,
       data_value
FROM omp_portal.widget_binary_data
WHERE owner_ref = @owner_ref
  AND is_enabled = 1
ORDER BY binary_data_id DESC;";

        try
        {
            await using var conn = _db.Create();
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add("@owner_ref", SqlDbType.NVarChar, 128).Value = ownerRef;

            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            if (await rdr.ReadAsync(ct))
            {
                var fileName = rdr.GetString(0);
                var contentType = rdr.GetString(1);
                var data = await rdr.GetFieldValueAsync<byte[]>(2, ct);
                return new UserProfileImageFile(fileName, contentType, data);
            }
        }
        catch (SqlException ex) when (IsProfileImageSchemaError(ex))
        {
            _log.LogWarning(
                ex,
                "Profile image binary storage is not available while reading image {StorageKey} for user {UserId}.",
                image.StorageKey,
                userId);
        }

        return await GetLegacyProfileImageFileAsync(image, ct);
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
        byte[] imageBytes;
        try
        {
            imageBytes = await ReadFileBytesAsync(file, ct);
        }
        catch (IOException ex) when (!ct.IsCancellationRequested)
        {
            _log.LogError(ex, "Failed to read uploaded profile image for user {UserId}.", userId);
            return ProfileImageSaveResult.StorageUnavailable;
        }
        catch (UnauthorizedAccessException ex) when (!ct.IsCancellationRequested)
        {
            _log.LogError(ex, "Failed to read uploaded profile image for user {UserId}.", userId);
            return ProfileImageSaveResult.StorageUnavailable;
        }

        var contentHash = SHA256.HashData(imageBytes);

        string? oldStorageKey = null;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            oldStorageKey = await GetCurrentStorageKeyAsync(conn, tx, userId, ct);
            var updated = await UpdateProfileImagePointerAsync(conn, tx, userId, safeFileName, storageKey, ct);
            if (!updated)
            {
                await tx.RollbackAsync(ct);
                return ProfileImageSaveResult.UserNotFound;
            }

            await InsertProfileImageBinaryDataAsync(
                conn,
                tx,
                userId,
                BuildOwnerRef(userId, storageKey),
                safeFileName,
                expectedContentType,
                imageBytes,
                contentHash,
                ct);

            await DisableProfileImageBinaryDataAsync(conn, tx, userId, oldStorageKey, ct);
            await tx.CommitAsync(ct);
        }
        catch (SqlException ex) when (IsProfileImageSchemaError(ex))
        {
            await RollbackBestEffortAsync(tx, ct);
            _log.LogError(
                ex,
                "The core profile image schema is missing while saving a profile image for user {UserId}.",
                userId);
            return ProfileImageSaveResult.SchemaUnavailable;
        }
        catch
        {
            await RollbackBestEffortAsync(tx, ct);
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
        try
        {
            await DisableProfileImageBinaryDataAsync(conn, tx: null, userId, oldStorageKey, ct);
        }
        catch (SqlException ex) when (IsProfileImageSchemaError(ex))
        {
            _log.LogWarning(ex, "Profile image binary storage is not available while removing an image for user {UserId}.", userId);
        }

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

    private static async Task<byte[]> ReadFileBytesAsync(IFormFile file, CancellationToken ct)
    {
        await using var source = file.OpenReadStream();
        using var target = new MemoryStream((int)file.Length);
        await source.CopyToAsync(target, ct);
        return target.ToArray();
    }

    private static async Task<string?> GetCurrentStorageKeyAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int userId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT profile_image_storage_key
FROM omp.users WITH (UPDLOCK)
WHERE user_id = @user_id
  AND account_status = 1;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static async Task<bool> UpdateProfileImagePointerAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int userId,
        string fileName,
        string storageKey,
        CancellationToken ct)
    {
        const string sql = @"
UPDATE omp.users
SET profile_image_file_name = @file_name,
    profile_image_storage_key = @storage_key,
    updated_at = SYSUTCDATETIME()
WHERE user_id = @user_id
  AND account_status = 1;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        cmd.Parameters.Add("@file_name", SqlDbType.NVarChar, 260).Value = fileName;
        cmd.Parameters.Add("@storage_key", SqlDbType.NVarChar, 260).Value = storageKey;
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private static async Task InsertProfileImageBinaryDataAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int userId,
        string ownerRef,
        string fileName,
        string contentType,
        byte[] bytes,
        byte[] hash,
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
        cmd.Parameters.Add("@owner_ref", SqlDbType.NVarChar, 128).Value = ownerRef;
        cmd.Parameters.Add("@file_name", SqlDbType.NVarChar, 260).Value = fileName;
        cmd.Parameters.Add("@content_type", SqlDbType.NVarChar, 128).Value = contentType;
        cmd.Parameters.Add("@content_length", SqlDbType.BigInt).Value = bytes.LongLength;
        cmd.Parameters.Add("@content_hash", SqlDbType.VarBinary, 32).Value = hash;
        cmd.Parameters.Add("@data_value", SqlDbType.VarBinary, -1).Value = bytes;
        cmd.Parameters.Add("@created_by_user_id", SqlDbType.Int).Value = userId;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task DisableProfileImageBinaryDataAsync(
        SqlConnection conn,
        SqlTransaction? tx,
        int userId,
        string? storageKey,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            return;
        }

        const string sql = @"
UPDATE omp_portal.widget_binary_data
SET is_enabled = 0,
    updated_at = SYSUTCDATETIME()
WHERE owner_ref = @owner_ref
  AND is_enabled = 1;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@owner_ref", SqlDbType.NVarChar, 128).Value = BuildOwnerRef(userId, storageKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task RollbackBestEffortAsync(SqlTransaction tx, CancellationToken ct)
    {
        try
        {
            await tx.RollbackAsync(ct);
        }
        catch (InvalidOperationException)
        {
            // The transaction may already be completed; preserve the original save failure.
        }
        catch (SqlException)
        {
            // A broken connection can prevent rollback; preserve the original save failure.
        }
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

    private static bool IsProfileImageSchemaError(SqlException ex)
    {
        foreach (SqlError error in ex.Errors)
        {
            if (error.Number == 207
                && (error.Message.Contains("profile_image_file_name", StringComparison.OrdinalIgnoreCase)
                    || error.Message.Contains("profile_image_storage_key", StringComparison.OrdinalIgnoreCase)
                    || error.Message.Contains("owner_ref", StringComparison.OrdinalIgnoreCase)
                    || error.Message.Contains("content_hash", StringComparison.OrdinalIgnoreCase)
                    || error.Message.Contains("data_value", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (error.Number == 208
                && (error.Message.Contains("omp.users", StringComparison.OrdinalIgnoreCase)
                    || error.Message.Contains("omp_portal.widget_binary_data", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetContentType(string? fileName)
        => !string.IsNullOrWhiteSpace(fileName)
           && ContentTypesByExtension.TryGetValue(Path.GetExtension(fileName), out var contentType)
            ? contentType
            : null;

    private static string BuildOwnerRef(int userId, string storageKey)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"user-profile-image:{userId}:{storageKey}");

    private static bool IsValidStorageKey(string? storageKey)
    {
        var fileName = Path.GetFileName(storageKey);
        if (string.IsNullOrWhiteSpace(storageKey)
            || string.IsNullOrWhiteSpace(fileName)
            || !string.Equals(fileName, storageKey, StringComparison.Ordinal)
            || !ContentTypesByExtension.ContainsKey(Path.GetExtension(fileName)))
        {
            return false;
        }

        return true;
    }

    private async Task<UserProfileImageFile?> GetLegacyProfileImageFileAsync(UserProfileImage image, CancellationToken ct)
    {
        if (!TryResolveStoragePath(image.StorageKey, out var path) || !File.Exists(path))
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

    private bool TryResolveStoragePath(string? storageKey, out string path)
    {
        path = string.Empty;
        if (!IsValidStorageKey(storageKey))
        {
            return false;
        }

        var fileName = Path.GetFileName(storageKey);
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
    UserNotFound,
    StorageUnavailable,
    SchemaUnavailable
}

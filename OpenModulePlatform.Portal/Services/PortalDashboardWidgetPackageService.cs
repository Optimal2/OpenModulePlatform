// File: OpenModulePlatform.Portal/Services/PortalDashboardWidgetPackageService.cs
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Web.Shared.Services;
using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenModulePlatform.Portal.Services;

/// <summary>
/// Imports and exports portable dashboard widget definition documents.
/// </summary>
public sealed class PortalDashboardWidgetPackageService
{
    public const string FormatName = "omp.portal.dashboard.widgets";

    private const int FormatVersion = 1;
    private const string LegacyWidgetVersion = "0.0.0";
    private const int MaxJsonBytes = 1024 * 1024;
    private const int MaxPayloadLength = 4000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    private readonly SqlConnectionFactory _db;

    public PortalDashboardWidgetPackageService(SqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<DashboardWidgetAdminRow>> GetWidgetsAsync(
        string? moduleKey,
        CancellationToken ct)
    {
        const string sql = @"
SELECT w.widget_id,
       w.widget_key,
       w.title,
       w.description,
       w.widget_type,
       w.payload,
       w.module_key,
       w.author,
       w.widget_version,
       w.is_enabled,
       w.modified_at,
       p.Name AS permission_name,
       r.Name AS role_name
FROM omp_portal.widgets w
LEFT JOIN omp_portal.widget_permissions wp ON wp.widget_id = w.widget_id
LEFT JOIN omp.Permissions p ON p.PermissionId = wp.permission_id
LEFT JOIN omp.Roles r ON r.RoleId = wp.role_id
WHERE @module_key IS NULL
   OR w.module_key = @module_key
ORDER BY w.module_key,
         w.widget_key,
         w.title,
         w.widget_id,
         p.Name,
         r.Name;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@module_key", SqlDbType.NVarChar, 100).Value =
            string.IsNullOrWhiteSpace(moduleKey) ? DBNull.Value : moduleKey.Trim();

        var rows = new Dictionary<int, DashboardWidgetAdminRow>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var widgetId = rdr.GetInt32(0);
            if (!rows.TryGetValue(widgetId, out var row))
            {
                row = new DashboardWidgetAdminRow
                {
                    WidgetId = widgetId,
                    WidgetKey = rdr.IsDBNull(1) ? string.Empty : rdr.GetString(1),
                    Title = rdr.GetString(2),
                    Description = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                    WidgetType = rdr.GetString(4),
                    Payload = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                    ModuleKey = rdr.IsDBNull(6) ? string.Empty : rdr.GetString(6),
                    Author = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                    WidgetVersion = rdr.IsDBNull(8) ? LegacyWidgetVersion : rdr.GetString(8),
                    IsEnabled = rdr.GetBoolean(9),
                    ModifiedUtc = rdr.GetDateTime(10)
                };

                row.WidgetKey = string.IsNullOrWhiteSpace(row.WidgetKey)
                    ? CreateFallbackWidgetKey(row)
                    : row.WidgetKey;
                rows.Add(widgetId, row);
            }

            if (!rdr.IsDBNull(11))
            {
                AddDistinct(row.PermissionNames, rdr.GetString(11));
            }

            if (!rdr.IsDBNull(12))
            {
                AddDistinct(row.RoleNames, rdr.GetString(12));
            }
        }

        return rows.Values.ToArray();
    }

    public async Task SetWidgetEnabledAsync(
        int widgetId,
        bool isEnabled,
        CancellationToken ct)
    {
        const string sql = @"
UPDATE omp_portal.widgets
SET is_enabled = @is_enabled,
    modified_at = SYSUTCDATETIME()
WHERE widget_id = @widget_id;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@widget_id", SqlDbType.Int).Value = widgetId;
        cmd.Parameters.Add("@is_enabled", SqlDbType.Bit).Value = isEnabled;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateWidgetDescriptionAsync(
        int widgetId,
        string? description,
        CancellationToken ct)
    {
        var cleanedDescription = CleanOptionalText(description, "description", 1000);
        const string sql = @"
UPDATE omp_portal.widgets
SET description = @description,
    modified_at = SYSUTCDATETIME()
WHERE widget_id = @widget_id;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@widget_id", SqlDbType.Int).Value = widgetId;
        cmd.Parameters.Add("@description", SqlDbType.NVarChar, 1000).Value =
            cleanedDescription is null ? DBNull.Value : cleanedDescription;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(byte[] Content, string FileName, string PackageVersion)> ExportAsync(
        string? moduleKey,
        CancellationToken ct)
    {
        var rows = await GetWidgetsAsync(moduleKey, ct);
        var trimmedModuleKey = string.IsNullOrWhiteSpace(moduleKey) ? null : moduleKey.Trim();
        return ExportRows(rows, trimmedModuleKey, trimmedModuleKey ?? "all");
    }

    public async Task<(byte[] Content, string FileName, string PackageVersion)> ExportWidgetAsync(
        int widgetId,
        CancellationToken ct)
    {
        var rows = await GetWidgetsAsync(null, ct);
        var row = rows.FirstOrDefault(item => item.WidgetId == widgetId)
            ?? throw new InvalidOperationException("The selected dashboard widget was not found.");

        return ExportRows([row], null, row.WidgetKey);
    }

    public async Task<(byte[] Content, string FileName, string PackageVersion)> ExportWidgetsAsync(
        IReadOnlyList<int> widgetIds,
        CancellationToken ct)
    {
        if (widgetIds.Count == 0)
        {
            return await ExportAsync(null, ct);
        }

        var selectedIds = widgetIds.ToHashSet();
        var rows = (await GetWidgetsAsync(null, ct))
            .Where(row => selectedIds.Contains(row.WidgetId))
            .ToArray();
        if (rows.Length == 0)
        {
            throw new InvalidOperationException("No selected dashboard widgets were found.");
        }

        return ExportRows(rows, null, rows.Length == 1 ? rows[0].WidgetKey : "selected");
    }

    private static (byte[] Content, string FileName, string PackageVersion) ExportRows(
        IReadOnlyList<DashboardWidgetAdminRow> rows,
        string? documentModuleKey,
        string fileNameKey)
    {
        var packageVersion = rows
            .Select(static row => string.IsNullOrWhiteSpace(row.WidgetVersion) ? LegacyWidgetVersion : row.WidgetVersion)
            .OrderByDescending(static version => version, WidgetVersionComparer.Instance)
            .FirstOrDefault() ?? LegacyWidgetVersion;
        var document = new DashboardWidgetDocument
        {
            Format = FormatName,
            FormatVersion = FormatVersion,
            PackageVersion = packageVersion,
            ModuleKey = documentModuleKey,
            ExportedAtUtc = DateTime.UtcNow,
            Widgets = rows
                .OrderBy(static row => row.ModuleKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static row => row.WidgetKey, StringComparer.OrdinalIgnoreCase)
                .Select(row => new DashboardWidgetDocumentItem
                {
                    WidgetKey = row.WidgetKey,
                    WidgetVersion = string.IsNullOrWhiteSpace(row.WidgetVersion) ? LegacyWidgetVersion : row.WidgetVersion,
                    Title = row.Title,
                    Description = row.Description,
                    WidgetType = row.WidgetType,
                    Payload = row.Payload,
                    ModuleKey = GetExportItemModuleKey(row.ModuleKey, documentModuleKey),
                    Author = row.Author,
                    PermissionNames = row.PermissionNames
                        .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    RoleNames = row.RoleNames
                        .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                })
                .ToList()
        };

        var json = JsonSerializer.Serialize(document, JsonOptions);
        var fileName = $"omp-dashboard-widgets-{SanitizeFileName(fileNameKey)}-{SanitizeFileName(packageVersion)}-{DateTime.UtcNow:yyyyMMddHHmmss}.json";
        return (Encoding.UTF8.GetBytes(json), fileName, packageVersion);
    }

    public async Task<DashboardWidgetImportResult> ImportAsync(
        Stream stream,
        string sourceName,
        bool replaceExistingWidgets,
        bool quickImport,
        CancellationToken ct)
    {
        if (!stream.CanRead)
        {
            throw new InvalidOperationException("The dashboard widget JSON stream is not readable.");
        }

        var json = await ReadJsonWithSizeLimitAsync(stream, ct);
        var document = JsonSerializer.Deserialize<DashboardWidgetDocument>(json, JsonOptions)
            ?? throw new InvalidOperationException("The dashboard widget JSON file is empty.");
        ValidateDocument(document, sourceName);

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            var result = new DashboardWidgetImportResult();
            // The database work stays sequential so import results and transaction
            // behavior remain deterministic, while normalization is a pure projection.
            foreach (var normalized in document.Widgets.Select(item => Normalize(document, item)))
            {
                var permissionIds = await ResolvePermissionIdsAsync(conn, tx, normalized.PermissionNames, ct);
                var roleIds = await ResolveRoleIdsAsync(conn, tx, normalized.RoleNames, ct);
                var existing = await FindWidgetAsync(conn, tx, normalized, ct);
                if (existing is not null)
                {
                    var versionComparison = CompareWidgetVersions(normalized.WidgetVersion, existing.WidgetVersion);
                    if (ShouldSkipExistingWidget(existing, normalized, quickImport, replaceExistingWidgets))
                    {
                        result.SkippedCount++;
                        continue;
                    }

                    if (!replaceExistingWidgets && versionComparison == 0)
                    {
                        if (DashboardWidgetMatches(existing, normalized, permissionIds, roleIds))
                        {
                            result.SkippedCount++;
                            continue;
                        }

                        throw new InvalidOperationException(
                            $"Dashboard widget '{normalized.WidgetKey}' already exists with version {existing.WidgetVersion}, but the imported content is different. Use a new widgetVersion, or disable quick import and enable widget replacement for an intentional rollback or repair.");
                    }

                    await UpdateWidgetAsync(conn, tx, existing.WidgetId, normalized, ct);
                    await ReplacePermissionRowsAsync(conn, tx, existing.WidgetId, permissionIds, roleIds, ct);
                    result.UpdatedCount++;
                    result.PermissionRowCount += permissionIds.Count + roleIds.Count;
                }
                else
                {
                    var widgetId = await InsertWidgetAsync(conn, tx, normalized, ct);
                    await ReplacePermissionRowsAsync(conn, tx, widgetId, permissionIds, roleIds, ct);
                    result.CreatedCount++;
                    result.PermissionRowCount += permissionIds.Count + roleIds.Count;
                }
            }

            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static void ValidateDocument(DashboardWidgetDocument document, string sourceName)
    {
        if (!string.Equals(document.Format, FormatName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Dashboard widget JSON '{sourceName}' must contain format '{FormatName}'.");
        }

        if (document.FormatVersion != FormatVersion)
        {
            throw new InvalidOperationException($"Dashboard widget JSON formatVersion must be {FormatVersion}.");
        }

        if (document.Widgets.Count == 0)
        {
            throw new InvalidOperationException("Dashboard widget JSON must contain at least one widget.");
        }

        _ = document.Widgets
            .Select(item => Normalize(document, item))
            .ToArray();
    }

    private static async Task<string> ReadJsonWithSizeLimitAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var scratch = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(scratch.AsMemory(0, scratch.Length), ct);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaxJsonBytes)
            {
                throw new InvalidOperationException($"The dashboard widget JSON exceeds the limit of {MaxJsonBytes} bytes.");
            }

            buffer.Write(scratch, 0, read);
        }

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct);
    }

    private static NormalizedDashboardWidget Normalize(
        DashboardWidgetDocument document,
        DashboardWidgetDocumentItem item)
    {
        var widgetKey = CleanRequiredKey(item.WidgetKey, "widgetKey", 200);
        var widgetVersion = CleanVersionText(item.WidgetVersion ?? document.PackageVersion, "widgetVersion") ?? LegacyWidgetVersion;
        var title = CleanRequiredText(item.Title, "title", 200);
        var description = CleanOptionalText(item.Description, "description", 1000);
        var widgetType = CleanRequiredKey(item.WidgetType, "widgetType", 50);
        var moduleKey = CleanOptionalKey(item.ModuleKey ?? document.ModuleKey, "moduleKey", 100);
        var payload = CleanOptionalText(item.Payload, "payload", MaxPayloadLength);
        var author = CleanOptionalText(item.Author ?? document.Author, "author", 200);
        if (item.PermissionNames is null || item.RoleNames is null)
        {
            throw new InvalidOperationException(
                "Each dashboard widget must contain permissionNames and roleNames arrays. Use empty arrays for unrestricted widgets.");
        }

        var permissionNames = CleanDistinctNames(item.PermissionNames, "permissionNames");
        var roleNames = CleanDistinctNames(item.RoleNames, "roleNames");

        return new NormalizedDashboardWidget(
            widgetKey,
            widgetVersion,
            title,
            description,
            widgetType,
            payload,
            moduleKey,
            author,
            permissionNames,
            roleNames);
    }

    private static async Task<DashboardWidgetSnapshot?> FindWidgetAsync(
        SqlConnection conn,
        SqlTransaction tx,
        NormalizedDashboardWidget widget,
        CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1) widget_id
FROM
(
    SELECT widget_id,
           0 AS match_priority
    FROM omp_portal.widgets
    WHERE widget_key = @widget_key
    UNION ALL
    SELECT widget_id,
           1 AS match_priority
    FROM omp_portal.widgets
    WHERE ((@module_key IS NULL AND module_key IS NULL) OR module_key = @module_key)
      AND widget_key IS NULL
      AND title = @title
      AND widget_type = @widget_type
) AS matches
ORDER BY match_priority,
         widget_id;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        AddIdentityParameters(cmd, widget);
        var value = await cmd.ExecuteScalarAsync(ct);
        if (value is null || value == DBNull.Value)
        {
            return null;
        }

        return await ReadWidgetSnapshotAsync(
            conn,
            tx,
            Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture),
            ct);
    }

    private static async Task<DashboardWidgetSnapshot> ReadWidgetSnapshotAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int widgetId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT w.widget_id,
       w.widget_key,
       w.title,
       w.description,
       w.widget_type,
       w.payload,
       w.module_key,
       w.author,
       w.widget_version,
       p.PermissionId AS permission_id,
       r.RoleId AS role_id
FROM omp_portal.widgets w
LEFT JOIN omp_portal.widget_permissions wp ON wp.widget_id = w.widget_id
LEFT JOIN omp.Permissions p ON p.PermissionId = wp.permission_id
LEFT JOIN omp.Roles r ON r.RoleId = wp.role_id
WHERE w.widget_id = @widget_id;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@widget_id", SqlDbType.Int).Value = widgetId;
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        DashboardWidgetSnapshot? snapshot = null;
        var permissionIds = new List<int>();
        var roleIds = new List<int>();
        while (await rdr.ReadAsync(ct))
        {
            snapshot ??= new DashboardWidgetSnapshot(
                rdr.GetInt32(0),
                rdr.IsDBNull(1) ? string.Empty : rdr.GetString(1),
                rdr.GetString(2),
                rdr.IsDBNull(3) ? null : rdr.GetString(3),
                rdr.GetString(4),
                rdr.IsDBNull(5) ? null : rdr.GetString(5),
                rdr.IsDBNull(6) ? null : rdr.GetString(6),
                rdr.IsDBNull(7) ? null : rdr.GetString(7),
                rdr.IsDBNull(8) ? LegacyWidgetVersion : rdr.GetString(8),
                permissionIds,
                roleIds);

            if (!rdr.IsDBNull(9))
            {
                AddDistinct(permissionIds, rdr.GetInt32(9));
            }

            if (!rdr.IsDBNull(10))
            {
                AddDistinct(roleIds, rdr.GetInt32(10));
            }
        }

        return snapshot
            ?? throw new InvalidOperationException($"Dashboard widget {widgetId} was not found.");
    }

    private static async Task<int> InsertWidgetAsync(
        SqlConnection conn,
        SqlTransaction tx,
        NormalizedDashboardWidget widget,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO omp_portal.widgets(widget_key, title, description, widget_type, payload, module_key, author, widget_version, modified_at)
OUTPUT INSERTED.widget_id
VALUES(@widget_key, @title, @description, @widget_type, @payload, @module_key, @author, @widget_version, SYSUTCDATETIME());";

        await using var cmd = new SqlCommand(sql, conn, tx);
        AddWidgetParameters(cmd, widget);
        var value = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task UpdateWidgetAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int widgetId,
        NormalizedDashboardWidget widget,
        CancellationToken ct)
    {
        const string sql = @"
UPDATE omp_portal.widgets
SET widget_key = @widget_key,
    title = @title,
    description = @description,
    widget_type = @widget_type,
    payload = @payload,
    module_key = @module_key,
    author = @author,
    widget_version = @widget_version,
    modified_at = SYSUTCDATETIME()
WHERE widget_id = @widget_id;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@widget_id", SqlDbType.Int).Value = widgetId;
        AddWidgetParameters(cmd, widget);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ReplacePermissionRowsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int widgetId,
        IReadOnlyList<int> permissionIds,
        IReadOnlyList<int> roleIds,
        CancellationToken ct)
    {
        await using (var deleteCmd = new SqlCommand(
            "DELETE FROM omp_portal.widget_permissions WHERE widget_id = @widget_id;",
            conn,
            tx))
        {
            deleteCmd.Parameters.Add("@widget_id", SqlDbType.Int).Value = widgetId;
            await deleteCmd.ExecuteNonQueryAsync(ct);
        }

        const string insertSql = @"
INSERT INTO omp_portal.widget_permissions(widget_id, permission_id, role_id)
VALUES(@widget_id, @permission_id, @role_id);";

        foreach (var permissionId in permissionIds)
        {
            await using var cmd = new SqlCommand(insertSql, conn, tx);
            cmd.Parameters.Add("@widget_id", SqlDbType.Int).Value = widgetId;
            cmd.Parameters.Add("@permission_id", SqlDbType.Int).Value = permissionId;
            cmd.Parameters.Add("@role_id", SqlDbType.Int).Value = DBNull.Value;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var roleId in roleIds)
        {
            await using var cmd = new SqlCommand(insertSql, conn, tx);
            cmd.Parameters.Add("@widget_id", SqlDbType.Int).Value = widgetId;
            cmd.Parameters.Add("@permission_id", SqlDbType.Int).Value = DBNull.Value;
            cmd.Parameters.Add("@role_id", SqlDbType.Int).Value = roleId;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<IReadOnlyList<int>> ResolvePermissionIdsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        IReadOnlyList<string> permissionNames,
        CancellationToken ct)
        => await ResolveIdsAsync(
            conn,
            tx,
            "omp.Permissions",
            "PermissionId",
            permissionNames,
            "permission",
            ct);

    private static async Task<IReadOnlyList<int>> ResolveRoleIdsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        IReadOnlyList<string> roleNames,
        CancellationToken ct)
        => await ResolveIdsAsync(
            conn,
            tx,
            "omp.Roles",
            "RoleId",
            roleNames,
            "role",
            ct);

    private static async Task<IReadOnlyList<int>> ResolveIdsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        string tableName,
        string idColumn,
        IReadOnlyList<string> names,
        string label,
        CancellationToken ct)
    {
        var ids = new List<int>(names.Count);
        foreach (var name in names)
        {
            var sql = $"SELECT {idColumn} FROM {tableName} WHERE Name = @name;";
            await using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = name;
            var value = await cmd.ExecuteScalarAsync(ct);
            if (value is null || value == DBNull.Value)
            {
                throw new InvalidOperationException($"The dashboard widget {label} '{name}' does not exist.");
            }

            ids.Add(Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture));
        }

        return ids;
    }

    private static void AddIdentityParameters(SqlCommand cmd, NormalizedDashboardWidget widget)
    {
        cmd.Parameters.Add("@module_key", SqlDbType.NVarChar, 100).Value =
            widget.ModuleKey is null ? DBNull.Value : widget.ModuleKey;
        cmd.Parameters.Add("@widget_key", SqlDbType.NVarChar, 200).Value = widget.WidgetKey;
        cmd.Parameters.Add("@title", SqlDbType.NVarChar, 200).Value = widget.Title;
        cmd.Parameters.Add("@description", SqlDbType.NVarChar, 1000).Value =
            widget.Description is null ? DBNull.Value : widget.Description;
        cmd.Parameters.Add("@widget_type", SqlDbType.NVarChar, 50).Value = widget.WidgetType;
    }

    private static void AddWidgetParameters(SqlCommand cmd, NormalizedDashboardWidget widget)
    {
        AddIdentityParameters(cmd, widget);
        cmd.Parameters.Add("@payload", SqlDbType.NVarChar, -1).Value =
            widget.Payload is null ? DBNull.Value : widget.Payload;
        cmd.Parameters.Add("@author", SqlDbType.NVarChar, 200).Value =
            widget.Author is null ? DBNull.Value : widget.Author;
        cmd.Parameters.Add("@widget_version", SqlDbType.NVarChar, 50).Value = widget.WidgetVersion;
    }

    private static string CleanRequiredText(string? value, string propertyName, int maxLength)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"Each dashboard widget must contain {propertyName}.");
        }

        if (text.Length > maxLength)
        {
            throw new InvalidOperationException($"Dashboard widget {propertyName} must be at most {maxLength} characters.");
        }

        return text;
    }

    private static string CleanRequiredKey(string? value, string propertyName, int maxLength)
    {
        var key = CleanRequiredText(value, propertyName, maxLength);
        if (!key.All(IsPortableKeyCharacter))
        {
            throw new InvalidOperationException(
                $"Dashboard widget {propertyName} may only contain letters, digits, period, underscore, colon, or hyphen.");
        }

        return key;
    }

    private static string? CleanOptionalText(string? value, string propertyName, int maxLength)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (text.Length > maxLength)
        {
            throw new InvalidOperationException($"Dashboard widget {propertyName} must be at most {maxLength} characters.");
        }

        return text;
    }

    private static IReadOnlyList<string> CleanDistinctNames(IReadOnlyList<string>? values, string propertyName)
    {
        if (values is null)
        {
            return [];
        }

        return values
            .Select(value => CleanOptionalText(value, propertyName, 200))
            .Where(text => text is not null)
            .Select(text => text!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? CleanVersionText(string? value, string propertyName)
    {
        var version = CleanOptionalText(value, propertyName, 50);
        if (version is null)
        {
            return null;
        }

        if (!version.All(static ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '+' or '-'))
        {
            throw new InvalidOperationException(
                $"Dashboard widget {propertyName} may only contain letters, digits, period, underscore, plus, or hyphen.");
        }

        return version;
    }

    private static string CreateFallbackWidgetKey(DashboardWidgetAdminRow row)
    {
        var preferred = !string.IsNullOrWhiteSpace(row.Payload)
            ? row.Payload
            : row.Title;
        var key = new string(preferred
            .Trim()
            .ToLowerInvariant()
            .Select(ch => IsPortableKeyCharacter(ch) ? ch : '-')
            .ToArray());

        return string.IsNullOrWhiteSpace(key)
            ? $"widget-{row.WidgetId}"
            : key;
    }

    private static string? GetExportItemModuleKey(string? rowModuleKey, string? rootModuleKey)
    {
        var normalized = string.IsNullOrWhiteSpace(rowModuleKey) ? null : rowModuleKey.Trim();
        if (normalized is null || string.Equals(normalized, rootModuleKey, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return normalized;
    }

    private static string? CleanOptionalKey(string? value, string propertyName, int maxLength)
    {
        var key = CleanOptionalText(value, propertyName, maxLength);
        if (key is null)
        {
            return null;
        }

        if (!key.All(IsPortableKeyCharacter))
        {
            throw new InvalidOperationException(
                $"Dashboard widget {propertyName} may only contain letters, digits, period, underscore, colon, or hyphen.");
        }

        return key;
    }

    private static bool IsPortableKeyCharacter(char ch)
        => char.IsLetterOrDigit(ch) || ch is '.' or '_' or ':' or '-';

    private static void AddDistinct(List<string> values, string value)
    {
        if (!values.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            values.Add(value);
        }
    }

    private static void AddDistinct(List<int> values, int value)
    {
        if (!values.Contains(value))
        {
            values.Add(value);
        }
    }

    private static bool ShouldSkipExistingWidget(
        DashboardWidgetSnapshot existing,
        NormalizedDashboardWidget imported,
        bool quickImport,
        bool replaceExistingWidgets)
    {
        if (replaceExistingWidgets)
        {
            return false;
        }

        var comparison = CompareWidgetVersions(imported.WidgetVersion, existing.WidgetVersion);
        if (quickImport && comparison <= 0)
        {
            return true;
        }

        if (comparison < 0)
        {
            return true;
        }

        return false;
    }

    private static bool DashboardWidgetMatches(
        DashboardWidgetSnapshot existing,
        NormalizedDashboardWidget imported,
        IReadOnlyList<int> permissionIds,
        IReadOnlyList<int> roleIds)
        => string.Equals(existing.WidgetKey, imported.WidgetKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.Title, imported.Title, StringComparison.Ordinal)
            && string.Equals(existing.Description, imported.Description, StringComparison.Ordinal)
            && string.Equals(existing.WidgetType, imported.WidgetType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.Payload, imported.Payload, StringComparison.Ordinal)
            && string.Equals(existing.ModuleKey, imported.ModuleKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.Author, imported.Author, StringComparison.Ordinal)
            && IdSetsMatch(existing.PermissionIds, permissionIds)
            && IdSetsMatch(existing.RoleIds, roleIds);

    private static bool IdSetsMatch(IReadOnlyList<int> left, IReadOnlyList<int> right)
        => left.Count == right.Count
            && left.Order().SequenceEqual(right.Order());

    private static int CompareWidgetVersions(string left, string right)
    {
        if (TryParseComparableVersion(left, out var leftVersion)
            && TryParseComparableVersion(right, out var rightVersion))
        {
            return leftVersion.CompareTo(rightVersion);
        }

        return string.Compare(
            left?.Trim(),
            right?.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseComparableVersion(string? value, out Version version)
    {
        var text = value?.Trim() ?? string.Empty;
        var suffixIndex = text.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
        {
            text = text[..suffixIndex];
        }

        return Version.TryParse(text, out version!);
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value
            .Select(ch => invalid.Contains(ch) ? '-' : ch)
            .ToArray();
        return new string(chars);
    }

    private sealed class DashboardWidgetDocument
    {
        public string Format { get; set; } = string.Empty;

        public int FormatVersion { get; set; }

        public string? PackageVersion { get; set; }

        public string? ModuleKey { get; set; }

        public string? Author { get; set; }

        public DateTime? ExportedAtUtc { get; set; }

        public List<DashboardWidgetDocumentItem> Widgets { get; set; } = [];
    }

    private sealed class DashboardWidgetDocumentItem
    {
        public string WidgetKey { get; set; } = string.Empty;

        public string? WidgetVersion { get; set; }

        public string Title { get; set; } = string.Empty;

        public string? Description { get; set; }

        public string WidgetType { get; set; } = "portal";

        public string? Payload { get; set; }

        public string? ModuleKey { get; set; }

        public string? Author { get; set; }

        public List<string>? PermissionNames { get; set; }

        public List<string>? RoleNames { get; set; }
    }

    private sealed record NormalizedDashboardWidget(
        string WidgetKey,
        string WidgetVersion,
        string Title,
        string? Description,
        string WidgetType,
        string? Payload,
        string? ModuleKey,
        string? Author,
        IReadOnlyList<string> PermissionNames,
        IReadOnlyList<string> RoleNames);

    private sealed record DashboardWidgetSnapshot(
        int WidgetId,
        string WidgetKey,
        string Title,
        string? Description,
        string WidgetType,
        string? Payload,
        string? ModuleKey,
        string? Author,
        string WidgetVersion,
        IReadOnlyList<int> PermissionIds,
        IReadOnlyList<int> RoleIds);

    private sealed class WidgetVersionComparer : IComparer<string>
    {
        public static readonly WidgetVersionComparer Instance = new();

        public int Compare(string? x, string? y)
            => CompareWidgetVersions(
                string.IsNullOrWhiteSpace(x) ? LegacyWidgetVersion : x,
                string.IsNullOrWhiteSpace(y) ? LegacyWidgetVersion : y);
    }
}

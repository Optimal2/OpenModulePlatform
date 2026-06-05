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
       w.widget_type,
       w.payload,
       w.module_key,
       w.author,
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
                    WidgetType = rdr.GetString(3),
                    Payload = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    ModuleKey = rdr.IsDBNull(5) ? string.Empty : rdr.GetString(5),
                    Author = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                    IsEnabled = rdr.GetBoolean(7),
                    ModifiedUtc = rdr.GetDateTime(8)
                };

                row.WidgetKey = string.IsNullOrWhiteSpace(row.WidgetKey)
                    ? CreateFallbackWidgetKey(row)
                    : row.WidgetKey;
                rows.Add(widgetId, row);
            }

            if (!rdr.IsDBNull(9))
            {
                AddDistinct(row.PermissionNames, rdr.GetString(9));
            }

            if (!rdr.IsDBNull(10))
            {
                AddDistinct(row.RoleNames, rdr.GetString(10));
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

    public async Task<(byte[] Content, string FileName)> ExportAsync(
        string? moduleKey,
        CancellationToken ct)
    {
        var rows = await GetWidgetsAsync(moduleKey, ct);
        var trimmedModuleKey = string.IsNullOrWhiteSpace(moduleKey) ? null : moduleKey.Trim();
        return ExportRows(rows, trimmedModuleKey, trimmedModuleKey ?? "all");
    }

    public async Task<(byte[] Content, string FileName)> ExportWidgetAsync(
        int widgetId,
        CancellationToken ct)
    {
        var rows = await GetWidgetsAsync(null, ct);
        var row = rows.FirstOrDefault(item => item.WidgetId == widgetId)
            ?? throw new InvalidOperationException("The selected dashboard widget was not found.");

        return ExportRows([row], null, row.WidgetKey);
    }

    public async Task<(byte[] Content, string FileName)> ExportWidgetsAsync(
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

    private static (byte[] Content, string FileName) ExportRows(
        IReadOnlyList<DashboardWidgetAdminRow> rows,
        string? documentModuleKey,
        string fileNameKey)
    {
        var document = new DashboardWidgetDocument
        {
            Format = FormatName,
            FormatVersion = FormatVersion,
            ModuleKey = documentModuleKey,
            ExportedAtUtc = DateTime.UtcNow,
            Widgets = rows
                .OrderBy(static row => row.ModuleKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static row => row.WidgetKey, StringComparer.OrdinalIgnoreCase)
                .Select(row => new DashboardWidgetDocumentItem
                {
                    WidgetKey = row.WidgetKey,
                    Title = row.Title,
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
        var fileName = $"omp-dashboard-widgets-{SanitizeFileName(fileNameKey)}-{DateTime.UtcNow:yyyyMMddHHmmss}.json";
        return (Encoding.UTF8.GetBytes(json), fileName);
    }

    public async Task<DashboardWidgetImportResult> ImportAsync(
        Stream stream,
        string sourceName,
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
                var widgetId = await FindWidgetIdAsync(conn, tx, normalized, ct);
                if (widgetId.HasValue)
                {
                    await UpdateWidgetAsync(conn, tx, widgetId.Value, normalized, ct);
                    result.UpdatedCount++;
                }
                else
                {
                    widgetId = await InsertWidgetAsync(conn, tx, normalized, ct);
                    result.CreatedCount++;
                }

                await ReplacePermissionRowsAsync(conn, tx, widgetId.Value, permissionIds, roleIds, ct);
                result.PermissionRowCount += permissionIds.Count + roleIds.Count;
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
        var title = CleanRequiredText(item.Title, "title", 200);
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
            title,
            widgetType,
            payload,
            moduleKey,
            author,
            permissionNames,
            roleNames);
    }

    private static async Task<int?> FindWidgetIdAsync(
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
        return value is null || value == DBNull.Value
            ? null
            : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> InsertWidgetAsync(
        SqlConnection conn,
        SqlTransaction tx,
        NormalizedDashboardWidget widget,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO omp_portal.widgets(widget_key, title, widget_type, payload, module_key, author, modified_at)
OUTPUT INSERTED.widget_id
VALUES(@widget_key, @title, @widget_type, @payload, @module_key, @author, SYSUTCDATETIME());";

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
    widget_type = @widget_type,
    payload = @payload,
    module_key = @module_key,
    author = @author,
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
        cmd.Parameters.Add("@widget_type", SqlDbType.NVarChar, 50).Value = widget.WidgetType;
    }

    private static void AddWidgetParameters(SqlCommand cmd, NormalizedDashboardWidget widget)
    {
        AddIdentityParameters(cmd, widget);
        cmd.Parameters.Add("@payload", SqlDbType.NVarChar, -1).Value =
            widget.Payload is null ? DBNull.Value : widget.Payload;
        cmd.Parameters.Add("@author", SqlDbType.NVarChar, 200).Value =
            widget.Author is null ? DBNull.Value : widget.Author;
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

        public string? ModuleKey { get; set; }

        public string? Author { get; set; }

        public DateTime? ExportedAtUtc { get; set; }

        public List<DashboardWidgetDocumentItem> Widgets { get; set; } = [];
    }

    private sealed class DashboardWidgetDocumentItem
    {
        public string WidgetKey { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string WidgetType { get; set; } = "portal";

        public string? Payload { get; set; }

        public string? ModuleKey { get; set; }

        public string? Author { get; set; }

        public List<string>? PermissionNames { get; set; }

        public List<string>? RoleNames { get; set; }
    }

    private sealed record NormalizedDashboardWidget(
        string WidgetKey,
        string Title,
        string WidgetType,
        string? Payload,
        string? ModuleKey,
        string? Author,
        IReadOnlyList<string> PermissionNames,
        IReadOnlyList<string> RoleNames);
}

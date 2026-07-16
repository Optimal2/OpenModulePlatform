using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed class HostAgentCredentialStoreService
{
    private const string ProtectionProvider = "WindowsDpapi";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    static HostAgentCredentialStoreService()
    {
        JsonOptions.Converters.Add(new CredentialStoreDateTimeOffsetConverter());
    }

    private readonly HostAgentSettings _settings;

    public HostAgentCredentialStoreService(IOptionsMonitor<HostAgentSettings> options)
    {
        _settings = options.CurrentValue;
    }

    public async Task<HostAgentCredentialStoreDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        var storeSettings = _settings.CredentialStore;
        if (!storeSettings.IsEnabled())
        {
            return new HostAgentCredentialStoreDocument();
        }

        var path = storeSettings.ResolveFilePath();
        if (!File.Exists(path))
        {
            return new HostAgentCredentialStoreDocument();
        }

        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<HostAgentCredentialStoreDocument>(
            stream,
            JsonOptions,
            cancellationToken);

        return NormalizeDocument(document);
    }

    public async Task UpsertCredentialAsync(
        string key,
        string userName,
        string password,
        string description = "",
        CancellationToken cancellationToken = default)
    {
        ValidateCredentialKey(key);
        var storeSettings = _settings.CredentialStore;
        EnsureStoreEnabled(storeSettings);

        var document = await LoadAsync(cancellationToken);
        var normalizedKey = key.Trim();
        document.Credentials[normalizedKey] = new HostAgentStoredCredentialEntry
        {
            UserName = userName.Trim(),
            EncryptedPassword = ProtectPassword(password, storeSettings),
            ProtectionProvider = ProtectionProvider,
            ProtectionScope = NormalizeProtectionScope(storeSettings.ProtectionScope),
            Description = description.Trim(),
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        document.UpdatedUtc = DateTimeOffset.UtcNow;

        await SaveAsync(document, cancellationToken);
    }

    public async Task<bool> RemoveCredentialAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateCredentialKey(key);
        var storeSettings = _settings.CredentialStore;
        EnsureStoreEnabled(storeSettings);

        var document = await LoadAsync(cancellationToken);
        var removed = document.Credentials.Remove(key.Trim());
        if (!removed)
        {
            return false;
        }

        document.UpdatedUtc = DateTimeOffset.UtcNow;
        await SaveAsync(document, cancellationToken);
        return true;
    }

    /// <summary>
    /// Returns a plain-text credential. Keep the returned value scoped tightly and never log it.
    /// </summary>
    public async Task<HostAgentPlainTextCredential?> TryReadCredentialAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ValidateCredentialKey(key);
        var storeSettings = _settings.CredentialStore;
        if (!storeSettings.IsEnabled())
        {
            return null;
        }

        var document = await LoadAsync(cancellationToken);
        if (!document.Credentials.TryGetValue(key.Trim(), out var entry))
        {
            return null;
        }

        if (!string.Equals(entry.ProtectionProvider, ProtectionProvider, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Credential '{key}' uses unsupported protection provider '{entry.ProtectionProvider}'.");
        }

        return new HostAgentPlainTextCredential(
            key.Trim(),
            entry.UserName,
            UnprotectPassword(entry.EncryptedPassword, storeSettings, entry.ProtectionScope));
    }

    public string ProtectPassword(string password)
        => ProtectPassword(password, _settings.CredentialStore);

    public string UnprotectPassword(string encryptedPassword, string protectionScope)
        => UnprotectPassword(encryptedPassword, _settings.CredentialStore, protectionScope);

    public static string ProtectPassword(string password, HostAgentCredentialStoreSettings settings)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("HostAgent credential encryption requires Windows DPAPI.");
        }

        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(settings);

        return ProtectPasswordWindows(password, settings);
    }

    public static string UnprotectPassword(
        string encryptedPassword,
        HostAgentCredentialStoreSettings settings,
        string protectionScope)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("HostAgent credential encryption requires Windows DPAPI.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(encryptedPassword);
        ArgumentNullException.ThrowIfNull(settings);

        return UnprotectPasswordWindows(encryptedPassword, settings, protectionScope);
    }

    [SupportedOSPlatform("windows")]
    private static string ProtectPasswordWindows(string password, HostAgentCredentialStoreSettings settings)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var encryptedBytes = ProtectedData.Protect(
            passwordBytes,
            CreateEntropy(settings),
            ResolveDataProtectionScope(settings.ProtectionScope));
        return Convert.ToBase64String(encryptedBytes);
    }

    [SupportedOSPlatform("windows")]
    private static string UnprotectPasswordWindows(
        string encryptedPassword,
        HostAgentCredentialStoreSettings settings,
        string protectionScope)
    {
        var encryptedBytes = Convert.FromBase64String(encryptedPassword);
        var passwordBytes = ProtectedData.Unprotect(
            encryptedBytes,
            CreateEntropy(settings),
            ResolveDataProtectionScope(protectionScope));
        return Encoding.UTF8.GetString(passwordBytes);
    }

    private async Task SaveAsync(
        HostAgentCredentialStoreDocument document,
        CancellationToken cancellationToken)
    {
        var storeSettings = _settings.CredentialStore;
        EnsureStoreEnabled(storeSettings);

        var path = storeSettings.ResolveFilePath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private static HostAgentCredentialStoreDocument NormalizeDocument(HostAgentCredentialStoreDocument? document)
    {
        document ??= new HostAgentCredentialStoreDocument();
        document.Credentials ??= new Dictionary<string, HostAgentStoredCredentialEntry>(StringComparer.OrdinalIgnoreCase);
        document.Credentials = new Dictionary<string, HostAgentStoredCredentialEntry>(
            document.Credentials,
            StringComparer.OrdinalIgnoreCase);
        return document;
    }

    private static byte[] CreateEntropy(HostAgentCredentialStoreSettings settings)
    {
        var purpose = string.IsNullOrWhiteSpace(settings.EntropyPurpose)
            ? "OpenModulePlatform.HostAgent.CredentialStore.v1"
            : settings.EntropyPurpose.Trim();
        return Encoding.UTF8.GetBytes(purpose);
    }

    [SupportedOSPlatform("windows")]
    private static DataProtectionScope ResolveDataProtectionScope(string protectionScope)
    {
        var normalized = NormalizeProtectionScope(protectionScope);
        return string.Equals(
            normalized,
            HostAgentCredentialProtectionScopes.CurrentUser,
            StringComparison.OrdinalIgnoreCase)
            ? DataProtectionScope.CurrentUser
            : DataProtectionScope.LocalMachine;
    }

    private static string NormalizeProtectionScope(string protectionScope)
        => string.Equals(
            protectionScope?.Trim(),
            HostAgentCredentialProtectionScopes.CurrentUser,
            StringComparison.OrdinalIgnoreCase)
            ? HostAgentCredentialProtectionScopes.CurrentUser
            : HostAgentCredentialProtectionScopes.LocalMachine;

    private static void EnsureStoreEnabled(HostAgentCredentialStoreSettings settings)
    {
        settings.Validate();
        if (!settings.IsEnabled())
        {
            throw new InvalidOperationException(
                "HostAgent credential store is disabled. Set HostAgent:CredentialStore:AutomationMode before writing credentials.");
        }
    }

    private static void ValidateCredentialKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Credential key must be configured.", nameof(key));
        }
    }

    private sealed class CredentialStoreDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var value = reader.GetString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    return DateTimeOffset.MinValue;
                }

                var trimmed = value.Trim();
                if (DateTimeOffset.TryParse(
                    trimmed,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces,
                    out var parsed))
                {
                    return parsed;
                }

                if (TryParseMicrosoftJsonDate(trimmed, out parsed))
                {
                    return parsed;
                }
            }

            return reader.GetDateTimeOffset();
        }

        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset value,
            JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }

        private static bool TryParseMicrosoftJsonDate(string value, out DateTimeOffset result)
        {
            result = default;
            if (!value.StartsWith("/Date(", StringComparison.Ordinal)
                || !value.EndsWith(")/", StringComparison.Ordinal))
            {
                return false;
            }

            var millisecondsText = value["/Date(".Length..^2];
            var offsetIndex = millisecondsText.IndexOfAny(new[] { '+', '-' }, 1);
            if (offsetIndex > 0)
            {
                millisecondsText = millisecondsText[..offsetIndex];
            }

            if (!long.TryParse(millisecondsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
            {
                return false;
            }

            result = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            return true;
        }
    }
}

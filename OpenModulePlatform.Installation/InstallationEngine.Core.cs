using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.Installation;

public static partial class InstallationEngine
{
    internal const string BootstrapPrincipalPlaceholder = "__BOOTSTRAP_PORTAL_ADMIN_PRINCIPAL__";
    internal const int SqlDeadlockErrorNumber = 1205;
    internal const int SqlDeadlockRetryCount = 3;
    internal const int ServiceStopTimeoutSeconds = 60;
    internal const int ArtifactHashBufferSize = 64 * 1024;
    internal const string HostAgentWindowsServiceExecutableName = "OpenModulePlatform.HostAgent.WindowsService.exe";
    internal static readonly string[] KnownHostAgentServiceNamePrefixes =
    [
        "EMP.HostAgent",
        "OMP.HostAgent",
        "OpenModulePlatform.HostAgent"
    ];
    internal static readonly IReadOnlyDictionary<string, string> EmptyStringDictionary =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    internal static async Task<T> ReadJsonAsync<T>(string path)
        where T : new()
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Configuration file was not found.", path);
        }

        await using var stream = File.OpenRead(path);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions);
        return value ?? new T();
    }
}

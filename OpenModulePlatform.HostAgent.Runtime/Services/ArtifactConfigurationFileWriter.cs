using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

internal static class ArtifactConfigurationFileWriter
{
    private const string AppSettingsRelativePath = "appsettings.json";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static IReadOnlyList<ArtifactConfigurationFileDescriptor> WithBuiltInWebAppConfiguration(
        IReadOnlyList<ArtifactConfigurationFileDescriptor> files,
        WebAppDeploymentDescriptor deployment,
        string ompConnectionString,
        HostAgentSettings settings)
    {
        if (HasAppSettingsJson(files))
        {
            return files;
        }

        return
        [
            CreateBuiltInWebAppConfigurationFile(deployment, ompConnectionString, settings),
            .. files
        ];
    }

    public static IReadOnlyList<ArtifactConfigurationFileDescriptor> WithBuiltInServiceAppConfiguration(
        IReadOnlyList<ArtifactConfigurationFileDescriptor> files,
        ServiceAppDeploymentDescriptor deployment,
        string ompConnectionString)
    {
        if (HasAppSettingsJson(files))
        {
            return files;
        }

        return
        [
            CreateBuiltInServiceAppConfigurationFile(deployment, ompConnectionString),
            .. files
        ];
    }

    public static bool AreApplied(
        string targetRoot,
        IReadOnlyList<ArtifactConfigurationFileDescriptor> files,
        IReadOnlyDictionary<string, string> variables)
    {
        if (files.Count == 0)
        {
            return true;
        }

        foreach (var file in files)
        {
            var path = ResolveTargetPath(targetRoot, file);
            var expectedContent = Render(file.FileContent, variables);
            if (!File.Exists(path) || !FileContentEquals(path, expectedContent))
            {
                return false;
            }
        }

        return true;
    }

    public static async Task ApplyAsync(
        string targetRoot,
        IReadOnlyList<ArtifactConfigurationFileDescriptor> files,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken cancellationToken)
    {
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = ResolveTargetPath(targetRoot, file);
            var fileContent = Render(file.FileContent, variables);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            if (File.Exists(path) && FileContentEquals(path, fileContent))
            {
                continue;
            }

            await File.WriteAllTextAsync(path, fileContent, Utf8NoBom, cancellationToken);
        }
    }

    public static IReadOnlyDictionary<string, string> CreateVariables(
        WebAppDeploymentDescriptor deployment,
        string ompConnectionString,
        HostAgentSettings settings)
        => CreateVariables(
            deployment.HostId,
            deployment.HostKey,
            deployment.AppInstanceId,
            deployment.AppInstanceKey,
            deployment.ArtifactId,
            deployment.Version,
            deployment.TargetName,
            ompConnectionString,
            settings);

    public static IReadOnlyDictionary<string, string> CreateVariables(
        ServiceAppDeploymentDescriptor deployment,
        string ompConnectionString,
        HostAgentSettings settings)
        => CreateVariables(
            deployment.HostId,
            deployment.HostKey,
            deployment.AppInstanceId,
            deployment.AppInstanceKey,
            deployment.ArtifactId,
            deployment.Version,
            deployment.TargetName,
            ompConnectionString,
            settings);

    private static string ResolveTargetPath(
        string targetRoot,
        ArtifactConfigurationFileDescriptor file)
    {
        if (string.IsNullOrWhiteSpace(file.RelativePath))
        {
            throw new InvalidOperationException(
                $"Artifact configuration file '{file.ArtifactConfigurationFileId}' has no relative path.");
        }

        var relativePath = file.RelativePath.Trim().Replace('/', Path.DirectorySeparatorChar);
        return DeploymentPath.CombineUnderRoot(
            targetRoot,
            relativePath,
            $"Artifact configuration file '{file.ArtifactConfigurationFileId}' RelativePath");
    }

    private static ArtifactConfigurationFileDescriptor CreateBuiltInWebAppConfigurationFile(
        WebAppDeploymentDescriptor deployment,
        string ompConnectionString,
        HostAgentSettings settings)
    {
        var databaseName = ResolveDatabaseName(ompConnectionString);
        var dataProtectionKeyPath = ResolveWebAppDataProtectionKeyPath(settings);
        var content = JsonSerializer.Serialize(
            new
            {
                Portal = new
                {
                    Title = deployment.DisplayName,
                    DefaultCulture = "sv-SE",
                    SupportedCultures = new[] { "sv-SE", "en-US" },
                    PortalTopBar = new
                    {
                        Enabled = true,
                        PortalBaseUrl = "/"
                    },
                    AllowAnonymous = false,
                    UseForwardedHeaders = false,
                    PermissionMode = "Any"
                },
                ConnectionStrings = new
                {
                    OmpDb = ompConnectionString
                },
                OmpAuth = new
                {
                    CookieName = ".OpenModulePlatform.Auth",
                    LoginPath = "/auth/login",
                    LogoutPath = "/auth/logout",
                    AccessDeniedPath = "/status/403",
                    ApplicationName = "OpenModulePlatform",
                    DataProtectionKeyPath = dataProtectionKeyPath
                },
                OpenDocViewer = new
                {
                    BaseUrl = "/opendocviewer/",
                    SampleFileUrl = "/opendocviewer/sample.pdf"
                },
                ContentWebAppModule = new
                {
                    AppInstanceId = deployment.AppInstanceId,
                    HomeSlug = "home",
                    ServerReportsPath = "App_Data/ContentReports",
                    HtmlFilesPath = "App_Data/ContentPages",
                    AllowedServerReportDatabases = string.IsNullOrWhiteSpace(databaseName)
                        ? Array.Empty<string>()
                        : [databaseName],
                    ServerReportDefaultMaxRows = 100,
                    ServerReportMaxRowsLimit = 1000,
                    ServerReportQueryTimeoutSeconds = 30
                },
                Logging = new
                {
                    LogLevel = new Dictionary<string, string>
                    {
                        ["Default"] = "Information",
                        ["Microsoft.AspNetCore"] = "Warning"
                    }
                }
            },
            new JsonSerializerOptions { WriteIndented = true });

        return new ArtifactConfigurationFileDescriptor
        {
            ArtifactConfigurationFileId = 0,
            ArtifactId = deployment.ArtifactId,
            RelativePath = AppSettingsRelativePath,
            FileContent = content
        };
    }

    private static ArtifactConfigurationFileDescriptor CreateBuiltInServiceAppConfigurationFile(
        ServiceAppDeploymentDescriptor deployment,
        string ompConnectionString)
    {
        var content = JsonSerializer.Serialize(
            new
            {
                ConnectionStrings = new
                {
                    OmpDb = ompConnectionString
                },
                Worker = new
                {
                    AppInstanceId = deployment.AppInstanceId,
                    PollSeconds = 5,
                    ConfigRefreshSeconds = 15,
                    HeartbeatSeconds = 10
                },
                Logging = new
                {
                    LogLevel = new Dictionary<string, string>
                    {
                        ["Default"] = "Information",
                        ["Microsoft.Hosting.Lifetime"] = "Information"
                    }
                }
            },
            new JsonSerializerOptions { WriteIndented = true });

        return new ArtifactConfigurationFileDescriptor
        {
            ArtifactConfigurationFileId = 0,
            ArtifactId = deployment.ArtifactId,
            RelativePath = AppSettingsRelativePath,
            FileContent = content
        };
    }

    private static IReadOnlyDictionary<string, string> CreateVariables(
        Guid hostId,
        string hostKey,
        Guid appInstanceId,
        string appInstanceKey,
        int artifactId,
        string artifactVersion,
        string? targetName,
        string ompConnectionString,
        HostAgentSettings settings)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Omp.HostId"] = hostId.ToString("D"),
            ["Omp.HostKey"] = hostKey,
            ["Omp.AppInstanceId"] = appInstanceId.ToString("D"),
            ["Omp.AppInstanceKey"] = appInstanceKey,
            ["Omp.ArtifactId"] = artifactId.ToString(CultureInfo.InvariantCulture),
            ["Omp.ArtifactVersion"] = artifactVersion,
            ["Omp.TargetName"] = targetName ?? string.Empty,
            ["Omp.ConnectionStrings.OmpDb"] = ompConnectionString,
            ["Omp.HostAgent.CentralArtifactRoot"] = settings.CentralArtifactRoot,
            ["Omp.HostAgent.LocalArtifactCacheRoot"] = settings.LocalArtifactCacheRoot,
            ["Omp.HostAgent.WebAppsRoot"] = settings.WebAppsRoot,
            ["Omp.HostAgent.PortalPhysicalPath"] = settings.PortalPhysicalPath,
            ["Omp.HostAgent.ServicesRoot"] = settings.ServicesRoot,
            ["Omp.HostAgent.WebAppDataProtectionKeyPath"] = settings.WebAppDataProtectionKeyPath
        };

        foreach (var item in variables.ToArray())
        {
            variables["Omp.Json." + item.Key[4..]] = JsonStringContent(item.Value);
        }

        return variables;
    }

    private static string Render(string content, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(content) || !content.Contains("{{Omp.", StringComparison.Ordinal))
        {
            return content;
        }

        var rendered = content;
        foreach (var variable in variables)
        {
            rendered = rendered.Replace(
                "{{" + variable.Key + "}}",
                variable.Value,
                StringComparison.Ordinal);
        }

        return rendered;
    }

    private static string JsonStringContent(string value)
    {
        var serialized = JsonSerializer.Serialize(value);
        return serialized.Length < 2 ? string.Empty : serialized[1..^1];
    }

    private static bool FileContentEquals(string path, string expectedContent)
    {
        var expectedBytes = Utf8NoBom.GetBytes(expectedContent);
        var actualBytes = File.ReadAllBytes(path);
        return actualBytes.AsSpan().SequenceEqual(expectedBytes);
    }

    private static bool HasAppSettingsJson(IReadOnlyList<ArtifactConfigurationFileDescriptor> files)
        => files.Any(file => string.Equals(
            file.RelativePath.Trim().Replace('\\', '/'),
            AppSettingsRelativePath,
            StringComparison.OrdinalIgnoreCase));

    private static string ResolveDatabaseName(string connectionString)
    {
        try
        {
            return new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private static string ResolveWebAppDataProtectionKeyPath(HostAgentSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.WebAppDataProtectionKeyPath))
        {
            return settings.WebAppDataProtectionKeyPath.Trim();
        }

        if (string.IsNullOrWhiteSpace(settings.WebAppsRoot))
        {
            return string.Empty;
        }

        var webAppsRoot = Path.GetFullPath(settings.WebAppsRoot.Trim());
        var runtimeRoot = Directory.GetParent(webAppsRoot)?.FullName;
        return string.IsNullOrWhiteSpace(runtimeRoot)
            ? string.Empty
            : Path.Combine(runtimeRoot, "DataProtectionKeys");
    }
}

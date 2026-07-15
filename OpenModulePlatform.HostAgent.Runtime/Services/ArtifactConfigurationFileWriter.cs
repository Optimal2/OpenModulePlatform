using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        var builtInFile = CreateBuiltInWebAppConfigurationFile(deployment, ompConnectionString, settings);
        if (!HasAppSettingsJson(files))
        {
            return
            [
                builtInFile,
                .. files
            ];
        }

        return files
            .Select(file => IsAppSettingsJson(file)
                ? new ArtifactConfigurationFileDescriptor
                {
                    ArtifactConfigurationFileId = file.ArtifactConfigurationFileId,
                    ArtifactId = file.ArtifactId,
                    RelativePath = file.RelativePath,
                    FileContent = MergeJsonConfiguration(builtInFile.FileContent, file.FileContent, file.RelativePath)
                }
                : file)
            .ToArray();
    }

    public static IReadOnlyList<ArtifactConfigurationFileDescriptor> WithBuiltInServiceAppConfiguration(
        IReadOnlyList<ArtifactConfigurationFileDescriptor> files,
        ServiceAppDeploymentDescriptor deployment,
        string ompConnectionString,
        HostAgentSettings settings)
    {
        if (HasAppSettingsJson(files))
        {
            return files;
        }

        return
        [
            IsWorkerManagerDeployment(deployment)
                ? CreateBuiltInWorkerManagerConfigurationFile(deployment, ompConnectionString, settings)
                : CreateBuiltInServiceAppConfigurationFile(deployment, ompConnectionString),
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
        var dataProtectionKeyPath = ResolveWebAppDataProtectionKeyPath(settings);
        var forwardedHeadersKnownProxies = settings.WebAppForwardedHeadersKnownProxies ?? Array.Empty<string>();
        var forwardedHeadersKnownNetworks = settings.WebAppForwardedHeadersKnownNetworks ?? Array.Empty<string>();
        var webAppOptions = new
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
            UseForwardedHeaders = settings.WebAppUseForwardedHeaders,
            ForwardedHeadersTrustAllProxies = settings.WebAppForwardedHeadersTrustAllProxies,
            ForwardedHeadersKnownProxies = forwardedHeadersKnownProxies,
            ForwardedHeadersKnownNetworks = forwardedHeadersKnownNetworks,
            PermissionMode = "Any"
        };
        var content = JsonSerializer.Serialize(
            new
            {
                // OMP-hosted web apps historically used Portal, while shared web defaults use WebApp.
                // Keep both sections aligned so external modules can use either convention.
                Portal = webAppOptions,
                WebApp = webAppOptions,
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

    private static ArtifactConfigurationFileDescriptor CreateBuiltInWorkerManagerConfigurationFile(
        ServiceAppDeploymentDescriptor deployment,
        string ompConnectionString,
        HostAgentSettings settings)
    {
        var content = JsonSerializer.Serialize(
            new
            {
                ConnectionStrings = new
                {
                    OmpDb = ompConnectionString
                },
                WorkerManager = new
                {
                    CatalogMode = "OmpDatabase",
                    HostKey = deployment.HostKey,
                    HostName = Environment.MachineName,
                    RefreshSeconds = 15,
                    WorkerProcessPath = string.Empty,
                    StopTimeoutSeconds = 15,
                    RestartDelaySeconds = 5,
                    RestartWindowSeconds = 300,
                    MaxRestartsPerWindow = 5,
                    OmpDatabase = new
                    {
                        RuntimeKind = "windows-worker-plugin",
                        RunningDesiredState = 1,
                        UseHostArtifactCache = true
                    },
                    HostAgentRpc = new
                    {
                        Enabled = settings.EnableRpc,
                        PipeName = settings.ResolveRpcPipeName(),
                        TimeoutSeconds = settings.RpcRequestTimeoutSeconds
                    },
                    Workers = Array.Empty<object>()
                },
                Logging = new
                {
                    LogLevel = new Dictionary<string, string>
                    {
                        ["Default"] = "Information",
                        ["Microsoft.Hosting.Lifetime"] = "Information"
                    }
                },
                NLog = new
                {
                    autoReload = true,
                    throwConfigExceptions = true,
                    variables = new
                    {
                        appName = "OpenModulePlatform.WorkerManager.WindowsService",
                        logDirectory = "${basedir}/logs"
                    },
                    targets = new Dictionary<string, object>
                    {
                        ["logfile"] = new
                        {
                            type = "File",
                            fileName = "${var:logDirectory}/${var:appName}-${shortdate}.log",
                            layout = "${longdate}|${uppercase:${level}}|${logger}|${message}${onexception:inner= ${exception:format=tostring}}"
                        },
                        ["console"] = new
                        {
                            type = "Console",
                            layout = "${longdate}|${uppercase:${level}}|${logger}|${message}${onexception:inner= ${exception:format=tostring}}"
                        }
                    },
                    rules = new object[]
                    {
                        new
                        {
                            logger = "Microsoft.Hosting.Lifetime",
                            minLevel = "Info",
                            writeTo = "console,logfile",
                            final = true
                        },
                        new
                        {
                            logger = "Microsoft.*",
                            maxLevel = "Info",
                            final = true
                        },
                        new
                        {
                            logger = "*",
                            minLevel = "Info",
                            writeTo = "console,logfile"
                        }
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
            ["Omp.ConnectionStrings.OmpDb.DatabaseName"] = ResolveDatabaseName(ompConnectionString),
            ["Omp.HostAgent.CentralArtifactRoot"] = settings.CentralArtifactRoot,
            ["Omp.HostAgent.LocalArtifactCacheRoot"] = settings.LocalArtifactCacheRoot,
            ["Omp.HostAgent.WebAppsRoot"] = settings.WebAppsRoot,
            ["Omp.HostAgent.PortalPhysicalPath"] = settings.PortalPhysicalPath,
            ["Omp.HostAgent.ServicesRoot"] = settings.ServicesRoot,
            ["Omp.HostAgent.WebAppDataProtectionKeyPath"] = ResolveWebAppDataProtectionKeyPath(settings),
            ["Omp.HostAgent.WebAppUseForwardedHeaders"] = settings.WebAppUseForwardedHeaders.ToString(CultureInfo.InvariantCulture),
            ["Omp.HostAgent.WebAppForwardedHeadersTrustAllProxies"] = settings.WebAppForwardedHeadersTrustAllProxies.ToString(CultureInfo.InvariantCulture),
            ["Omp.HostAgent.WebAppForwardedHeadersKnownProxies"] = string.Join(",", settings.WebAppForwardedHeadersKnownProxies ?? Array.Empty<string>()),
            ["Omp.HostAgent.WebAppForwardedHeadersKnownNetworks"] = string.Join(",", settings.WebAppForwardedHeadersKnownNetworks ?? Array.Empty<string>())
        };

        foreach (var item in variables.ToArray())
        {
            variables["Omp.Json." + item.Key[4..]] = JsonStringContent(item.Value);
        }

        return variables;
    }

    internal static string Render(string content, IReadOnlyDictionary<string, string> variables)
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
        => files.Any(IsAppSettingsJson);

    private static bool IsAppSettingsJson(ArtifactConfigurationFileDescriptor file)
        => string.Equals(
            file.RelativePath.Trim().Replace('\\', '/'),
            AppSettingsRelativePath,
            StringComparison.OrdinalIgnoreCase);

    private static string MergeJsonConfiguration(
        string baseContent,
        string overrideContent,
        string relativePath)
    {
        try
        {
            var baseObject = ParseJsonObject(baseContent, "built-in web app configuration");
            var overrideObject = ParseJsonObject(overrideContent, relativePath);
            var merged = MergeJsonObjects(baseObject, overrideObject);

            return merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Web app configuration file '{relativePath}' must contain a valid JSON object.",
                ex);
        }
    }

    private static JsonObject ParseJsonObject(string content, string sourceName)
    {
        var node = JsonNode.Parse(content);
        return node as JsonObject
            ?? throw new JsonException($"Configuration source '{sourceName}' is not a JSON object.");
    }

    private static JsonObject MergeJsonObjects(JsonObject baseObject, JsonObject overrideObject)
    {
        var result = new JsonObject();

        foreach (var item in baseObject)
        {
            result[item.Key] = item.Value?.DeepClone();
        }

        foreach (var item in overrideObject)
        {
            if (item.Value is JsonObject overrideChild
                && result[item.Key] is JsonObject baseChild)
            {
                result[item.Key] = MergeJsonObjects(baseChild, overrideChild);
                continue;
            }

            result[item.Key] = item.Value?.DeepClone();
        }

        return result;
    }

    private static bool IsWorkerManagerDeployment(ServiceAppDeploymentDescriptor deployment)
        => string.Equals(deployment.AppInstanceKey, "omp_workermanager", StringComparison.OrdinalIgnoreCase)
            || string.Equals(deployment.TargetName, "omp-workermanager", StringComparison.OrdinalIgnoreCase);

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
            : Path.Join(runtimeRoot, "DataProtectionKeys");
    }
}

using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// Pins the deployment continuity gate: when the previously deployed
/// appsettings.json on disk carries configuration that the new artifact
/// resolution no longer provides, the deployment must report Failed naming the
/// lost section instead of silently falling back to the built-in default file.
/// </summary>
public sealed class ConfigurationContinuityGateTests : IDisposable
{
    private const string PreviousWithOidc = """
        {
          "ConnectionStrings": { "OmpDb": "Server=.;Database=Omp" },
          "OmpAuth": {
            "CookieName": ".OpenModulePlatform.Auth",
            "Oidc": { "Authority": "https://login.example", "ClientId": "portal" }
          },
          "Logging": { "LogLevel": { "Default": "Information" } }
        }
        """;

    private const string BuiltInWithoutOidc = """
        {
          "ConnectionStrings": { "OmpDb": "Server=.;Database=Omp" },
          "OmpAuth": { "CookieName": ".OpenModulePlatform.Auth" },
          "Logging": { "LogLevel": { "Default": "Information" } }
        }
        """;

    private readonly string _targetRoot;

    public ConfigurationContinuityGateTests()
    {
        _targetRoot = Path.Join(Path.GetTempPath(), "OmpContinuityGateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_targetRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_targetRoot))
            {
                Directory.Delete(_targetRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort: temp cleanup must never fail the test run.
        }
    }

    [Fact]
    public void Gate_FailsWhenPreviousDeployHadOmpAuthOidcMissingFromNewResolution()
    {
        WritePreviousAppSettings(PreviousWithOidc);

        var violation = ConfigurationContinuityGate.EvaluateViolation(
            _targetRoot,
            [AppSettings(BuiltInWithoutOidc)],
            new Dictionary<string, string>());

        Assert.NotNull(violation);
        Assert.Contains("OmpAuth:Oidc", violation);
    }

    [Fact]
    public void Gate_FailsWhenPreviousDeployHadTopLevelSectionMissingFromNewResolution()
    {
        WritePreviousAppSettings(PreviousWithOidc);

        var violation = ConfigurationContinuityGate.EvaluateViolation(
            _targetRoot,
            [AppSettings("""
                {
                  "OmpAuth": {
                    "CookieName": ".OpenModulePlatform.Auth",
                    "Oidc": { "Authority": "https://login.example", "ClientId": "portal" }
                  },
                  "Logging": { "LogLevel": { "Default": "Information" } }
                }
                """)],
            new Dictionary<string, string>());

        Assert.NotNull(violation);
        Assert.Contains("ConnectionStrings", violation);
    }

    [Fact]
    public void Gate_FailsWhenNewResolutionHasNoAppSettingsAtAll()
    {
        WritePreviousAppSettings(PreviousWithOidc);

        var violation = ConfigurationContinuityGate.EvaluateViolation(
            _targetRoot,
            [],
            new Dictionary<string, string>());

        Assert.NotNull(violation);
        Assert.Contains("appsettings.json", violation);
    }

    [Fact]
    public void Gate_PassesWhenNewResolutionKeepsEverySection()
    {
        WritePreviousAppSettings(PreviousWithOidc);

        var violation = ConfigurationContinuityGate.EvaluateViolation(
            _targetRoot,
            [AppSettings(PreviousWithOidc)],
            new Dictionary<string, string>());

        Assert.Null(violation);
    }

    [Fact]
    public void Gate_PassesOnFirstDeployWhenNoPreviousFileExists()
    {
        var violation = ConfigurationContinuityGate.EvaluateViolation(
            _targetRoot,
            [AppSettings(BuiltInWithoutOidc)],
            new Dictionary<string, string>());

        Assert.Null(violation);
    }

    [Fact]
    public void Gate_RendersVariablesBeforeComparing()
    {
        WritePreviousAppSettings(PreviousWithOidc);

        var violation = ConfigurationContinuityGate.EvaluateViolation(
            _targetRoot,
            [AppSettings("""
                {
                  "ConnectionStrings": { "OmpDb": "{{Omp.Json.ConnectionStrings.OmpDb}}" },
                  "OmpAuth": {
                    "CookieName": ".OpenModulePlatform.Auth",
                    "Oidc": { "Authority": "https://login.example", "ClientId": "portal" }
                  },
                  "Logging": { "LogLevel": { "Default": "Information" } }
                }
                """)],
            new Dictionary<string, string> { ["Omp.Json.ConnectionStrings.OmpDb"] = "Server=.;Database=Omp" });

        Assert.Null(violation);
    }

    private void WritePreviousAppSettings(string content)
        => File.WriteAllText(Path.Join(_targetRoot, "appsettings.json"), content);

    private static ArtifactConfigurationFileDescriptor AppSettings(string content)
        => new()
        {
            ArtifactConfigurationFileId = 1,
            ArtifactId = 1,
            RelativePath = "appsettings.json",
            FileContent = content
        };
}

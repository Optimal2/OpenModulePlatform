using System.Text.Json;
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

    [Fact]
    public void Gate_PassesWhenOnlyHostOwnedAllowedHostsIsMissingFromNewResolution()
    {
        // A module that moves AllowedHosts out of its packaged configuration (the host
        // decides it) must not strand every host whose previous file still carries it.
        WritePreviousAppSettings("""
            {
              "ConnectionStrings": { "OmpDb": "Server=.;Database=Omp" },
              "AllowedHosts": "*"
            }
            """);

        var violation = ConfigurationContinuityGate.EvaluateViolation(
            _targetRoot,
            [AppSettings("""{ "ConnectionStrings": { "OmpDb": "Server=.;Database=Omp" } }""")],
            new Dictionary<string, string>());

        Assert.Null(violation);
    }

    [Fact]
    public void Gate_PassesWhenOnlyHostOwnedLoggingKeysAreMissingFromNewResolution()
    {
        WritePreviousAppSettings("""
            {
              "ConnectionStrings": { "OmpDb": "Server=.;Database=Omp" },
              "Logging": { "LogLevel": { "Default": "Information" }, "Console": { "FormatterName": "simple" } }
            }
            """);

        var violation = ConfigurationContinuityGate.EvaluateViolation(
            _targetRoot,
            [AppSettings("""
                {
                  "ConnectionStrings": { "OmpDb": "Server=.;Database=Omp" },
                  "Logging": { "LogLevel": { "Default": "Warning" } }
                }
                """)],
            new Dictionary<string, string>());

        Assert.Null(violation);
    }

    [Fact]
    public void Gate_StillFailsForModuleSectionWhenHostOwnedKeysAreAlsoMissing()
    {
        WritePreviousAppSettings("""
            {
              "AllowedHosts": "*",
              "ExampleModule": { "RecentJobCount": 20, "ResultPreviewCount": 50 },
              "NLog": { "throwConfigExceptions": true }
            }
            """);

        var violation = ConfigurationContinuityGate.EvaluateViolation(
            _targetRoot,
            [AppSettings("""{ "NLog": { "throwConfigExceptions": true } }""")],
            new Dictionary<string, string>());

        Assert.NotNull(violation);
        Assert.Contains("ExampleModule", violation);
        Assert.DoesNotContain("AllowedHosts", violation);
        // The operator must be told both ways forward: keep the settings through a
        // config overlay, or remove them from the previous file when the drop is intended.
        Assert.Contains("config overlay", violation);
        Assert.Contains("delete", violation);
    }

    [Fact]
    public void Gate_TreatsHostOwnedKeysCaseInsensitively()
    {
        // Configuration keys are case-insensitive, so the host-owned exemption must be too.
        WritePreviousAppSettings("""
            {
              "ConnectionStrings": { "OmpDb": "Server=.;Database=Omp" },
              "allowedhosts": "*",
              "LOGGING": { "LogLevel": { "Default": "Information" } }
            }
            """);

        var violation = ConfigurationContinuityGate.EvaluateViolation(
            _targetRoot,
            [AppSettings("""{ "ConnectionStrings": { "OmpDb": "Server=.;Database=Omp" } }""")],
            new Dictionary<string, string>());

        Assert.Null(violation);
    }

    [Fact]
    public void Gate_StillFailsForModuleSectionWhoseNameOnlyStartsWithLogging()
    {
        // The exemption is an exact top-level name, never a prefix.
        WritePreviousAppSettings("""
            {
              "ConnectionStrings": { "OmpDb": "Server=.;Database=Omp" },
              "LoggingSettings": { "RetentionDays": 30 }
            }
            """);

        var violation = ConfigurationContinuityGate.EvaluateViolation(
            _targetRoot,
            [AppSettings("""{ "ConnectionStrings": { "OmpDb": "Server=.;Database=Omp" } }""")],
            new Dictionary<string, string>());

        Assert.NotNull(violation);
        Assert.Contains("LoggingSettings", violation);
    }

    [Fact]
    public void Gate_StillComparesHostOwnedNamesNestedInsideModuleSection()
    {
        // Only the top-level keys are host-owned; a module's own setting that happens to
        // share the name is module configuration and is still compared.
        WritePreviousAppSettings("""
            {
              "ExampleModule": {
                "AllowedHosts": "partner.example",
                "Logging": { "AuditLevel": "Full" },
                "RecentJobCount": 20
              }
            }
            """);

        var violation = ConfigurationContinuityGate.EvaluateViolation(
            _targetRoot,
            [AppSettings("""{ "ExampleModule": { "RecentJobCount": 20 } }""")],
            new Dictionary<string, string>());

        Assert.NotNull(violation);
        Assert.Contains("ExampleModule:AllowedHosts", violation);
        Assert.Contains("ExampleModule:Logging", violation);
    }

    [Fact]
    public void CarryOver_KeepsRestrictiveAllowedHostsTheNewResolutionLeavesOut()
    {
        // Host-owned means the host's value holds: an operator's restrictive AllowedHosts
        // must not disappear silently because the module dropped the key.
        WritePreviousAppSettings("""
            {
              "ConnectionStrings": { "OmpDb": "Server=.;Database=Omp" },
              "AllowedHosts": "localhost;127.0.0.1"
            }
            """);

        var files = ConfigurationContinuityGate.CarryOverHostOwnedKeys(
            _targetRoot,
            [AppSettings("""{ "ConnectionStrings": { "OmpDb": "Server=.;Database=Omp" } }""")],
            out var carriedKeys);

        Assert.Equal(["AllowedHosts"], carriedKeys);
        using var resolved = JsonDocument.Parse(Assert.Single(files).FileContent);
        Assert.Equal("localhost;127.0.0.1", resolved.RootElement.GetProperty("AllowedHosts").GetString());
        Assert.Equal(
            "Server=.;Database=Omp",
            resolved.RootElement.GetProperty("ConnectionStrings").GetProperty("OmpDb").GetString());
    }

    [Fact]
    public void CarryOver_KeepsLoggingTheNewResolutionLeavesOut()
    {
        WritePreviousAppSettings("""
            {
              "ConnectionStrings": { "OmpDb": "Server=.;Database=Omp" },
              "Logging": { "LogLevel": { "Default": "Warning" } }
            }
            """);

        var files = ConfigurationContinuityGate.CarryOverHostOwnedKeys(
            _targetRoot,
            [AppSettings("""{ "ConnectionStrings": { "OmpDb": "Server=.;Database=Omp" } }""")],
            out var carriedKeys);

        Assert.Equal(["Logging"], carriedKeys);
        using var resolved = JsonDocument.Parse(Assert.Single(files).FileContent);
        Assert.Equal(
            "Warning",
            resolved.RootElement.GetProperty("Logging").GetProperty("LogLevel").GetProperty("Default").GetString());
    }

    [Fact]
    public void CarryOver_LeavesKeyTheNewResolutionSetsItself()
    {
        // The artifact or an overlay that sets the key wins, whatever its casing.
        WritePreviousAppSettings("""
            {
              "AllowedHosts": "localhost;127.0.0.1",
              "Logging": { "LogLevel": { "Default": "Warning" } }
            }
            """);
        var newResolution = AppSettings("""
            {
              "allowedHosts": "*",
              "Logging": { "LogLevel": { "Default": "Information" } }
            }
            """);

        var files = ConfigurationContinuityGate.CarryOverHostOwnedKeys(
            _targetRoot,
            [newResolution],
            out var carriedKeys);

        Assert.Empty(carriedKeys);
        Assert.Same(newResolution, Assert.Single(files));
    }

    [Fact]
    public void CarryOver_NeverCarriesModuleSections()
    {
        WritePreviousAppSettings("""
            {
              "ExampleModule": { "RecentJobCount": 20 },
              "LoggingSettings": { "RetentionDays": 30 }
            }
            """);
        var newResolution = AppSettings("""{ "ConnectionStrings": { "OmpDb": "Server=.;Database=Omp" } }""");

        var files = ConfigurationContinuityGate.CarryOverHostOwnedKeys(
            _targetRoot,
            [newResolution],
            out var carriedKeys);

        Assert.Empty(carriedKeys);
        Assert.Same(newResolution, Assert.Single(files));
    }

    [Fact]
    public void CarryOver_DoesNothingOnFirstDeploy()
    {
        var newResolution = AppSettings("""{ "ConnectionStrings": { "OmpDb": "Server=.;Database=Omp" } }""");

        var files = ConfigurationContinuityGate.CarryOverHostOwnedKeys(
            _targetRoot,
            [newResolution],
            out var carriedKeys);

        Assert.Empty(carriedKeys);
        Assert.Same(newResolution, Assert.Single(files));
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

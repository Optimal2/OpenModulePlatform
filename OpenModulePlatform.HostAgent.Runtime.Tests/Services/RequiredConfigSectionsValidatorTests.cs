using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class RequiredConfigSectionsValidatorTests
{
    [Fact]
    public void Validate_AllRequiredSectionsPresent_ReturnsNull()
    {
        var files = new List<ArtifactConfigurationFileDescriptor>
        {
            CreateAppSettingsFile("""
            {
              "Portal": {},
              "OmpAuth": {},
              "NLog": {},
              "PrinterDatabases": {},
              "ZebraConfig": {},
              "ConnectionStrings": {},
              "AuditLog": {}
            }
            """)
        };

        var result = RequiredConfigSectionsValidator.Validate(
            files,
            ["Portal", "OmpAuth", "NLog", "PrinterDatabases", "ZebraConfig", "ConnectionStrings", "AuditLog"]);

        Assert.Null(result);
    }

    [Fact]
    public void Validate_SomeRequiredSectionsMissing_ReturnsWarningListingMissingSections()
    {
        var files = new List<ArtifactConfigurationFileDescriptor>
        {
            CreateAppSettingsFile("""
            {
              "Portal": {},
              "OmpAuth": {},
              "ConnectionStrings": {},
              "AuditLog": {}
            }
            """)
        };

        var result = RequiredConfigSectionsValidator.Validate(
            files,
            ["Portal", "OmpAuth", "NLog", "PrinterDatabases", "ZebraConfig", "ConnectionStrings", "AuditLog"]);

        Assert.NotNull(result);
        Assert.Contains("PrinterDatabases", result);
        Assert.Contains("ZebraConfig", result);
        Assert.Contains("NLog", result);
        Assert.Contains("missing required sections", result);
    }

    [Fact]
    public void Validate_EmptyRequiredSections_ReturnsNull()
    {
        var files = new List<ArtifactConfigurationFileDescriptor>
        {
            CreateAppSettingsFile("""{ "Portal": {} }""")
        };

        var result = RequiredConfigSectionsValidator.Validate(files, []);

        Assert.Null(result);
    }

    [Fact]
    public void Validate_NoAppSettingsFile_ReturnsWarning()
    {
        var files = new List<ArtifactConfigurationFileDescriptor>
        {
            CreateFile("appsettings.Development.json", """{ "Portal": {} }""")
        };

        var result = RequiredConfigSectionsValidator.Validate(files, ["Portal"]);

        Assert.NotNull(result);
        Assert.Contains("Portal", result);
        Assert.Contains("missing required sections", result);
    }

    [Fact]
    public void Validate_InvalidJson_ReturnsWarning()
    {
        var files = new List<ArtifactConfigurationFileDescriptor>
        {
            CreateAppSettingsFile("not valid json")
        };

        var result = RequiredConfigSectionsValidator.Validate(files, ["Portal"]);

        Assert.NotNull(result);
        Assert.Contains("Portal", result);
    }

    [Fact]
    public void Validate_RequiredSectionWithDifferentCasing_ReturnsNull()
    {
        var files = new List<ArtifactConfigurationFileDescriptor>
        {
            CreateAppSettingsFile("""{ "portal": {}, "ompauth": {} }""")
        };

        var result = RequiredConfigSectionsValidator.Validate(files, ["Portal", "OmpAuth"]);

        Assert.Null(result);
    }

    [Fact]
    public void Validate_WhitespaceAndNullRequiredSectionsAreIgnored()
    {
        var files = new List<ArtifactConfigurationFileDescriptor>
        {
            CreateAppSettingsFile("""{ "Portal": {} }""")
        };

        var result = RequiredConfigSectionsValidator.Validate(files, ["", "   ", "Portal"]);

        Assert.Null(result);
    }

    private static ArtifactConfigurationFileDescriptor CreateAppSettingsFile(string content)
        => CreateFile("appsettings.json", content);

    private static ArtifactConfigurationFileDescriptor CreateFile(string relativePath, string content)
        => new()
        {
            ArtifactConfigurationFileId = 0,
            ArtifactId = 1,
            RelativePath = relativePath,
            FileContent = content
        };
}

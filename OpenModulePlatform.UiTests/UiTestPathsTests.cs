using OpenModulePlatform.TestSupport.Ui;

namespace OpenModulePlatform.UiTests;

// No browser, app or database: these run in the ordinary (non-Ui) test pass, so a
// consumer output layout that makes the UI suite lose its app fails CI here rather
// than turning every UI test into a silent skip.
public sealed class UiTestPathsTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "omp-uitestpaths-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    // The default in-tree layout: <project>\bin\<Configuration>\<Tfm>\.
    [InlineData(@"C:\r\App.UiTests\bin\Release\net10.0\", "Release", "net10.0")]
    [InlineData(@"C:\r\App.UiTests\bin\Debug\net10.0", "Debug", "net10.0")]
    // OmpIsolatedBuildRoot: <root>\bin\<ProjectName>\<Configuration>\<Tfm>\ - the
    // project name after bin used to be read as the configuration.
    [InlineData(@"C:\r\.omp-build\bin\App.UiTests\Release\net10.0\", "Release", "net10.0")]
    // A runtime identifier after the target framework.
    [InlineData(@"C:\r\App.UiTests\bin\Release\net10.0-windows\win-x64\", "Release", "net10.0-windows")]
    // A platform folder between bin and the configuration.
    [InlineData(@"C:\r\App.UiTests\bin\x64\Release\net10.0\", "Release", "net10.0")]
    public void Build_output_segments_are_read_around_the_target_framework(string baseDirectory, string configuration, string tfm)
    {
        Assert.Equal((configuration, tfm), UiTestPaths.BuildOutputSegments(baseDirectory));
    }

    [Fact]
    public void The_app_is_found_in_the_in_tree_layout()
    {
        var projectDirectory = Path.Join(_root, "App.Web");
        var exe = CreateFile(projectDirectory, "bin", "Release", "net10.0", "App.Web.exe");
        var testOutput = Path.Join(_root, "App.UiTests", "bin", "Release", "net10.0");

        Assert.Equal(exe, UiTestPaths.FindAppExecutable(projectDirectory, "App.Web", "App.Web", testOutput));
    }

    [Fact]
    public void The_app_is_found_in_the_isolated_build_layout()
    {
        // Directory.Build.props with OmpIsolatedBuildRoot: every project's output sits
        // under <root>\bin\<ProjectName>\, nothing under the project folder.
        var projectDirectory = Path.Join(_root, "src", "App.Web");
        Directory.CreateDirectory(projectDirectory);
        var exe = CreateFile(_root, ".omp-build", "bin", "App.Web", "Release", "net10.0", "App.Web.exe");
        var testOutput = Path.Join(_root, ".omp-build", "bin", "App.UiTests", "Release", "net10.0");

        Assert.Equal(exe, UiTestPaths.FindAppExecutable(projectDirectory, "App.Web", "App.Web", testOutput));
    }

    [Fact]
    public void No_app_is_null()
    {
        var projectDirectory = Path.Join(_root, "App.Web");
        var testOutput = Path.Join(_root, "App.UiTests", "bin", "Release", "net10.0");

        Assert.Null(UiTestPaths.FindAppExecutable(projectDirectory, "App.Web", "App.Web", testOutput));
    }

    [Fact]
    public async Task A_required_app_that_is_missing_fails_with_the_reason()
    {
        var fixture = new MissingAppFixture();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(fixture.InitializeAsync);

        Assert.Contains("OpenModulePlatform.DoesNotExist", error.Message, StringComparison.Ordinal);
        Assert.Contains("app binary not found", error.Message, StringComparison.Ordinal);
        Assert.Contains(UiTestPaths.RequiredEnvironmentVariable, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_optional_app_that_is_missing_only_reports_unavailable()
    {
        var fixture = new MissingAppFixture(required: false);

        await fixture.InitializeAsync();

        Assert.False(fixture.Available);
        Assert.Contains("app binary not found", fixture.UnavailableReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    public void The_requirement_is_read_from_the_environment_value(string? value, bool expected)
    {
        Assert.Equal(expected, UiTestPaths.IsRequired(value));
    }

    private static string CreateFile(string root, params string[] parts)
    {
        var path = Path.Join([root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private sealed class MissingAppFixture(bool required = true) : WebAppProcessFixture
    {
        protected override string SolutionFileName => "OpenModulePlatform.slnx";
        protected override string WebProjectName => "OpenModulePlatform.DoesNotExist";
        protected override bool AppRequired => required;
    }
}

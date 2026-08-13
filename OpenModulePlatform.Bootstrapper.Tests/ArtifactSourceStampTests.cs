// File: OpenModulePlatform.Bootstrapper.Tests/ArtifactSourceStampTests.cs
using Xunit;

namespace OpenModulePlatform.Bootstrapper.Tests;

/// <summary>
/// Regression tests for the per-component artifact source scope (R8-P6-3).
/// </summary>
/// <remarks>
/// Two defects have to stay dead here. The first is the repository-wide stamp: every component in
/// a repository was rebuilt whenever any file in it changed, so a deploy only converged if the
/// whole repository was version-bumped at once. The second is subtler and is why these tests
/// exist at all -- the cross-repository reference was resolved with Path.Join against an already
/// absolute expanded property, producing a path like
/// C:\repo\Project\C:\other-repo\Shared\Shared.csproj. That never exists, so resolution declined
/// and silently fell back to the repository-wide stamp. The observable behaviour was identical to
/// having no fix at all, and only an end-to-end deploy revealed it.
/// </remarks>
public sealed class ArtifactSourceStampTests : IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "omp-stamp-tests-" + Guid.NewGuid().ToString("N"));

    public ArtifactSourceStampTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp tree must never fail the suite.
        }
    }

    [Fact]
    public void TryResolveComponentProjectFile_FindsTheProjectInAComponentDirectory()
    {
        var repository = CreateRepository("repo");
        var project = WriteProject(repository, "Sample.Web");

        var resolved = ArtifactSourceStamp.TryResolveComponentProjectFile(repository, "Sample.Web");

        Assert.Equal(project, resolved, ignoreCase: true);
    }

    [Fact]
    public void TryResolveComponentProjectFile_DeclinesAPathOutsideTheSourceRoot()
    {
        var repository = CreateRepository("repo");
        CreateRepository("elsewhere");
        WriteProject(Path.Join(_root, "elsewhere"), "Other");

        var resolved = ArtifactSourceStamp.TryResolveComponentProjectFile(
            repository,
            Path.Join("..", "elsewhere", "Other"));

        Assert.Null(resolved);
    }

    [Fact]
    public void TryCollectProjectClosure_IncludesTransitiveReferences()
    {
        var repository = CreateRepository("repo");
        WriteProject(repository, "Sample.Core");
        WriteProject(repository, "Sample.Runtime", [@"..\Sample.Core\Sample.Core.csproj"]);
        var web = WriteProject(repository, "Sample.Web", [@"..\Sample.Runtime\Sample.Runtime.csproj"]);

        var closure = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var collected = ArtifactSourceStamp.TryCollectProjectClosure(web, repository, closure, depth: 0);

        Assert.True(collected, ArtifactSourceStamp.DeclineReason ?? "closure was declined");
        Assert.Equal(3, closure.Count);
        Assert.Contains(closure, path => path.EndsWith("Sample.Core.csproj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TryCollectProjectClosure_ResolvesACrossRepositoryReferenceThroughAnMsBuildProperty()
    {
        // The exact shape IbsPackager uses to reach OpenModulePlatform.Web.Shared, and the case
        // that Path.Join silently broke: the expanded property is already an absolute path.
        var platform = CreateRepository("platform");
        WriteProject(platform, "Platform.Shared");

        var repository = CreateRepository("repo");
        File.WriteAllText(
            Path.Join(repository, "Directory.Build.targets"),
            """
            <Project>
              <PropertyGroup>
                <PlatformRoot Condition="'$(PlatformRoot)' == ''">$(MSBuildThisFileDirectory)..\platform</PlatformRoot>
                <PlatformRoot>$([System.IO.Path]::GetFullPath('$(PlatformRoot)'))</PlatformRoot>
              </PropertyGroup>
            </Project>
            """);
        var web = WriteProject(
            repository,
            "Sample.Web",
            [@"$(PlatformRoot)\Platform.Shared\Platform.Shared.csproj"]);

        var closure = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var collected = ArtifactSourceStamp.TryCollectProjectClosure(web, repository, closure, depth: 0);

        Assert.True(collected, ArtifactSourceStamp.DeclineReason ?? "closure was declined");
        Assert.Contains(
            closure,
            path => path.EndsWith("Platform.Shared.csproj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TryCollectProjectClosure_DeclinesWhenAPropertyCannotBeExpanded()
    {
        // Declining is the safe outcome: the caller falls back to the repository-wide stamp, which
        // rebuilds too much rather than too little. Under-scoping would ship a stale artifact.
        var repository = CreateRepository("repo");
        var web = WriteProject(repository, "Sample.Web", [@"$(UndefinedRoot)\Thing\Thing.csproj"]);

        var closure = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var collected = ArtifactSourceStamp.TryCollectProjectClosure(web, repository, closure, depth: 0);

        Assert.False(collected);
        Assert.NotNull(ArtifactSourceStamp.DeclineReason);
    }

    [Fact]
    public void TryCollectProjectClosure_ExcludesASiblingProjectThatIsNotReferenced()
    {
        // The heart of R8-P6-3: ibs-packager-worker must not rebuild when the FileDrop channel
        // type changes, because it does not reference it.
        var repository = CreateRepository("repo");
        WriteProject(repository, "Sample.Core");
        WriteProject(repository, "Sample.ChannelType", [@"..\Sample.Core\Sample.Core.csproj"]);
        var worker = WriteProject(repository, "Sample.Worker", [@"..\Sample.Core\Sample.Core.csproj"]);

        var closure = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.True(ArtifactSourceStamp.TryCollectProjectClosure(worker, repository, closure, depth: 0));

        Assert.DoesNotContain(
            closure,
            path => path.EndsWith("Sample.ChannelType.csproj", StringComparison.OrdinalIgnoreCase));
    }

    private string CreateRepository(string name)
    {
        var path = Path.Join(_root, name);
        Directory.CreateDirectory(path);

        // The property walk stops at the repository root, which it recognises by .git.
        Directory.CreateDirectory(Path.Join(path, ".git"));
        return path;
    }

    private static string WriteProject(
        string repository,
        string name,
        IReadOnlyList<string>? projectReferences = null)
    {
        var directory = Path.Join(repository, name);
        Directory.CreateDirectory(directory);

        var references = string.Join(
            Environment.NewLine,
            (projectReferences ?? []).Select(reference =>
                $"""    <ProjectReference Include="{reference}" />"""));

        var projectPath = Path.Join(directory, name + ".csproj");
        File.WriteAllText(
            projectPath,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
            {references}
              </ItemGroup>
            </Project>
            """);
        return projectPath;
    }
}

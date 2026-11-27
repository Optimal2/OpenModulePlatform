namespace OpenModulePlatform.Bootstrapper.Tests;

/// <summary>
/// The package-root runner refuses to refresh when it cannot show that it was built from the
/// installer sources next to it. Measured 2026-11-27: a runner five weeks behind the sources
/// hashed with old path ordering and refused 27 byte-identical artifacts.
/// </summary>
public sealed class RunnerSourceStampTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "omp-runner-stamp-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _repository;
    private readonly string _packageRoot;
    private readonly string _runner;

    public RunnerSourceStampTests()
    {
        _repository = Path.Join(_root, "repo");
        _packageRoot = Path.Join(_root, "package");
        Directory.CreateDirectory(Path.Join(_repository, ".git"));
        Directory.CreateDirectory(Path.Join(_repository, "OpenModulePlatform.Bootstrapper"));
        File.WriteAllText(
            RunnerSourceStamp.GetRunnerProjectPath(_repository),
            "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(Path.Join(_repository, "OpenModulePlatform.Bootstrapper", "Program.cs"), "// v1");
        Directory.CreateDirectory(_packageRoot);
        _runner = Path.Join(_packageRoot, "OpenModulePlatform.Bootstrapper.exe");
        File.WriteAllText(_runner, "runner");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void AStampWrittenFromTheCurrentSourcesPasses()
    {
        RunnerSourceStamp.Write(_runner, _repository);

        var result = RunnerSourceStamp.Evaluate(_runner, _packageRoot, _repository);

        Assert.False(result.Refuse, result.Message);
    }

    [Fact]
    public void ASourceChangeAfterTheStampRefuses()
    {
        RunnerSourceStamp.Write(_runner, _repository);
        File.WriteAllText(Path.Join(_repository, "OpenModulePlatform.Bootstrapper", "Program.cs"), "// v2");

        var result = RunnerSourceStamp.Evaluate(_runner, _packageRoot, _repository);

        Assert.True(result.Refuse);
        Assert.Contains("update-installer-runner-only.ps1", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingStampRefuses()
    {
        var result = RunnerSourceStamp.Evaluate(_runner, _packageRoot, _repository);

        Assert.True(result.Refuse);
        Assert.Contains(RunnerSourceStamp.StampFileSuffix, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunnerOutsideThePackageRootIsNotChecked()
    {
        var buildOutputRunner = Path.Join(_root, "bin", "OpenModulePlatform.Bootstrapper.exe");

        var result = RunnerSourceStamp.Evaluate(buildOutputRunner, _packageRoot, _repository);

        Assert.False(result.Refuse, result.Message);
    }

    [Fact]
    public void NoConfiguredInstallerSourceIsNotChecked()
    {
        Assert.Null(RunnerSourceStamp.FindRepositoryRoot([Path.Join(_root, "other")]));

        var result = RunnerSourceStamp.Evaluate(_runner, _packageRoot, repositoryRoot: null);

        Assert.False(result.Refuse, result.Message);
    }

    [Fact]
    public void TheStampDoesNotDependOnTheCheckoutPath()
    {
        var copy = Path.Join(_root, "copy");
        Directory.CreateDirectory(Path.Join(copy, ".git"));
        Directory.CreateDirectory(Path.Join(copy, "OpenModulePlatform.Bootstrapper"));
        foreach (var file in Directory.EnumerateFiles(Path.Join(_repository, "OpenModulePlatform.Bootstrapper")))
        {
            File.Copy(file, Path.Join(copy, "OpenModulePlatform.Bootstrapper", Path.GetFileName(file)));
        }

        Assert.Equal(RunnerSourceStamp.Compute(_repository), RunnerSourceStamp.Compute(copy));
    }
}

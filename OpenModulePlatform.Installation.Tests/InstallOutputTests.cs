using OpenModulePlatform.Installation;

namespace OpenModulePlatform.Installation.Tests;

[Collection(InstallOutputTestCollection.Name)]
public sealed class InstallOutputTests
{
    [Fact]
    public void Current_DefaultsToConsoleProgress()
    {
        Assert.IsType<ConsoleInstallProgress>(InstallOutput.Current);
    }

    [Fact]
    public void CustomProgress_ReceivesInfoAndError()
    {
        var original = InstallOutput.Current;
        var sink = new CollectingProgress();
        try
        {
            InstallOutput.Current = sink;
            InstallOutput.Info("step one");
            InstallOutput.Info();
            InstallOutput.Error("bad news");

            Assert.Equal(["step one", ""], sink.InfoLines);
            Assert.Equal(["bad news"], sink.ErrorLines);
        }
        finally
        {
            InstallOutput.Current = original;
        }
    }

    private sealed class CollectingProgress : IInstallProgress
    {
        public List<string> InfoLines { get; } = [];

        public List<string> ErrorLines { get; } = [];

        public void Info(string message) => InfoLines.Add(message);

        public void Error(string message) => ErrorLines.Add(message);
    }
}

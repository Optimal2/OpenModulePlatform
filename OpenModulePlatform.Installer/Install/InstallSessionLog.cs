using System.Text;
using OpenModulePlatform.Installation;

namespace OpenModulePlatform.Installer.Install;

/// <summary>
/// The installer's log file, written next to the executable, plus a progress
/// fan-out so every line reaches both the UI and the file.
/// </summary>
public sealed class InstallSessionLog : IInstallProgress, IDisposable
{
    private readonly StreamWriter _writer;

    private InstallSessionLog(string path)
    {
        Path = path;
        _writer = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
    }

    public string Path { get; }

    public static InstallSessionLog CreateNextToExecutable()
    {
        var fileName = "OpenModulePlatform.Installer-"
            + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)
            + ".log";
        return new InstallSessionLog(System.IO.Path.Join(AppContext.BaseDirectory, fileName));
    }

    public void Info(string message) => Write(message);

    public void Error(string message) => Write("ERROR: " + message);

    private void Write(string message)
    {
        _writer.WriteLine(DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "  " + message);
    }

    public void Dispose() => _writer.Dispose();
}

/// <summary>Fans progress lines out to several sinks (UI list + log file).</summary>
public sealed class CompositeInstallProgress(IReadOnlyList<IInstallProgress> sinks) : IInstallProgress
{
    public void Info(string message)
    {
        foreach (var sink in sinks)
        {
            sink.Info(message);
        }
    }

    public void Error(string message)
    {
        foreach (var sink in sinks)
        {
            sink.Error(message);
        }
    }
}

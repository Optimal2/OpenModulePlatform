namespace OpenModulePlatform.Installation;

/// <summary>
/// Receives progress and log output from the installation engine. The Bootstrapper
/// keeps its historical console output through <see cref="ConsoleInstallProgress"/>;
/// a GUI (or the first-install-only installer) supplies its own implementation to
/// show steps in its own UI.
/// </summary>
public interface IInstallProgress
{
    /// <summary>Writes a normal progress/information line (historically Console.Out).</summary>
    void Info(string message);

    /// <summary>Writes an error line (historically Console.Error).</summary>
    void Error(string message);
}

/// <summary>
/// Ambient progress sink used by <see cref="InstallationEngine"/>. Defaults to the
/// console, which preserves the exact historical Bootstrapper output; installers with
/// their own UI assign <see cref="Current"/> before running install steps. The
/// installation flow is single-threaded, so a process-wide sink matches the previous
/// Console.WriteLine behaviour one-to-one.
/// </summary>
public static class InstallOutput
{
    public static IInstallProgress Current { get; set; } = new ConsoleInstallProgress();

    internal static void Info(string message) => Current.Info(message);

    internal static void Info() => Current.Info(string.Empty);

    internal static void Error(string message) => Current.Error(message);
}

/// <summary>Default progress sink: identical to the historical console output.</summary>
public sealed class ConsoleInstallProgress : IInstallProgress
{
    public void Info(string message) => Console.WriteLine(message);

    public void Error(string message) => Console.Error.WriteLine(message);
}

namespace OpenModulePlatform.Installer.Install;

/// <summary>
/// The last-resort crash handler for the WinForms app: shows the failing step
/// and the log path, and never prints secrets (the exception's Message only -
/// never a dump, and the install request's ToString redacts the password).
/// </summary>
internal static class CrashReporter
{
    /// <summary>The active session log path, set when an install run starts.</summary>
    public static string? LogPath { get; set; }

    /// <summary>The current install step (the last "&gt; ..." progress line).</summary>
    public static string? CurrentStep { get; set; }

    /// <summary>The operator-facing one-line description of a crash.</summary>
    public static string Describe(Exception? exception)
    {
        var message = exception is null ? "Unknown error." : exception.Message;
        var step = string.IsNullOrWhiteSpace(CurrentStep) ? null : CurrentStep;
        var text = step is null
            ? "The installation failed unexpectedly: " + message
            : $"The installation failed unexpectedly at step \"{step}\": {message}";
        return string.IsNullOrWhiteSpace(LogPath)
            ? text
            : text + Environment.NewLine + Environment.NewLine + "Log file: " + LogPath;
    }

    /// <summary>Shows the crash in a message box. Best-effort: never throws.</summary>
    public static void Show(Exception? exception)
    {
        try
        {
            System.Windows.Forms.MessageBox.Show(
                Describe(exception),
                "OpenModulePlatform installation",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
        }
        catch (Exception)
        {
            // A crash handler must never throw.
        }
    }
}

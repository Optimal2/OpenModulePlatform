using System.Diagnostics;
using System.Text;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

internal static class HostAgentProcessRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StreamDrainTimeout = TimeSpan.FromSeconds(5);

    private const string StreamDrainIncompleteNote =
        "(output was truncated: the process exited but its redirected streams stayed open, "
        + "which usually means a child process inherited them)";

    public static HostAgentProcessResult Run(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan? timeout = null,
        IEnumerable<string>? sensitiveValues = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var argumentList = arguments.ToArray();
        var sensitiveList = sensitiveValues?.Where(value => !string.IsNullOrEmpty(value)).ToArray() ?? [];
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in argumentList)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}' with arguments: {FormatArguments(argumentList, sensitiveList)}.");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        var effectiveTimeout = timeout.GetValueOrDefault(DefaultTimeout);
        if (effectiveTimeout <= TimeSpan.Zero)
        {
            effectiveTimeout = DefaultTimeout;
        }

        if (!process.WaitForExit(ToMilliseconds(effectiveTimeout)))
        {
            TryKillProcessTree(process);
            TryWaitForExit(process, StreamDrainTimeout);

            var output = TryReadCompletedTask(outputTask);
            var error = TryReadCompletedTask(errorTask);
            throw new TimeoutException(
                $"Process '{Path.GetFileName(fileName)}' did not exit within {effectiveTimeout.TotalSeconds:0.#} seconds. Arguments: {FormatArguments(argumentList, sensitiveList)}.{CreateOutputDiagnostic(output, error, sensitiveList)}");
        }

        // R9-A2. The process exiting does not mean its pipes closed. A grandchild that
        // inherited the redirected handles keeps them open, and ReadToEndAsync then never
        // completes -- so these two lines could block the HostAgent cycle forever. The
        // path runs icacls, appcmd and netsh, all of which can spawn helpers.
        //
        // WaitForExit() with no argument would drain the readers for us, but it has the
        // same unbounded wait. Bound the drain instead and take whatever arrived; the exit
        // code is the part callers act on, and the timeout branch above already treats
        // partial output as acceptable.
        var drained = Task.WhenAll(outputTask, errorTask).Wait(ToMilliseconds(StreamDrainTimeout));
        var stdOut = TryReadCompletedTask(outputTask);
        var stdErr = TryReadCompletedTask(errorTask);
        if (!drained)
        {
            stdErr = string.IsNullOrEmpty(stdErr)
                ? StreamDrainIncompleteNote
                : stdErr + Environment.NewLine + StreamDrainIncompleteNote;
        }

        return new HostAgentProcessResult(process.ExitCode, stdOut, stdErr);
    }

    public static string FormatArguments(IEnumerable<string> arguments)
        => FormatArguments(arguments, null);

    public static string FormatArguments(IEnumerable<string> arguments, IEnumerable<string>? sensitiveValues)
    {
        var sensitiveList = sensitiveValues?.Where(value => !string.IsNullOrEmpty(value)).ToArray();
        var formatted = string.Join(
            " ",
            arguments.Select(static argument => argument.Contains(' ', StringComparison.Ordinal)
                ? '"' + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + '"'
                : argument));

        return RedactSensitiveValues(formatted, sensitiveList);
    }

    internal static string RedactSensitiveValues(string value, IEnumerable<string>? sensitiveValues)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var sensitiveList = sensitiveValues?.Where(s => !string.IsNullOrEmpty(s)).ToArray();
        if (sensitiveList is null || sensitiveList.Length == 0)
        {
            return value;
        }

        // Redact longer values first so that shorter values do not leave partial leaks.
        foreach (var sensitive in sensitiveList.OrderByDescending(static s => s.Length))
        {
            value = value.Replace(sensitive, "[REDACTED]", StringComparison.Ordinal);
        }

        return value;
    }

    private static int ToMilliseconds(TimeSpan timeout)
    {
        var totalMilliseconds = Math.Ceiling(timeout.TotalMilliseconds);
        if (totalMilliseconds >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return Math.Max(1, (int)totalMilliseconds);
    }

    private static bool TryWaitForExit(Process process, TimeSpan timeout)
    {
        try
        {
            return process.WaitForExit(ToMilliseconds(timeout));
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the timeout check and Kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The timeout exception below is still the operationally relevant failure.
        }
    }

    private static string TryReadCompletedTask(Task<string> task)
    {
        if (!task.IsCompletedSuccessfully)
        {
            return string.Empty;
        }

        return task.GetAwaiter().GetResult();
    }

    private static string CreateOutputDiagnostic(string output, string error, string[]? sensitiveValues = null)
    {
        var builder = new StringBuilder();
        AppendDiagnostic(builder, "stdout", output, sensitiveValues);
        AppendDiagnostic(builder, "stderr", error, sensitiveValues);
        return builder.Length == 0 ? string.Empty : " " + builder;
    }

    private static void AppendDiagnostic(StringBuilder builder, string name, string value, string[]? sensitiveValues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = RedactSensitiveValues(value.Trim(), sensitiveValues);
        const int maxLength = 1000;
        if (normalized.Length > maxLength)
        {
            normalized = normalized[..maxLength] + "...";
        }

        builder.Append(name).Append(": ").Append(normalized).Append(' ');
    }
}

internal sealed record HostAgentProcessResult(int ExitCode, string StdOut, string StdErr);

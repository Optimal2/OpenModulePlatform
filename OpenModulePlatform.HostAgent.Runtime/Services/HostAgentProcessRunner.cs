using System.Diagnostics;
using System.Text;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

internal static class HostAgentProcessRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StreamDrainTimeout = TimeSpan.FromSeconds(5);

    public static HostAgentProcessResult Run(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var argumentList = arguments.ToArray();
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
            ?? throw new InvalidOperationException($"Failed to start '{fileName}' with arguments: {FormatArguments(argumentList)}.");

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
                $"Process '{Path.GetFileName(fileName)}' did not exit within {effectiveTimeout.TotalSeconds:0.#} seconds. Arguments: {FormatArguments(argumentList)}.{CreateOutputDiagnostic(output, error)}");
        }

        var stdOut = outputTask.GetAwaiter().GetResult();
        var stdErr = errorTask.GetAwaiter().GetResult();
        return new HostAgentProcessResult(process.ExitCode, stdOut, stdErr);
    }

    public static string FormatArguments(IEnumerable<string> arguments)
        => string.Join(
            " ",
            arguments.Select(static argument => argument.Contains(' ', StringComparison.Ordinal)
                ? '"' + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + '"'
                : argument));

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

    private static string CreateOutputDiagnostic(string output, string error)
    {
        var builder = new StringBuilder();
        AppendDiagnostic(builder, "stdout", output);
        AppendDiagnostic(builder, "stderr", error);
        return builder.Length == 0 ? string.Empty : " " + builder;
    }

    private static void AppendDiagnostic(StringBuilder builder, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = value.Trim();
        const int maxLength = 1000;
        if (normalized.Length > maxLength)
        {
            normalized = normalized[..maxLength] + "...";
        }

        builder.Append(name).Append(": ").Append(normalized).Append(' ');
    }
}

internal sealed record HostAgentProcessResult(int ExitCode, string StdOut, string StdErr);

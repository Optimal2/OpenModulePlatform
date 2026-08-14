// File: OpenModulePlatform.HostAgent.Runtime/Services/OmpProcessStreamDrain.cs
namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>
/// Collects what a finished child process wrote, without ever waiting on it forever.
/// </summary>
/// <remarks>
/// R11-B4. This logic was built up over three rounds inside
/// <see cref="HostAgentProcessRunner"/> and lived there as private members -- so the
/// Bootstrapper's own RunProcess, which drives the same tools (sc, icacls, appcmd, netsh,
/// dotnet) through the same redirected pipes, had none of it. That is the campaign's
/// dominant defect exactly: the hardened version was private, so the sibling caller did
/// without it. Extracted here so there is one implementation and both callers reach it.
///
/// The reasoning it encodes:
///
/// R9-A2 -- a process exiting does not mean its pipes closed. A grandchild that inherited
/// the redirected handles keeps them open, and ReadToEndAsync then never completes. Every
/// one of these tools can spawn helpers. Process.WaitForExit() with no argument waits for
/// stream EOF as well as for exit, so it is not an escape either; it has the same
/// unbounded wait wearing different clothes.
///
/// R10-S1 -- inspect the reads, never throw them. Task.WhenAll(...).Wait() rethrows a
/// faulted read wrapped in an AggregateException, and no exception filter along these
/// paths lists that type, so a broken pipe escaped and aborted the whole cycle: the very
/// failure R9-A2 had been written to prevent, reintroduced by its own fix. WhenAny's task
/// never faults, so the outcome is examined rather than raised.
///
/// What a caller gets is whatever arrived before the deadline, plus a note on stderr when
/// something was missed. The exit code is the part callers act on, and every caller
/// already treats partial output as acceptable.
/// </remarks>
public static class OmpProcessStreamDrain
{
    /// <summary>
    /// How long to wait for redirected streams to finish after the process has exited.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Appended to stderr when the streams never reached EOF.
    /// </summary>
    public const string IncompleteNote =
        "(output was truncated: the process exited but its redirected streams stayed open, "
        + "which usually means a child process inherited them)";

    /// <summary>
    /// Waits up to <see cref="Timeout"/> for both reads, then returns what arrived.
    /// </summary>
    public static (string StdOut, string StdErr) Drain(Task<string> outputTask, Task<string> errorTask)
    {
        ArgumentNullException.ThrowIfNull(outputTask);
        ArgumentNullException.ThrowIfNull(errorTask);

        var deadline = Task.Delay(Timeout);
        var settled = Task.WhenAny(Task.WhenAll(outputTask, errorTask), deadline)
            .GetAwaiter()
            .GetResult();

        var stdOut = ReadCompletedOrEmpty(outputTask);
        var stdErr = ReadCompletedOrEmpty(errorTask);

        var note = ReferenceEquals(settled, deadline)
            ? IncompleteNote
            : DescribeFaultedReads(outputTask, errorTask);
        if (note is not null)
        {
            stdErr = string.IsNullOrEmpty(stdErr) ? note : stdErr + Environment.NewLine + note;
        }

        return (stdOut, stdErr);
    }

    /// <summary>
    /// The read's result if it succeeded, and an empty string for every other outcome --
    /// faulted, cancelled or still running. Never throws and never blocks.
    /// </summary>
    public static string ReadCompletedOrEmpty(Task<string> task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.IsCompletedSuccessfully ? task.GetAwaiter().GetResult() : string.Empty;
    }

    /// <summary>
    /// A diagnostic line naming the reads that did not succeed, or null when both did.
    /// </summary>
    /// <remarks>
    /// A read that faulted is not the same as one that timed out, and the difference is
    /// what tells an operator whether to look at the tool or at the machine. Reporting it
    /// on stderr keeps a broken pipe visible without turning it into a failed deployment.
    /// </remarks>
    public static string? DescribeFaultedReads(Task<string> outputTask, Task<string> errorTask)
    {
        ArgumentNullException.ThrowIfNull(outputTask);
        ArgumentNullException.ThrowIfNull(errorTask);

        var reasons = new List<string>(2);
        AppendFault(reasons, "stdout", outputTask);
        AppendFault(reasons, "stderr", errorTask);
        return reasons.Count == 0 ? null : "(output was incomplete: " + string.Join("; ", reasons) + ")";

        static void AppendFault(List<string> reasons, string name, Task<string> task)
        {
            if (task.IsCompletedSuccessfully)
            {
                return;
            }

            var reason = task.Exception?.GetBaseException().Message ?? "the read did not complete";
            reasons.Add($"{name} could not be read ({reason})");
        }
    }
}

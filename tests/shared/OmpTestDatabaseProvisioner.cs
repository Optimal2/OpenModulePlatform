// File: tests/shared/OmpTestDatabaseProvisioner.cs
//
// Linked into every test project that provisions its own database. There is exactly one
// copy on purpose: seven fixtures used to open a connection and issue CREATE DATABASE
// independently, and a fix applied to one of them would have left the other six.
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.TestSupport;

/// <summary>
/// Creates test databases one at a time, machine-wide.
/// </summary>
/// <remarks>
/// <para>
/// CREATE DATABASE copies the <c>model</c> database, and to do that it takes an exclusive
/// lock on it. Concurrent creates therefore queue behind each other, and on a slow agent
/// disk that queue outlasts the 30-second default command timeout. CI ran four test
/// assemblies in parallel, each with xUnit running its collections in parallel, each
/// fixture creating a database -- so runs failed with
/// "Could not obtain exclusive lock on database 'model'" and a wall of timeouts, in
/// fixture constructors rather than in any test body.
/// </para>
/// <para>
/// The failures were intermittent and hit unrelated commits, which is what identified this
/// as contention rather than a code defect. The fix serialises the one operation that
/// genuinely cannot run concurrently and leaves everything else parallel.
/// </para>
/// <para>
/// The lock body is deliberately synchronous from end to end. <see cref="Mutex"/> has
/// thread affinity: it must be released by the thread that took it, and an <c>await</c>
/// inside the lock can resume on a different pool thread. The first version of this class
/// did exactly that and turned four failing tests into thirty-five, every one of them
/// "Object synchronization method was called from an unsynchronized block of code".
/// Async callers get a <see cref="Task.Run"/> wrapper instead, so acquire, execute and
/// release all happen on one thread.
/// </para>
/// </remarks>
public static class OmpTestDatabaseProvisioner
{
    /// <summary>Machine-wide, so it holds across the separate test-assembly processes.</summary>
    private const string CreateDatabaseMutexName = @"Global\OpenModulePlatform.Tests.CreateDatabase";

    /// <summary>
    /// Long enough for a cold CI agent to copy <c>model</c> while other work is queued
    /// behind the same lock. The default 30 seconds is what CI kept exceeding.
    /// </summary>
    private const int CreateDatabaseCommandTimeoutSeconds = 180;

    /// <summary>How long to wait for another process to finish its own creation.</summary>
    private static readonly TimeSpan MutexWaitTimeout = TimeSpan.FromMinutes(5);

    private const int MaxAttempts = 3;

    /// <summary>
    /// Runs <paramref name="createStatement"/> with the machine-wide creation lock held.
    /// </summary>
    /// <param name="masterConnectionString">A connection string pointing at <c>master</c>.</param>
    /// <param name="createStatement">The CREATE DATABASE statement to execute.</param>
    public static Task CreateDatabaseAsync(string masterConnectionString, string createStatement)
        => Task.Run(() => CreateDatabaseCore(masterConnectionString, createStatement));

    /// <summary>Synchronous overload for fixtures whose constructor cannot await.</summary>
    public static void CreateDatabase(string masterConnectionString, string createStatement)
        => CreateDatabaseCore(masterConnectionString, createStatement);

    private static void CreateDatabaseCore(string masterConnectionString, string createStatement)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                ExecuteUnderCreationLock(masterConnectionString, createStatement);
                return;
            }
            catch (SqlException ex) when (attempt < MaxAttempts && IsTransientCreationFailure(ex))
            {
                // Someone outside this process family holds the lock -- another build on
                // the same agent, or a leftover connection. Backing off and retrying is
                // right; failing the run is not, because nothing is actually wrong.
                Thread.Sleep(TimeSpan.FromSeconds(5 * attempt));
            }
        }
    }

    private static void ExecuteUnderCreationLock(string masterConnectionString, string createStatement)
    {
        using var mutex = new Mutex(initiallyOwned: false, CreateDatabaseMutexName);
        var acquired = false;

        try
        {
            acquired = mutex.WaitOne(MutexWaitTimeout);
        }
        catch (AbandonedMutexException)
        {
            // The previous holder died without releasing. The lock is ours, and the
            // database it was creating is not our problem.
            acquired = true;
        }

        try
        {
            // A timed-out wait is not a reason to give up: proceeding unserialised is what
            // the code did before, so the worst case is the old behaviour rather than a
            // test run that refuses to start.
            using var conn = new SqlConnection(masterConnectionString);
            conn.Open();
            using var cmd = new SqlCommand(createStatement, conn)
            {
                CommandTimeout = CreateDatabaseCommandTimeoutSeconds
            };
            cmd.ExecuteNonQuery();
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    /// <summary>
    /// True for failures that mean "try again", not "this is broken".
    /// </summary>
    private static bool IsTransientCreationFailure(SqlException exception)
    {
        foreach (SqlError error in exception.Errors)
        {
            // -2: command timeout. 1807: could not obtain exclusive lock on database 'model'.
            if (error.Number is -2 or 1807)
            {
                return true;
            }
        }

        return false;
    }
}

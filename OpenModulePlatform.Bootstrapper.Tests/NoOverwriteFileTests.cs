using System.Text;

namespace OpenModulePlatform.Bootstrapper.Tests;

/// <summary>
/// The package writers must never replace an existing file, also not when
/// another writer creates the same name while they run, and a refusal must
/// arrive as <see cref="OutputFileExistsException"/> with an explanation
/// instead of a bare <see cref="IOException"/>.
/// </summary>
public sealed class NoOverwriteFileTests : IDisposable
{
    private readonly string _testRoot = Path.Join(
        Path.GetTempPath(),
        "omp-nooverwrite-tests-" + Guid.NewGuid().ToString("N"));

    public NoOverwriteFileTests() => Directory.CreateDirectory(_testRoot);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    [Fact]
    public void WriteCreatesTheFileAndLeavesNoTemporaryFile()
    {
        var path = Path.Join(_testRoot, "out", "package.zip");

        NoOverwriteFile.Write(path, stream => stream.Write(Encoding.UTF8.GetBytes("first")));

        Assert.Equal("first", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void WriteRefusesAnExistingFileWithoutTouchingIt()
    {
        var path = Path.Join(_testRoot, "package.zip");
        File.WriteAllText(path, "existing");
        var called = false;

        var ex = Assert.Throws<OutputFileExistsException>(
            () => NoOverwriteFile.Write(path, _ => called = true));

        Assert.False(called);
        Assert.Equal(Path.GetFullPath(path), ex.Path);
        Assert.Contains("already exists", ex.Message, StringComparison.Ordinal);
        Assert.Equal("existing", File.ReadAllText(path));
    }

    [Fact]
    public void ASecondWriterDuringTheFirstWriteIsRefused()
    {
        // The second writer starts while the first is still producing content:
        // the name is already claimed, so the second fails before writing and
        // the first finishes with its own content.
        var path = Path.Join(_testRoot, "package.zip");
        OutputFileExistsException? second = null;

        NoOverwriteFile.Write(path, stream =>
        {
            second = Assert.Throws<OutputFileExistsException>(
                () => NoOverwriteFile.Write(path, inner => inner.Write(Encoding.UTF8.GetBytes("second"))));
            stream.Write(Encoding.UTF8.GetBytes("first"));
        });

        Assert.NotNull(second);
        Assert.Equal(Path.GetFullPath(path), second.Path);
        Assert.Equal("first", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_testRoot));
    }

    [Fact]
    public void AccessDeniedDuringCompetingPublishIsReportedAsExistingFile()
    {
        var path = Path.Join(_testRoot, "package.zip");
        var denied = new UnauthorizedAccessException("Simulated Windows rename/delete contention");
        var called = false;
        NoOverwriteFile.Write(path, stream =>
        {
            var ex = Assert.Throws<OutputFileExistsException>(() => NoOverwriteFile.Write(
                path, _ => called = true, createClaim: _ => throw denied));
            Assert.Same(denied, ex.InnerException);
            stream.Write(Encoding.UTF8.GetBytes("winner"));
        });
        Assert.False(called);
        Assert.Equal("winner", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_testRoot));
    }

    [Fact]
    public void AccessDeniedWhileNameIsTemporarilyInvisibleRetriesTheAtomicClaim()
    {
        var path = Path.Join(_testRoot, "package.zip");
        var attempts = 0;
        NoOverwriteFile.Write(path, stream => stream.Write(Encoding.UTF8.GetBytes("winner")), createClaim: name =>
        {
            if (++attempts == 1)
            {
                throw new UnauthorizedAccessException("Simulated delete-pending name");
            }
            return new FileStream(name, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        });
        Assert.Equal(2, attempts);
        Assert.Equal("winner", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_testRoot));
    }

    [Fact]
    public void ContentionOutlivingTheOldRetryWindowStillClaimsTheName()
    {
        // Delete-pending contention can outlive a short fixed retry budget
        // while the name stays invisible to File.Exists. Denials that resolve
        // inside the bounded window must still claim the name instead of
        // escaping as a raw UnauthorizedAccessException.
        var path = Path.Join(_testRoot, "package.zip");
        var attempts = 0;
        NoOverwriteFile.Write(path, stream => stream.Write(Encoding.UTF8.GetBytes("winner")), createClaim: name =>
        {
            if (++attempts <= 8)
            {
                throw new UnauthorizedAccessException("Simulated delete-pending name");
            }

            return new FileStream(name, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        });
        Assert.Equal(9, attempts);
        Assert.Equal("winner", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_testRoot));
    }

    [Fact]
    public void ATransientHandleOnTheClaimStillPublishes()
    {
        // A scanner-like handle on the claim (no FILE_SHARE_DELETE) denies
        // the final rename while it is held. A handle that goes away inside
        // the bounded window must not fail the build or leave a reservation
        // behind; only a handle that persists across the whole window warns.
        var path = Path.Join(_testRoot, "package.zip");
        var warnings = new List<string>();
        FileStream? blocker = null;
        using var timer = new Timer(_ => blocker?.Dispose(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            NoOverwriteFile.Write(
                path,
                stream =>
                {
                    blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

                    // Arm the release only once the handle exists. A timer armed
                    // before the write starts can expire while the claim is still
                    // being created, which would leave the handle held for the
                    // whole publish window instead of releasing inside it.
                    timer.Change(150, Timeout.Infinite);
                    stream.Write(Encoding.UTF8.GetBytes("content"));
                },
                warnings.Add);
        }
        finally
        {
            blocker?.Dispose();
        }

        Assert.Equal("content", File.ReadAllText(path));
        Assert.Empty(warnings);
        Assert.Equal([path], Directory.GetFiles(_testRoot));
    }

    [Fact]
    public async Task PublishRetryNeverReplacesAFileThatIsNoLongerItsOwnClaim()
    {
        // Force the first rename to fail. A second handle on the temporary file
        // keeps every rename denied while another writer atomically takes the
        // name, so the next retry cannot race the foreign publish itself.
        var path = Path.Join(_testRoot, "package.zip");
        var foreignPath = Path.Join(_testRoot, "foreign.tmp");
        File.WriteAllText(foreignPath, "foreign");
        FileStream? claimBlocker = null;
        Task? replaceClaim = null;
        string? temporaryPackage = null;
        Exception? publishException;
        try
        {
            publishException = Record.Exception(() => NoOverwriteFile.Write(path, stream =>
            {
                var targetBlocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                claimBlocker = targetBlocker;
                var publishThread = Thread.CurrentThread;
                replaceClaim = Task.Run(() =>
                {
                    var tempPath = Directory.GetFiles(
                        _testRoot, $".{Path.GetFileName(path)}.*.tmp").Single();
                    temporaryPackage = tempPath;
                    FileStream tempBlocker;

                    // Generous enough that no realistic load reaches it, so the
                    // handshake stays load-independent, but bounded: if the
                    // temporary file is gone the open can never succeed, and an
                    // unbounded loop would hang the suite instead of naming it.
                    var tempOpenUntil = DateTime.UtcNow.AddSeconds(30);
                    for (;;)
                    {
                        try
                        {
                            tempBlocker = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                            break;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            if (DateTime.UtcNow >= tempOpenUntil)
                            {
                                throw new IOException(
                                    $"The temporary package {tempPath} never became readable, so the publish "
                                        + "retry under test was never reached.",
                                    ex);
                            }

                            // The writer still owns the stream. Wait for its close,
                            // not for a tight deadline that a loaded machine can miss.
                            Thread.Sleep(1);
                        }
                    }

                    using (tempBlocker)
                    {
                        // After the temporary stream closes, the publish retry is
                        // the only managed wait in Write. Observing it proves that
                        // the first rename was denied before the foreign publish;
                        // a fixed delay cannot provide that ordering guarantee.
                        // Bounded: with the publish retry removed the thread never
                        // waits, and an unbounded spin would hang the suite instead
                        // of naming the regression.
                        Assert.True(
                            SpinWait.SpinUntil(
                                () => (publishThread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                                TimeSpan.FromSeconds(30)),
                            "The publish never waited to retry, so the first rename was not denied.");
                        targetBlocker.Dispose();

                        // Closing the target blocker can leave its name transiently
                        // delete-pending on Windows. The foreign publish must tolerate
                        // that contention instead of turning it into a test failure.
                        // The temporary-file blocker prevents our package from publishing
                        // until the foreign file has atomically taken the target name.
                        var foreignPublishUntil = DateTime.UtcNow.AddSeconds(2);
                        for (;;)
                        {
                            try
                            {
                                File.Move(foreignPath, path, overwrite: true);
                                break;
                            }
                            catch (Exception ex)
                                when ((ex is IOException or UnauthorizedAccessException)
                                    && DateTime.UtcNow < foreignPublishUntil)
                            {
                                Thread.Sleep(1);
                            }
                        }
                    }
                });
                stream.Write(Encoding.UTF8.GetBytes("mine"));
            }));
        }
        finally
        {
            if (replaceClaim is not null)
            {
                await replaceClaim;
            }
            claimBlocker?.Dispose();
            if (temporaryPackage is not null)
            {
                // The injected handle can outlive the write's own cleanup attempt.
                File.Delete(temporaryPackage);
            }
        }

        Assert.Equal("foreign", File.ReadAllText(path));
        var ex = Assert.IsType<OutputFileExistsException>(publishException);
        Assert.Contains("was replaced by another file", ex.Message, StringComparison.Ordinal);
        Assert.Equal([path], Directory.GetFiles(_testRoot));
    }

    [Fact]
    public void PermanentAccessDeniedRemainsAnAccessErrorWithoutCallingWriter()
    {
        var path = Path.Join(_testRoot, "package.zip");
        var denied = new UnauthorizedAccessException("Permission denied");
        var called = false;
        var ex = Assert.Throws<UnauthorizedAccessException>(() => NoOverwriteFile.Write(
            path, _ => called = true, createClaim: _ => throw denied));
        Assert.Same(denied, ex);
        Assert.False(called);
        Assert.Empty(Directory.GetFiles(_testRoot));
    }

    [Fact]
    public async Task ConcurrentWritersNeverProduceTwoFiles()
    {
        // The invariant is "never two", not "exactly one": under contention the
        // single claim owner can still fail to publish (for example when another
        // handle on its claim makes the final rename report access denied), and
        // then nobody wins. That a lone writer succeeds is
        // WriteCreatesTheFileAndLeavesNoTemporaryFile. What may never happen is
        // a second writer getting the name, and every writer that was refused
        // the name must say why with OutputFileExistsException.
        var path = Path.Join(_testRoot, "package.zip");
        const int writers = 8;
        var owned = new bool[writers];
        var warnings = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var start = new Barrier(writers);
        var writes = Enumerable.Range(0, writers)
            .Select(index => Task.Factory.StartNew(
                () =>
                {
                    start.SignalAndWait();
                    NoOverwriteFile.Write(
                        path,
                        stream =>
                        {
                            // Only the writer that holds the claim gets to write content.
                            Volatile.Write(ref owned[index], true);
                            stream.Write(Encoding.UTF8.GetBytes("writer-" + index));
                        },
                        warnings.Enqueue);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        await Task.WhenAll(writes.Select(static write => write.ContinueWith(
            static _ => { },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default)));

        var outcomes = string.Join(
            Environment.NewLine,
            writes.Select((write, index) => $"writer-{index}: owned={owned[index]} "
                + (write.Exception is null
                    ? "published"
                    : $"{write.Exception.GetBaseException().GetType().Name}: {write.Exception.GetBaseException().Message}"))
                .Concat(warnings.Select(static warning => "warning: " + warning)));
        var owners = Enumerable.Range(0, writers).Where(index => owned[index]).ToArray();
        var winners = Enumerable.Range(0, writers).Where(index => writes[index].Exception is null).ToArray();

        Assert.True(owners.Length <= 1, "More than one writer held the name:" + Environment.NewLine + outcomes);
        Assert.True(winners.Length <= 1, "More than one writer published:" + Environment.NewLine + outcomes);
        Assert.True(winners.All(index => owned[index]), "A writer published without holding the name:" + Environment.NewLine + outcomes);
        Assert.All(
            Enumerable.Range(0, writers).Where(index => !owned[index]),
            index => Assert.True(
                writes[index].Exception?.GetBaseException() is OutputFileExistsException,
                $"writer-{index} was refused the name without OutputFileExistsException:" + Environment.NewLine + outcomes));

        if (winners.Length == 1)
        {
            Assert.Equal("writer-" + winners[0], File.ReadAllText(path));
        }

        // Only the published package may remain: a failed owner releases its
        // claim and its temporary file, so without a winner the directory is
        // empty. Cleanup that cannot delete a file reports a warning naming it
        // instead of throwing, and a leftover it warned about is designed
        // behaviour -- an external handle on the claim denies the owner both
        // the rename and the delete. A leftover nobody warned about is a leak.
        var published = winners.Length == 1 ? path : null;
        Assert.All(
            Directory.GetFiles(_testRoot).Where(file => file != published),
            file => Assert.True(
                warnings.Any(warning => warning.Contains(file, StringComparison.Ordinal)),
                $"{file} was left behind without a cleanup warning:" + Environment.NewLine + outcomes));
    }

    [Fact]
    public void WriteFailureLeavesNeitherTargetNorTemporaryFile()
    {
        var path = Path.Join(_testRoot, "package.zip");

        Assert.Throws<InvalidOperationException>(() => NoOverwriteFile.Write(path, stream =>
        {
            stream.Write(Encoding.UTF8.GetBytes("partial"));
            throw new InvalidOperationException("build failed");
        }));

        Assert.Empty(Directory.GetFiles(_testRoot));
    }

    [Theory]
    [InlineData("")]
    [InlineData("someone else's package")]
    public void WriteNeverReplacesAFileThatIsNoLongerItsOwnClaim(string foreignContent)
    {
        // While the content is written, the empty claim is deleted and another
        // file takes the name -- empty or not, it is not this writer's claim,
        // so the final rename must not replace it.
        var path = Path.Join(_testRoot, "package.zip");

        Assert.Throws<OutputFileExistsException>(() => NoOverwriteFile.Write(path, stream =>
        {
            File.Delete(path);
            File.WriteAllText(path, foreignContent);
            stream.Write(Encoding.UTF8.GetBytes("mine"));
        }));

        Assert.Equal(foreignContent, File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_testRoot));
    }

    [Fact]
    public void AnEmptyLeftoverReservationIsRefusedWithAnExplanation()
    {
        // A build killed between claiming the name and the final rename leaves
        // the empty claim behind. The next build must say what the file is.
        var path = Path.Join(_testRoot, "package.zip");
        File.WriteAllBytes(path, []);

        var ex = Assert.Throws<OutputFileExistsException>(
            () => NoOverwriteFile.Write(path, stream => stream.Write(Encoding.UTF8.GetBytes("new"))));

        Assert.Contains("empty", ex.Message, StringComparison.Ordinal);
        Assert.Contains("interrupted build", ex.Message, StringComparison.Ordinal);
        Assert.Empty(File.ReadAllBytes(path));
    }

    [Fact]
    public void AReservationThatCannotBeReleasedIsReported()
    {
        // The write fails while another handle keeps the empty claim from being
        // deleted: the leftover must be reported, not swallowed.
        var path = Path.Join(_testRoot, "package.zip");
        var warnings = new List<string>();
        FileStream? blocker = null;
        try
        {
            Assert.Throws<InvalidOperationException>(() => NoOverwriteFile.Write(
                path,
                _ =>
                {
                    blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    throw new InvalidOperationException("build failed");
                },
                warnings.Add));
        }
        finally
        {
            blocker?.Dispose();
        }

        var warning = Assert.Single(warnings);
        Assert.Contains(Path.GetFullPath(path), warning, StringComparison.Ordinal);
        Assert.Contains("could not be removed", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyRefusesAnExistingDestinationWithoutTouchingIt()
    {
        var source = Path.Join(_testRoot, "source.zip");
        var destination = Path.Join(_testRoot, "staged.zip");
        File.WriteAllText(source, "new");
        File.WriteAllText(destination, "already staged");

        var ex = Assert.Throws<OutputFileExistsException>(() => NoOverwriteFile.Copy(source, destination));

        Assert.Equal(Path.GetFullPath(destination), ex.Path);
        Assert.Equal("already staged", File.ReadAllText(destination));
    }

    [Fact]
    public void CopyCreatesANewDestination()
    {
        var source = Path.Join(_testRoot, "source.zip");
        var destination = Path.Join(_testRoot, "staged.zip");
        File.WriteAllText(source, "new");

        NoOverwriteFile.Copy(source, destination);

        Assert.Equal("new", File.ReadAllText(destination));
    }
}

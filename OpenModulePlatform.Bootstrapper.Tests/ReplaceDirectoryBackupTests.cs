namespace OpenModulePlatform.Bootstrapper.Tests;

/// <summary>
/// ReplaceDirectory swaps a freshly generated package into the live package
/// root. It kept a timestamped backup of the old root, but threw it away as soon
/// as the copy call returned -- without ever checking that the new root was
/// complete. It also swept every older backup BEFORE the swap, so a good backup
/// left behind by an earlier run was gone before the risky part even started.
///
/// The result: a copy that returned without throwing but left an incomplete
/// destination (a file locked mid-copy, a full disk swallowed by a best-effort
/// path) took the last rollback copy with it. These tests pin the rule that at
/// least one verified backup survives until the new destination is proven good.
/// </summary>
public sealed class ReplaceDirectoryBackupTests : IDisposable
{
    private readonly string _testRoot = Path.Join(
        Path.GetTempPath(),
        "omp-replacedir-tests-" + Guid.NewGuid().ToString("N"));

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

    private string NewDirectory(string name)
    {
        var path = Path.Join(_testRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var full = Path.Join(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static string[] BackupsOf(string destination)
    {
        var parent = Path.GetDirectoryName(destination)!;
        var prefix = Path.GetFileName(destination) + ".backup-";
        return Directory.Exists(parent)
            ? Directory.GetDirectories(parent, prefix + "*")
            : [];
    }

    [Fact]
    public void SuccessfulReplaceLeavesTheNewContentAndClearsBackups()
    {
        var source = NewDirectory("source");
        var destination = Path.Join(_testRoot, "destination");
        Directory.CreateDirectory(destination);

        WriteFile(source, "app.dll", "new");
        WriteFile(source, Path.Join("sub", "config.json"), "{}");
        WriteFile(destination, "app.dll", "old");

        Program.ReplaceDirectory(source, destination);

        Assert.Equal("new", File.ReadAllText(Path.Join(destination, "app.dll")));
        Assert.Equal("{}", File.ReadAllText(Path.Join(destination, "sub", "config.json")));
        // A verified swap has no reason to keep the rollback copy around.
        Assert.Empty(BackupsOf(destination));
    }

    [Fact]
    public void AnEarlierBackupSurvivesUntilTheNewDestinationIsVerified()
    {
        // The sweep used to run before the swap, so a backup an earlier run had
        // left behind was deleted while the destination was still unproven.
        var source = NewDirectory("source");
        var destination = Path.Join(_testRoot, "destination");
        Directory.CreateDirectory(destination);
        WriteFile(source, "app.dll", "new");
        WriteFile(destination, "app.dll", "old");

        var strandedBackup = destination + ".backup-19990101000000";
        Directory.CreateDirectory(strandedBackup);
        File.WriteAllText(Path.Join(strandedBackup, "app.dll"), "ancient");

        var observedDuringVerification = Array.Empty<string>();
        Program.ReplaceDirectory(source, destination, _ =>
        {
            // At verification time the swap has happened but is not yet trusted:
            // this is exactly the moment a rollback copy must still exist.
            observedDuringVerification = BackupsOf(destination);
            return true;
        });

        Assert.NotEmpty(observedDuringVerification);
        // ...and once verification passed, the sweep may clear them.
        Assert.Empty(BackupsOf(destination));
    }

    [Fact]
    public void AnIncompleteDestinationIsRolledBackFromTheBackup()
    {
        // The copy returned without throwing, but produced a destination that
        // does not match the source. Before the fix the backup was already gone
        // and the original was unrecoverable.
        var source = NewDirectory("source");
        var destination = Path.Join(_testRoot, "destination");
        Directory.CreateDirectory(destination);
        WriteFile(source, "app.dll", "new");
        WriteFile(destination, "app.dll", "old");
        WriteFile(destination, "keepme.txt", "irreplaceable");

        var failure = Assert.ThrowsAny<Exception>(
            () => Program.ReplaceDirectory(source, destination, _ => false));

        Assert.Contains("verif", failure.Message, StringComparison.OrdinalIgnoreCase);
        // The original content is back, including the file only it had.
        Assert.Equal("old", File.ReadAllText(Path.Join(destination, "app.dll")));
        Assert.Equal("irreplaceable", File.ReadAllText(Path.Join(destination, "keepme.txt")));
    }

    // Verifieringen testas mot tva kataloger vi bygger sjalva. Att ga via
    // ReplaceDirectory gar INTE: en lyckad swap raderar kallan med flit, sa
    // jamforelsen efterat skulle sakna kalla - och ett test som havdar False
    // skulle da bli gront av fel skal.

    [Fact]
    public void VerificationAcceptsAFaithfulCopy()
    {
        var source = NewDirectory("source");
        var destination = NewDirectory("destination");
        WriteFile(source, "app.dll", "0123456789");
        WriteFile(source, Path.Join("sub", "b.txt"), "abc");
        WriteFile(destination, "app.dll", "0123456789");
        WriteFile(destination, Path.Join("sub", "b.txt"), "abc");

        Assert.True(Program.DestinationMatchesSource(source, destination));
    }

    [Fact]
    public void VerificationDetectsATruncatedFile()
    {
        // The file still exists, so mere presence is not enough: a copy that
        // lost bytes is exactly the failure that used to pass unnoticed.
        var source = NewDirectory("source");
        var destination = NewDirectory("destination");
        WriteFile(source, "app.dll", "0123456789");
        WriteFile(destination, "app.dll", "01234");

        Assert.False(Program.DestinationMatchesSource(source, destination));
    }

    [Fact]
    public void VerificationDetectsAMissingFile()
    {
        var source = NewDirectory("source");
        var destination = NewDirectory("destination");
        WriteFile(source, "app.dll", "x");
        WriteFile(source, Path.Join("sub", "b.txt"), "y");
        WriteFile(destination, "app.dll", "x");

        Assert.False(Program.DestinationMatchesSource(source, destination));
    }

    [Fact]
    public void VerificationDetectsAnExtraFileLeftBehind()
    {
        // A destination carrying files the source does not have is not the
        // package we built: a stale binary beside a new one is how a half-old
        // install survives a "successful" refresh.
        var source = NewDirectory("source");
        var destination = NewDirectory("destination");
        WriteFile(source, "app.dll", "x");
        WriteFile(destination, "app.dll", "x");
        WriteFile(destination, "leftover.dll", "stale");

        Assert.False(Program.DestinationMatchesSource(source, destination));
    }

    [Fact]
    public void VerificationRefusesWhenADirectoryIsMissingAltogether()
    {
        // Absence of a measurement must never read as a passing measurement.
        var source = NewDirectory("source");
        WriteFile(source, "app.dll", "x");

        Assert.False(Program.DestinationMatchesSource(source, Path.Join(_testRoot, "finns-inte")));
        Assert.False(Program.DestinationMatchesSource(Path.Join(_testRoot, "finns-inte"), source));
    }
}

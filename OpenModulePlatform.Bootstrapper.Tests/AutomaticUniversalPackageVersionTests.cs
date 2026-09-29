namespace OpenModulePlatform.Bootstrapper.Tests;

// The automatic refresh-and-stage package derives both its version and its
// output file name from the clock. Packages built before the switch to UTC carry
// local-time versions up to two hours ahead, so a plain UTC version could equal
// an existing one (and overwrite that zip) or sort below it.
public sealed class AutomaticUniversalPackageVersionTests : IDisposable
{
    private static readonly DateTime UtcNow = new(2026, 9, 29, 11, 30, 42, DateTimeKind.Utc);

    private readonly string _testRoot = Path.Join(
        Path.GetTempPath(),
        "omp-bootstrapper-version-tests-" + Guid.NewGuid().ToString("N"));

    public AutomaticUniversalPackageVersionTests()
    {
        Directory.CreateDirectory(_testRoot);
    }

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
    public void EmptyOrMissingFoldersUseTheCurrentUtcMinute()
    {
        var version = Program.NextAutomaticUniversalPackageVersion(
            UtcNow,
            [_testRoot, Path.Join(_testRoot, "missing"), null]);

        Assert.Equal("20260929-1130", version);
    }

    [Fact]
    public void AnOlderPackageDoesNotRaiseTheVersion()
    {
        Touch(_testRoot, "omp-universal__global__20260929-0900.zip");

        Assert.Equal("20260929-1130", Program.NextAutomaticUniversalPackageVersion(UtcNow, [_testRoot]));
    }

    // Same UTC minute as an existing package: the old code produced the same
    // file name and silently replaced the earlier zip.
    [Fact]
    public void AnIdenticalVersionIsRaisedPastTheExistingPackage()
    {
        Touch(_testRoot, "omp-universal__global__20260929-1130.zip");

        var version = Program.NextAutomaticUniversalPackageVersion(UtcNow, [_testRoot]);

        Assert.Equal("20260929-1131", version);
        Assert.False(File.Exists(Path.Join(_testRoot, Program.AutomaticUniversalPackageFileName(version))));
    }

    // A package built one hour earlier with a local-time (UTC+2) version reads
    // 12:30, later than the UTC 11:30 of a build made now.
    [Fact]
    public void ALocalTimeVersionAheadOfUtcIsNeverUndercut()
    {
        Touch(_testRoot, "omp-universal__global__20260929-1230.zip");
        Touch(_testRoot, "omp-universal__global__20260929-1115.zip");

        var version = Program.NextAutomaticUniversalPackageVersion(UtcNow, [_testRoot]);

        Assert.Equal("20260929-1231", version);
    }

    // The HostAgent import archive prefixes the file name with its own timestamp.
    [Fact]
    public void ArchivedImportsInOtherFoldersAreConsidered()
    {
        var processed = Path.Join(_testRoot, "processed");
        Directory.CreateDirectory(processed);
        Touch(processed, "20260929-101500-123-omp-universal__global__20260929-1259.zip");

        var version = Program.NextAutomaticUniversalPackageVersion(UtcNow, [_testRoot, processed]);

        Assert.Equal("20260929-1300", version);
    }

    [Fact]
    public void RaisingCarriesOverTheDayBoundary()
    {
        Touch(_testRoot, "omp-universal__global__20260929-2359.zip");

        Assert.Equal("20260930-0000", Program.NextAutomaticUniversalPackageVersion(UtcNow, [_testRoot]));
    }

    [Fact]
    public void UnrelatedOrMalformedNamesAreIgnored()
    {
        Touch(_testRoot, "omp-universal__global__latest.zip");
        Touch(_testRoot, "omp-universal__host-a__20991231-2359.zip");
        Touch(_testRoot, "omp-universal__global__20991231-2359.txt");

        Assert.Equal("20260929-1130", Program.NextAutomaticUniversalPackageVersion(UtcNow, [_testRoot]));
    }

    [Fact]
    public void VersionsStayOrderedAsText()
    {
        Touch(_testRoot, "omp-universal__global__20260929-1230.zip");
        var existing = Directory.GetFiles(_testRoot).Select(Path.GetFileName).ToArray();

        var next = Program.AutomaticUniversalPackageFileName(
            Program.NextAutomaticUniversalPackageVersion(UtcNow, [_testRoot]));

        Assert.All(existing, name => Assert.True(string.CompareOrdinal(next, name) > 0));
    }

    private static void Touch(string folder, string name)
        => File.WriteAllBytes(Path.Join(folder, name), []);
}

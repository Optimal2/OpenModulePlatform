using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class ArtifactZipImportReadinessTests : IDisposable
{
    private readonly string _root = Path.Join(Directory.GetCurrentDirectory(), $"import-readiness-{Guid.NewGuid():N}");
    private readonly ManualTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly HostAgentSettings _settings;
    private readonly RecordingLogger _logger = new();
    private readonly ArtifactZipImportService _service;

    public ArtifactZipImportReadinessTests()
    {
        _settings = new HostAgentSettings
        {
            CentralArtifactRoot = Path.Join(_root, "store"),
            ArtifactZipImport = new HostAgentArtifactZipImportSettings
            {
                IsEnabled = true,
                ImportPath = Path.Join(_root, "import"),
                NotReadyTimeoutMinutes = 1
            }
        };
        Directory.CreateDirectory(_settings.ArtifactZipImport.ImportPath);
        // The folder pipeline needs no database for an empty universal package. A mistaken
        // database call would fail this test rather than mutate a developer database.
        _service = new ArtifactZipImportService(
            new FakeOptionsMonitor<HostAgentSettings> { CurrentValue = _settings }, null!, _logger, _clock);
    }

    [Fact]
    public async Task YoungFile_WaitsEvenWhenStabilityWindowHasElapsed()
    {
        _settings.ArtifactZipImport.MinFileAgeSeconds = 20;
        var path = WritePackage();
        await Scan();
        Advance(10);
        await Scan();
        AssertPending(path);
        Advance(10);
        await Scan();
        AssertImported(path);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SizeOrTimestampChange_RestartsStabilityWindow(bool changeSize)
    {
        var path = WritePackage(ageSeconds: 100);
        await Scan();
        Advance(10);
        if (changeSize)
        {
            WritePackage(comment: "size changed", ageSeconds: 100);
        }
        else
        {
            File.SetLastWriteTimeUtc(path, _clock.GetUtcNow().UtcDateTime.AddSeconds(-50));
        }
        await Scan();
        AssertPending(path);
        Advance(9);
        await Scan();
        AssertPending(path);
        Advance(1);
        await Scan();
        AssertImported(path);
    }

    [Fact]
    public async Task StableLockedFile_IsRetriedEvenAfterTimeout_LogsOnlyStateChanges()
    {
        var path = WritePackage(ageSeconds: 100);
        await Scan();
        Advance(10);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            await Scan();
            Advance(120);
            await Scan();
            await Scan();
            AssertPending(path);
        }
        Assert.Single(_logger.Messages, message => message.Contains("locked or temporarily unreadable"));
        await Scan();
        AssertImported(path);
    }

    [Fact]
    public async Task TruncatedZip_IsDeferredUntilUnchangedTimeout_ThenArchivedWithReason()
    {
        var path = WritePackage(ageSeconds: 1000);
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..^22]);
        File.SetLastWriteTimeUtc(path, _clock.GetUtcNow().UtcDateTime.AddDays(-1));
        await Scan();
        Advance(10);
        await Scan();
        AssertPending(path);
        Advance(49);
        await Scan();
        AssertPending(path);
        Assert.Single(_logger.Messages, message => message.Contains("incomplete or corrupt zip"));
        Advance(1);
        await Scan();
        Assert.False(File.Exists(path));
        var error = Assert.Single(Directory.GetFiles(_settings.ArtifactZipImport.ResolveFailedPath(), "*.error.txt"));
        Assert.Contains("incomplete or corrupt zip, unchanged for 1 minutes", File.ReadAllText(error));
        AssertStagingEmpty();
    }

    [Fact]
    public async Task CompletingTruncatedZip_ResetsTimeoutAndImportsAfterStability()
    {
        var path = WritePackage(ageSeconds: 100);
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..^22]);
        await Scan();
        Advance(59);
        await Scan();
        File.WriteAllBytes(path, bytes);
        await Scan();
        Advance(1);
        await Scan();
        AssertPending(path);
        Advance(9);
        await Scan();
        AssertImported(path);
    }

    [Fact]
    public async Task CompletePackage_IsImportedExactlyOnce()
    {
        var path = WritePackage(ageSeconds: 100);
        await Scan();
        AssertPending(path);
        Advance(10);
        await Scan();
        AssertImported(path);
        await Scan();
        Assert.Single(Directory.GetFiles(_settings.ArtifactZipImport.ResolveProcessedPath()));
        Assert.Single(_logger.Messages, message => message.StartsWith("Imported universal module package"));
    }

    [Fact]
    public async Task ChangingInvalidFile_RestartsNotReadyTimeout()
    {
        var path = WritePackage(ageSeconds: 100);
        File.WriteAllText(path, "unfinished");
        await Scan();
        Advance(59);
        await Scan();
        File.AppendAllText(path, "still unfinished");
        await Scan();
        Advance(59);
        await Scan();
        AssertPending(path);
        Advance(1);
        await Scan();
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(_settings.ArtifactZipImport.ResolveFailedPath(), "*.error.txt"));
    }

    [Fact]
    public async Task DisappearedFile_DiscardsObservationEvenIfSameMetadataReturns()
    {
        var path = WritePackage(ageSeconds: 100);
        var parked = Path.Join(_root, "parked.zip");
        await Scan();
        File.Move(path, parked);
        Advance(20);
        await Scan();
        File.Move(parked, path);
        await Scan();
        AssertPending(path);
        Advance(10);
        await Scan();
        AssertImported(path);
    }

    [Theory]
    [InlineData("package.zip.part")]
    [InlineData("package.zip.tmp")]
    [InlineData("package.zip.crdownload")]
    [InlineData("package.zip.partial")]
    [InlineData("~package.zip")]
    [InlineData("package.json")]
    public async Task StagingNames_AreIgnored(string name)
    {
        var path = WritePackage(name, ageSeconds: 100);
        await Scan();
        Advance(120);
        await Scan();
        AssertPending(path);
        Assert.Empty(_logger.Messages);
    }

    [Fact]
    public async Task RenameFromPart_StartsNewStabilityWindow()
    {
        var partial = WritePackage("package.zip.part", ageSeconds: 100);
        await Scan();
        Advance(30);
        var path = Path.ChangeExtension(partial, null);
        File.Move(partial, path);
        await Scan();
        AssertPending(path);
        Advance(9);
        await Scan();
        AssertPending(path);
        Advance(1);
        await Scan();
        AssertImported(path);
    }

    [Fact]
    public async Task CompleteZipWithInvalidManifest_FailsWithoutNotReadyTimeout()
    {
        var path = WritePackage(manifest: "{broken", ageSeconds: 100);
        await Scan();
        Advance(10);
        await Scan();
        Assert.False(File.Exists(path));
        var error = Assert.Single(Directory.GetFiles(_settings.ArtifactZipImport.ResolveFailedPath(), "*.error.txt"));
        Assert.DoesNotContain("unchanged for", File.ReadAllText(error));
    }

    [Fact]
    public async Task WaitingFiles_DoNotStarveReadyFilesBehindCycleLimit()
    {
        _settings.ArtifactZipImport.MaxFilesPerCycle = 1;
        var pending = WritePackage("a.zip", ageSeconds: 100);
        File.WriteAllText(pending, "unfinished");
        var ready = WritePackage("z.ZIP", ageSeconds: 100);
        await Scan();
        Advance(10);
        await Scan();
        AssertImported(ready);
        Assert.True(File.Exists(pending));
    }

    [Fact]
    public void DefaultsAndInvalidReadinessSettings_AreValidated()
    {
        var settings = new HostAgentArtifactZipImportSettings { IsEnabled = true, ImportPath = _root };
        Assert.Equal(10, settings.MinFileAgeSeconds);
        Assert.Equal(10, settings.StableSizeSeconds);
        Assert.Equal(60, settings.NotReadyTimeoutMinutes);
        settings.Validate();
        settings.MinFileAgeSeconds = -1;
        Assert.Throws<InvalidOperationException>(settings.Validate);
        settings.MinFileAgeSeconds = 0;
        settings.StableSizeSeconds = -1;
        Assert.Throws<InvalidOperationException>(settings.Validate);
        settings.StableSizeSeconds = 0;
        settings.NotReadyTimeoutMinutes = 0;
        Assert.Throws<InvalidOperationException>(settings.Validate);
    }

    [Fact]
    public void ZipWithComment_IsComplete()
    {
        var path = WritePackage(comment: "completed archive comment");
        ImportZipStructureValidator.Validate(path, CancellationToken.None);
    }

    [Theory]
    [InlineData(12)] // Central directory size.
    [InlineData(16)] // Central directory offset.
    public void OutOfBoundsCentralDirectory_IsIncomplete(int field)
    {
        var path = WritePackage();
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 22 + field), uint.MaxValue - 1);
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => ImportZipStructureValidator.Validate(path, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Zip64EndRecords_AreValidated(bool corruptOffset)
    {
        var path = WritePackage();
        var bytes = File.ReadAllBytes(path);
        var end = bytes.Length - 22;
        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(bytes, 0, end);
            writer.Write(0x06064b50u);
            writer.Write(44UL);
            writer.Write((ushort)45);
            writer.Write((ushort)45);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(1UL);
            writer.Write(1UL);
            writer.Write((ulong)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 12)));
            writer.Write(corruptOffset ? ulong.MaxValue : (ulong)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16)));
            writer.Write(0x07064b50u);
            writer.Write(0u);
            writer.Write((ulong)end);
            writer.Write(1u);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(end + 8), ushort.MaxValue);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(end + 10), ushort.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(end + 12), uint.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(end + 16), uint.MaxValue);
            writer.Write(bytes, end, 22);
        }
        if (corruptOffset)
            Assert.Throws<InvalidDataException>(() => ImportZipStructureValidator.Validate(path, CancellationToken.None));
        else
            ImportZipStructureValidator.Validate(path, CancellationToken.None);
    }

    private Task<bool> Scan() => _service.ImportFilesAsync(_settings, CancellationToken.None);
    private void Advance(int seconds) => _clock.Advance(TimeSpan.FromSeconds(seconds));

    private string WritePackage(string name = "package.zip", string manifest = "{\"formatVersion\":1,\"items\":[]}",
        string comment = "", int ageSeconds = 0)
    {
        var path = Path.Join(_settings.ArtifactZipImport.ImportPath, name);
        using (var stream = File.Create(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            archive.Comment = comment;
            using var writer = new StreamWriter(archive.CreateEntry("omp-universal-package.json").Open());
            writer.Write(manifest);
        }
        File.SetLastWriteTimeUtc(path, _clock.GetUtcNow().UtcDateTime.AddSeconds(-ageSeconds));
        return path;
    }

    private void AssertPending(string path)
    {
        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_settings.ArtifactZipImport.ResolveProcessedPath()));
        Assert.Empty(Directory.GetFiles(_settings.ArtifactZipImport.ResolveFailedPath()));
        AssertStagingEmpty();
    }

    private void AssertImported(string path)
    {
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(_settings.ArtifactZipImport.ResolveProcessedPath()));
        Assert.Empty(Directory.GetFiles(_settings.ArtifactZipImport.ResolveFailedPath()));
        AssertStagingEmpty();
    }

    private void AssertStagingEmpty()
    {
        var staging = Path.Join(_settings.CentralArtifactRoot, ".hostagent-import-staging");
        if (Directory.Exists(staging)) Assert.Empty(Directory.GetFileSystemEntries(staging));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class RecordingLogger : ILogger<ArtifactZipImportService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}

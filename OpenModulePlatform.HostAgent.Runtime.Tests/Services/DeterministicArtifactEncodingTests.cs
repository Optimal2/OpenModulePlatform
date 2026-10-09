using System.Collections;
using System.Globalization;
using System.IO.Compression;
using OpenModulePlatform.Artifacts;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class DeterministicArtifactEncodingTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "omp-encoding-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Json_HasFixedEscapingOrderingAndInvariantNumbers()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
            var value = new Hashtable
            {
                ["z"] = new object[] { true, false, 1.25m },
                ["text"] = "\"\\\n\rå漢😀",
                ["empty"] = Array.Empty<object>(),
                ["null"] = null,
                ["A"] = 1
            };
            Assert.Equal("{\"A\":1,\"empty\":[],\"null\":null,\"text\":\"\\\"\\\\\\u000a\\u000d\\u00e5\\u6f22\\ud83d\\ude00\",\"z\":[true,false,1.25]}",
                DeterministicArtifactEncoding.Json(value));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Zip_RoundTripsEmptyUnicodeAndMultiBufferFilesAndNormalizesExistingZip()
    {
        var source = Path.Join(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Join(source, "empty.txt"), []);
        var bytes = Enumerable.Range(0, 300000).Select(i => (byte)(i % 251)).ToArray();
        File.WriteAllBytes(Path.Join(source, "å漢.txt"), bytes);
        var first = Path.Join(_root, "first.zip");
        DeterministicArtifactEncoding.WriteDirectory(source, first);
        using (var archive = ZipFile.OpenRead(first))
        {
            Assert.Equal(["empty.txt", "å漢.txt"], archive.Entries.Select(e => e.FullName));
            Assert.All(archive.Entries, entry => Assert.Equal(new DateTime(1980, 1, 1), entry.LastWriteTime.DateTime));
            using var stream = archive.GetEntry("å漢.txt")!.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            Assert.Equal(bytes, copy.ToArray());
        }
        var second = Path.Join(_root, "second.zip");
        DeterministicArtifactEncoding.NormalizeZip(first, second);
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/absolute.txt")]
    [InlineData("..\\escape.txt")]
    public void Zip_RejectsUnsafeEntryNamesBeforeWriting(string name)
    {
        Assert.Throws<InvalidDataException>(() => DeterministicArtifactEncoding.WriteFiles(
            Path.Join(_root, "invalid.zip"), [name], ["unused"]));
        Assert.False(File.Exists(Path.Join(_root, "invalid.zip")));
    }

    [Fact]
    public void Zip_RejectsCaseInsensitiveDuplicateNames()
    {
        Assert.Throws<InvalidDataException>(() => DeterministicArtifactEncoding.WriteFiles(
            Path.Join(_root, "duplicate.zip"), ["a", "A"], ["unused", "unused"]));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

using System.Buffers.Binary;
using System.IO.Compression;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>Checks the completed ZIP envelope before any package content is imported.</summary>
internal static class ImportZipStructureValidator
{
    // PKWARE APPNOTE 4.3.14-4.3.16: https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT
    internal static void Validate(string path, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(path);
        try
        {
            Validate(stream, cancellationToken);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("ZIP directory records are truncated.", ex);
        }
    }

    private static void Validate(Stream stream, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // EOCD is 22 bytes followed by at most 65535 bytes of archive comment.
        var tail = new byte[(int)Math.Min(stream.Length, 22 + ushort.MaxValue)];
        var tailOffset = stream.Length - tail.Length;
        stream.Position = tailOffset;
        stream.ReadExactly(tail);
        var endIndex = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
        {
            if (U32(tail, i) == 0x06054b50 && i + 22 + U16(tail, i + 20) == tail.Length)
            {
                endIndex = i;
                break;
            }
        }

        Require(endIndex >= 0, "ZIP end of central directory is missing or truncated.");
        var endOffset = tailOffset + endIndex;
        Require(U16(tail, endIndex + 4) == 0 && U16(tail, endIndex + 6) == 0,
            "Multi-disk ZIP files are not supported.");
        ulong count = U16(tail, endIndex + 10);
        Require(U16(tail, endIndex + 8) == count, "ZIP directory entry counts disagree.");
        ulong size = U32(tail, endIndex + 12);
        ulong offset = U32(tail, endIndex + 16);
        var directoryLimit = (ulong)endOffset;
        var requiresZip64 = count == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue;

        // ZIP64 is also allowed for small archives, without sentinel values in EOCD.
        var locator = new byte[20];
        if (endOffset >= locator.Length)
        {
            stream.Position = endOffset - locator.Length;
            stream.ReadExactly(locator);
        }

        if (U32(locator, 0) == 0x07064b50)
        {
            Require(U32(locator, 4) == 0 && U32(locator, 16) == 1, "Invalid ZIP64 disk locator.");
            var zip64Offset = U64(locator, 8);
            var locatorOffset = (ulong)(endOffset - locator.Length);
            Require(zip64Offset <= locatorOffset && locatorOffset - zip64Offset >= 56,
                "ZIP64 end record lies outside the file.");
            stream.Position = (long)zip64Offset;
            var record = new byte[56];
            stream.ReadExactly(record);
            Require(U32(record, 0) == 0x06064b50, "ZIP64 end record is missing.");
            var recordSize = U64(record, 4);
            Require(recordSize >= 44 && recordSize == locatorOffset - zip64Offset - 12,
                "ZIP64 end record is truncated.");
            Require(U32(record, 16) == 0 && U32(record, 20) == 0, "Multi-disk ZIP64 files are not supported.");
            var zip64Count = U64(record, 32);
            var zip64Size = U64(record, 40);
            var zip64DirectoryOffset = U64(record, 48);
            Require(U64(record, 24) == zip64Count, "ZIP64 directory entry counts disagree.");
            Require((count == ushort.MaxValue || count == zip64Count)
                && (size == uint.MaxValue || size == zip64Size)
                && (offset == uint.MaxValue || offset == zip64DirectoryOffset), "ZIP and ZIP64 directory records disagree.");
            count = zip64Count;
            size = zip64Size;
            offset = zip64DirectoryOffset;
            directoryLimit = zip64Offset;
        }
        else
        {
            Require(!requiresZip64, "ZIP64 locator is missing.");
        }

        // Subtract rather than add untrusted unsigned fields, avoiding overflow.
        Require(offset <= directoryLimit && size <= directoryLimit - offset,
            "ZIP central directory lies outside the completed archive.");
        Require(count <= size / 46, "ZIP central directory is too small for its entry count.");
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        Require((ulong)archive.Entries.Count == count, "ZIP central directory entry count is invalid.");
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = entry.FullName;
            _ = entry.Length;
        }
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}

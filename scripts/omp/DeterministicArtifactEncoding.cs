// Shared verbatim by the .NET writer and Add-Type on Windows PowerShell 5.1/PowerShell 7.
// Keep this file compatible with the C# compiler shipped with .NET Framework.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace OpenModulePlatform.Artifacts
{
    public static class DeterministicArtifactEncoding
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        // Compact JSON, ordinal keys, ASCII escapes, no BOM or trailing newline.
        public static string Json(object value)
        {
            if (value == null) return "null";
            var text = value as string;
            if (text != null)
            {
                var result = new StringBuilder("\"");
                foreach (char c in text)
                {
                    if (c == '"' || c == '\\') result.Append('\\').Append(c);
                    else if (c < 32 || c > 126) result.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else result.Append(c);
                }
                return result.Append('"').ToString();
            }
            if (value is bool) return (bool)value ? "true" : "false";
            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                var keys = dictionary.Keys.Cast<string>().OrderBy(key => key, StringComparer.Ordinal);
                return "{" + string.Join(",", keys.Select(key => Json(key) + ":" + (dictionary[key] == null ? "null" : Json(dictionary[key] ?? "")))) + "}";
            }
            var array = value as IEnumerable;
            if (array != null) return "[" + string.Join(",", array.Cast<object>().Select(Json)) + "]";
            if (value is int || value is long || value is uint || value is ulong || value is decimal)
                return ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
            throw new ArgumentException("Unsupported canonical JSON type: " + value.GetType().FullName);
        }

        public static void WriteJson(string path, object value) { File.WriteAllText(path, Json(value), Utf8); }

        public static void WriteDirectory(string root, string destination)
        {
            WriteDirectoryExcept(root, destination, "");
        }

        public static void WriteDirectoryExcept(string root, string destination, string excludedTopLevelDirectory)
        {
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var files = EnumerateFiles(root).Where(file => string.IsNullOrEmpty(excludedTopLevelDirectory) ||
                !file.Substring(root.Length + 1).Replace('\\', '/').StartsWith(excludedTopLevelDirectory + "/", StringComparison.OrdinalIgnoreCase)).ToArray();
            WriteFiles(destination, files.Select(file => file.Substring(root.Length + 1).Replace('\\', '/')).ToArray(), files);
        }

        private static IEnumerable<string> EnumerateFiles(string root)
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Artifact root must not be a reparse point.");
            foreach (var path in Directory.EnumerateFileSystemEntries(root))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    foreach (var file in EnumerateFiles(path)) yield return file;
                }
                else yield return path;
            }
        }

        public static void WriteFiles(string destination, string[] names, string[] paths)
        {
            if (names.Length != paths.Length) throw new ArgumentException("Entry names and paths must match.");
            WriteArchive(destination, names, index => File.OpenRead(paths[index]));
        }

        // Re-encode zip inputs as well: callers may supply archives with arbitrary mtimes,
        // order, compression or separators. No extraction or filesystem traversal is needed.
        public static void NormalizeZip(string source, string destination)
        {
            using (var archive = ZipFile.OpenRead(source))
            {
                var entries = archive.Entries.Where(entry => entry.Name.Length != 0).ToArray();
                WriteArchive(destination, entries.Select(entry => entry.FullName.Replace('\\', '/')).ToArray(),
                    index => entries[index].Open());
            }
        }

        private static readonly uint[] CrcTable = MakeCrcTable();
        private static uint[] MakeCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint crc = i;
                for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
                table[i] = crc;
            }
            return table;
        }

        private sealed class Entry
        {
            public byte[] Name = new byte[0];
            public long Offset;
            public long Size;
            public uint Crc;
        }

        // A specified ZIP64/store wire format, independent of runtime zlib versions,
        // OS attributes and ZipArchive header defaults. Streaming, including >4 GiB files.
        // Every entry uses UTF-8, DOS epoch 1980-01-01, no comments or data descriptors.
        private static void WriteArchive(string destination, string[] names, Func<int, Stream> open)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination)) ?? ".");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in names)
            {
                if (string.IsNullOrEmpty(name) || name.StartsWith("/", StringComparison.Ordinal) || name.Contains(":") || name.Contains("\\") ||
                    name.Split('/').Any(part => part == ".." || part == "." || part.Length == 0) || name.IndexOf('\0') >= 0 || !seen.Add(name))
                    throw new InvalidDataException("Invalid or duplicate artifact entry: " + name);
            }
            using (var output = new BinaryWriter(File.Create(destination), Utf8))
            {
                var entries = new List<Entry>();
                var buffer = new byte[131072];
                foreach (int index in Enumerable.Range(0, names.Length).OrderBy(i => names[i], StringComparer.Ordinal))
                {
                    var entry = new Entry { Name = Utf8.GetBytes(names[index]), Offset = output.BaseStream.Position };
                    if (entry.Name.Length > ushort.MaxValue) throw new InvalidDataException("ZIP entry name is too long.");
                    output.Write(0x04034b50u); output.Write((ushort)45); output.Write((ushort)0x800);
                    output.Write((ushort)0); output.Write((ushort)0); output.Write((ushort)33);
                    output.Write(0u); output.Write(uint.MaxValue); output.Write(uint.MaxValue);
                    output.Write((ushort)entry.Name.Length); output.Write((ushort)20); output.Write(entry.Name);
                    output.Write((ushort)1); output.Write((ushort)16); output.Write(0L); output.Write(0L);
                    uint crc = uint.MaxValue;
                    using (var input = open(index))
                    {
                        int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            output.Write(buffer, 0, count);
                            entry.Size += count;
                            for (int i = 0; i < count; i++) crc = CrcTable[(crc ^ buffer[i]) & 255] ^ (crc >> 8);
                        }
                    }
                    entry.Crc = ~crc;
                    long end = output.BaseStream.Position;
                    output.BaseStream.Position = entry.Offset + 14; output.Write(entry.Crc);
                    output.BaseStream.Position = entry.Offset + 34 + entry.Name.Length;
                    output.Write(entry.Size); output.Write(entry.Size);
                    output.BaseStream.Position = end;
                    entries.Add(entry);
                }
                long central = output.BaseStream.Position;
                foreach (var entry in entries)
                {
                    output.Write(0x02014b50u); output.Write((ushort)45); output.Write((ushort)45);
                    output.Write((ushort)0x800); output.Write((ushort)0); output.Write((ushort)0); output.Write((ushort)33);
                    output.Write(entry.Crc); output.Write(uint.MaxValue); output.Write(uint.MaxValue);
                    output.Write((ushort)entry.Name.Length); output.Write((ushort)28); output.Write((ushort)0);
                    output.Write((ushort)0); output.Write((ushort)0); output.Write(0u); output.Write(uint.MaxValue);
                    output.Write(entry.Name); output.Write((ushort)1); output.Write((ushort)24);
                    output.Write(entry.Size); output.Write(entry.Size); output.Write(entry.Offset);
                }
                long centralSize = output.BaseStream.Position - central;
                long zip64 = output.BaseStream.Position;
                output.Write(0x06064b50u); output.Write(44L); output.Write((ushort)45); output.Write((ushort)45);
                output.Write(0u); output.Write(0u); output.Write((long)entries.Count); output.Write((long)entries.Count);
                output.Write(centralSize); output.Write(central);
                output.Write(0x07064b50u); output.Write(0u); output.Write(zip64); output.Write(1u);
                output.Write(0x06054b50u); output.Write((ushort)0); output.Write((ushort)0);
                output.Write(ushort.MaxValue); output.Write(ushort.MaxValue);
                output.Write(uint.MaxValue); output.Write(uint.MaxValue); output.Write((ushort)0);
            }
        }
    }
}

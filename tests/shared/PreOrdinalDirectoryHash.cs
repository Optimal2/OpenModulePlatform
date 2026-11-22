using System.Security.Cryptography;
using System.Text;

namespace OpenModulePlatform.TestSupport;

/// <summary>
/// The artifact directory hash as it was written before the path ordering became ordinal,
/// kept as an independent reference: a test that needs "the value registered before the
/// change" must not derive it from the code under test.
/// </summary>
/// <remarks>
/// Deliberately a plain copy of the old algorithm with no dependency on the production
/// hashing code: relative paths sorted with StringComparer.OrdinalIgnoreCase, then for each
/// file the relative path with '/', UTF-8, one zero byte and the file content.
/// </remarks>
internal static class PreOrdinalDirectoryHash
{
    /// <summary>
    /// File names for which the ordinal and the case-insensitive ordering disagree:
    /// ordinal sorts "B.txt" and "Zeta" before "a.txt" and "alpha", case-insensitive after.
    /// </summary>
    public static readonly IReadOnlyList<(string RelativePath, string Content)> OrderSensitiveFiles =
    [
        ("B.txt", "bravo"),
        ("a.txt", "alpha"),
        ("Zeta/x", "zeta-x"),
        ("alpha/y", "alpha-y")
    ];

    public static void WriteOrderSensitiveTree(string root)
    {
        foreach (var (relativePath, content) in OrderSensitiveFiles)
        {
            var path = Path.Join(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }

    public static string Compute(string path)
    {
        using var sha = SHA256.Create();
        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .OrderBy(file => Path.GetRelativePath(path, file), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in files)
        {
            var relativeBytes = Encoding.UTF8.GetBytes(Path.GetRelativePath(path, file).Replace('\\', '/'));
            sha.TransformBlock(relativeBytes, 0, relativeBytes.Length, null, 0);
            sha.TransformBlock([0], 0, 1, null, 0);
            var content = File.ReadAllBytes(file);
            sha.TransformBlock(content, 0, content.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }
}

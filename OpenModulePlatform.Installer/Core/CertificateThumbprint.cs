using System.Text;

namespace OpenModulePlatform.Installer.Core;

/// <summary>
/// Normalises a certificate thumbprint as operators actually paste it. The
/// certificate snap-in shows colon- or space-separated hex, certutil output
/// carries spaces, and copied text often contains invisible characters (a
/// leading left-to-right mark U+200E, zero-width spaces, a BOM). Store lookup
/// needs plain hex; case is irrelevant.
/// </summary>
public static class CertificateThumbprint
{
    /// <summary>
    /// Keeps only the hexadecimal digits, uppercased. Everything else -
    /// colons, spaces, invisible formatting characters - is dropped, so
    /// "AA:BB:CC", "aa bb cc" and a thumbprint copied out of the certificate
    /// snap-in (with its invisible leading formatting mark) all find the same
    /// certificate. Returns an empty string for null/blank input.
    /// </summary>
    public static string Normalize(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(thumbprint.Length);
        foreach (var character in thumbprint)
        {
            if (char.IsAsciiHexDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString();
    }
}

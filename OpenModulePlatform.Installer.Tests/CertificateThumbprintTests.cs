using OpenModulePlatform.Installer.Core;

namespace OpenModulePlatform.Installer.Tests;

/// <summary>
/// Certificate thumbprints as operators paste them (certificate snap-in,
/// certutil, documentation) must be normalised before the store lookup.
/// </summary>
public class CertificateThumbprintTests
{
    [Fact]
    public void Colon_separated_thumbprint_normalises_to_plain_hex()
        => Assert.Equal("AABBCCDD", CertificateThumbprint.Normalize("AA:BB:CC:DD"));

    [Fact]
    public void Space_separated_thumbprint_normalises_to_plain_hex()
        => Assert.Equal("AABBCCDD", CertificateThumbprint.Normalize("AA BB CC DD"));

    [Fact]
    public void Lowercase_thumbprint_is_uppercased()
        => Assert.Equal("AABBCCDD", CertificateThumbprint.Normalize("aabbccdd"));

    [Fact]
    public void Invisible_formatting_characters_are_removed()
    {
        // The certificate snap-in's copy puts a left-to-right mark (U+200E) in
        // front of the thumbprint; zero-width spaces (U+200B) show up from web
        // pages; certutil output can carry a BOM (U+FEFF).
        Assert.Equal("AABBCCDD", CertificateThumbprint.Normalize("\u200EAA\u200BBB\uFEFFCCDD"));
    }

    [Fact]
    public void Mixed_paste_form_normalises_to_plain_hex()
        => Assert.Equal("AABBCCDD", CertificateThumbprint.Normalize("  aa:bb CC-dd\u200E "));

    [Fact]
    public void Blank_input_normalises_to_empty()
    {
        Assert.Equal(string.Empty, CertificateThumbprint.Normalize(null));
        Assert.Equal(string.Empty, CertificateThumbprint.Normalize(""));
        Assert.Equal(string.Empty, CertificateThumbprint.Normalize("   "));
    }
}

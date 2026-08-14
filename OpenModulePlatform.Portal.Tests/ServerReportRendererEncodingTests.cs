using System.Text.Json;
using OpenModulePlatform.Web.ContentWebAppModule.Services;

namespace OpenModulePlatform.Portal.Tests;

/// <summary>
/// Pins the encoder that keeps report data from breaking out of its &lt;script&gt; block.
/// </summary>
/// <remarks>
/// ServerReportRenderer writes serialized report rows straight into a script element. Those
/// rows come from a database, so a value can contain a literal "&lt;/script&gt;" -- which,
/// unescaped, ends the block and lets whatever follows be parsed as markup.
///
/// The protection is JavaScriptEncoder.Default escaping '&lt;'. It is worth stating plainly
/// that this is NOT the often-cited '/' escaping: the encoder leaves '/' alone, as the
/// second test asserts. Anyone reasoning about the safety of this path from the '/' angle
/// will reach the wrong conclusion, which is exactly why the mechanism is pinned here
/// rather than described in prose alone.
///
/// Swapping in JavaScriptEncoder.UnsafeRelaxedJsonEscaping fails the first test.
/// </remarks>
public sealed class ServerReportRendererEncodingTests
{
    private sealed record Row(string Value);

    [Fact]
    public void JavaScriptJsonOptions_EscapesScriptClosingTag()
    {
        var payload = new Row("</script><script>alert(1)</script>");

        var json = JsonSerializer.Serialize(payload, ServerReportRenderer.JavaScriptJsonOptions);

        Assert.DoesNotContain("</script>", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('<', json);
        Assert.Contains("\\u003C", json, StringComparison.Ordinal);
    }

    [Fact]
    public void JavaScriptJsonOptions_DoesNotRelyOnEscapingForwardSlash()
    {
        // Documents the actual mechanism. If a future change starts escaping '/' as well
        // that is harmless, but this test failing would mean the reasoning in the remarks
        // above no longer matches the encoder, and the comment needs revisiting.
        var payload = new Row("a/b");

        var json = JsonSerializer.Serialize(payload, ServerReportRenderer.JavaScriptJsonOptions);

        Assert.Contains("a/b", json, StringComparison.Ordinal);
    }
}

using System.Security.Cryptography;
using System.Text;

namespace OpenModulePlatform.Bootstrapper.Tests;

public sealed class OpenDocViewerArtifactIntegrityTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "omp-odv-integrity-test-" + Guid.NewGuid().ToString("N"));

    public OpenDocViewerArtifactIntegrityTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("")]
    [InlineData(" data-odv-config-integrity=\"sha384-stale\"")]
    [InlineData(" DATA-ODV-CONFIG-INTEGRITY='sha384-stale'")]
    public void UsesActualConfigBytesAndIsIdempotent(string existing)
    {
        var config = Encoding.UTF8.GetBytes("window.__ODV_CONFIG__ = {};\r\n");
        File.WriteAllBytes(Path.Join(_root, "odv.config.js"), config);
        File.WriteAllText(Path.Join(_root, "index.html"), "<html><script type=\"module\" data-odv-bootstrap" + existing + "></script></html>");

        OpenDocViewerArtifactIntegrity.Apply(_root);
        var first = File.ReadAllBytes(Path.Join(_root, "index.html"));
        var expected = "sha384-" + Convert.ToBase64String(SHA384.HashData(config));
        Assert.Contains("data-odv-config-integrity=\"" + expected + "\"", Encoding.UTF8.GetString(first));
        Assert.DoesNotContain("stale", Encoding.UTF8.GetString(first));
        OpenDocViewerArtifactIntegrity.Apply(_root);
        Assert.Equal(first, File.ReadAllBytes(Path.Join(_root, "index.html")));
    }

    [Fact]
    public void LeavesUnrelatedWebPayloadUntouched()
    {
        var html = "<html>generic web application</html>";
        File.WriteAllText(Path.Join(_root, "index.html"), html);
        OpenDocViewerArtifactIntegrity.Apply(_root);
        Assert.Equal(html, File.ReadAllText(Path.Join(_root, "index.html")));
    }

    [Fact]
    public void RefusesViewerPayloadWithoutItsBootstrapMarker()
    {
        File.WriteAllText(Path.Join(_root, "odv.config.js"), "{}");
        File.WriteAllText(Path.Join(_root, "index.html"), "<html><script></script></html>");
        Assert.Throws<InvalidOperationException>(() => OpenDocViewerArtifactIntegrity.Apply(_root));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

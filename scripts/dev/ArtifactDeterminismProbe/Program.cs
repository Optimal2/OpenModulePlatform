using System.IO.Compression;
using System.Globalization;
using System.Text.Json;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Services;

CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
var root = Path.GetFullPath(args[1]);
if (args[0] == "pack")
{
    new ArtifactPackageWriter().CreateFromPayloadDirectory(
        Path.Join(root, "payload"), Path.Join(root, "package.zip"),
        [new ArtifactPackageConfigurationFile("settings/config.txt", File.ReadAllText(Path.Join(root, "config.txt")))],
        "1.2.3", "1.0.0");
}
else
{
    var extracted = Path.Join(root, "extracted");
    ZipFile.ExtractToDirectory(Path.Join(root, "package.zip"), extracted);
    var payload = Path.Join(root, "unpacked");
    ZipFile.ExtractToDirectory(Path.Join(extracted, "payload", "artifact.zip"), payload);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        PayloadHash = await ArtifactHash.ComputeSha256Async(payload, CancellationToken.None),
        ZipHash = await ArtifactHash.ComputeSha256Async(Path.Join(root, "package.zip"), CancellationToken.None)
    }));
}

using System.Reflection.Metadata;

var symbols = Directory.GetFiles(args[0], "OpenModulePlatform.*.pdb");
if (symbols.Length == 0) throw new InvalidOperationException("No OMP portable symbols were published.");
var razorDocuments = 0;
var documents = 0;
foreach (var path in symbols)
{
    using var stream = File.OpenRead(path);
    using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
    var reader = provider.GetMetadataReader();
    if (reader.Documents.Count == 0) throw new InvalidOperationException($"No source documents in {path}.");
    foreach (var handle in reader.Documents)
    {
        var name = reader.GetString(reader.GetDocument(handle).Name).Replace('\\', '/');
        if (!name.StartsWith("/_/", StringComparison.Ordinal))
            throw new InvalidOperationException($"Physical or unmapped PDB document: {name}");
        if (name.EndsWith("_cshtml.g.cs", StringComparison.Ordinal)
            || name.EndsWith("_razor.g.cs", StringComparison.Ordinal))
            razorDocuments++;
        documents++;
    }
}
if (args.Length > 1 && args[1] == "razor" && razorDocuments == 0)
    throw new InvalidOperationException("No generated Razor documents survived in the published symbols.");
Console.WriteLine($"PASS: {documents} virtual PDB documents, including {razorDocuments} generated Razor sources.");

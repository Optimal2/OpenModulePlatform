# Deterministic Razor inputs

This build-only source-generator adapter uses the Razor compiler supplied by the
selected .NET 10 SDK. It does not ship in application artifacts and downloads no
compiler package. The target in `../OpenModulePlatform.DeterministicStaticWebAssets.targets`
replaces the SDK analyzer only for command-line builds; design-time builds keep
the SDK analyzer so Razor editor integration is unchanged.

The SDK generator receives `AdditionalText.Path` and `MSBuildProjectDirectory`
before C# emission. Absolute paths therefore occur in generated `#line` and
`#pragma checksum` directives. Roslyn maps PDB document names but embeds the
original generated text and its checksum. Razor TagHelper IDs also depend on
generated-text offsets: replacing directives after generation is insufficient.

The adapter feeds the SDK generator paths normalized by the compilation's own
`SourceReferenceResolver`/`PathMap`. It forwards original file contents, item
metadata (including encoded `TargetPath` and `CssScope`), compilation, parse
options and diagnostics. This keeps view identifiers and TagHelper discovery
intact while producing stable directives and IDs before embedding. The driver
runs once per compilation; the outer adapter is not incremental. Generator
failures are build errors (`CS8784`/`CS8785`), never a successful empty-view build.
The generated text is materialized before it is handed to the outer driver for
concurrent compilation. The target also maps Windows' drive-qualified `/_/`
root back to the virtual root when Csc resolves generated line directives.
When an isolated build root is supplied, a stable hash of each helper copy's
physical directory separates the consumer and platform copies' obj/bin. Those
intermediates are mapped to the same virtual paths for compiler determinism.
Command-line Razor compilation uses a fresh compiler process. Reusing the
compiler server across relocated builds with identical virtual paths produced
inconsistent Auth Razor parsing during the three-leg CI probe; the source files
were identical. Design-time builds retain the SDK defaults.

The adapter explicitly loads the SDK's Razor dependencies in its analyzer load
context. Registering the SDK compiler as another analyzer would run the original
generator as well and duplicate every view. It builds against the SDK's Roslyn
assemblies rather than mixing a NuGet compiler version with the installed SDK.

Consumers copy the target, this project and `DeterministicRazorGenerator.cs`
together (Check 15 enforces these three files). Existing repository and isolated
intermediate `PathMap` entries are honored. A project-local map is provided when
none exists; consumers using external intermediate or linked source roots must
map those roots as well. `UseRazorSourceGenerator=false` retains the SDK's legacy
pipeline, which is outside this regression contract. `OmpDeterministicRazor=false`
is a diagnostic escape hatch, not a deterministic packaging configuration.

Run `scripts/omp/test-deterministic-publish.ps1` for WorkerProcessHost and Portal,
or select all web projects with `-Project Portal,Auth,Content,Iframe,Example,Blazor,ServiceWeb,WorkerWeb`.
CI also checks different isolated roots, both PowerShell engines and matching
hashes on independent Windows runners. Keep SDK/runtime, dependencies, source
bytes and build options fixed when comparing machines.

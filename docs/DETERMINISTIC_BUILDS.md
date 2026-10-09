# Deterministic local artifact builds

Artifact identity must survive a fresh checkout or an isolated build directory.
`Directory.Build.props` enables `ContinuousIntegrationBuild` and `Deterministic`
for local builds as well as CI, uses portable PDBs, and maps source paths to
`/_/openmoduleplatform/`. Isolated `obj/<project>` and `bin/<project>` directories
map to the equivalent virtual `<project>/obj` and `<project>/bin` paths.
For nested projects this uses their actual repository-relative directory, not
the project name (for example `examples/WebAppModule/WebApp/obj`).
Caller-supplied PathMap entries are retained after the OMP mappings, including
when the installer passes PathMap as a global MSBuild property.

This normalizes PDB documents, the PE debug directory's PDB path and compiler
`CallerFilePath` literals. The existing informational-version and SourceLink
policy is unchanged: local artifacts exclude the revision, while GitHub CI
enables SourceLink generation. The explicit OMP PathMap can prevent those
SourceLink mappings from matching PDB documents; automatic source download is
not guaranteed. This is a same-input, same-toolchain guarantee, not a promise
of equal bytes across SDK/runtime versions, signing operations or local/CI
SourceLink settings.

### Debugging choice

Packaging keeps the mandatory PathMap. Portable PDBs retain symbols and line
information, but Visual Studio cannot automatically find local source files at
`/_/openmoduleplatform/` and a global `-p:PathMap` does not override the OMP map.
Use the debugger's source-file browse dialog to select the matching local file
(or configure source-path substitution in a debugger that supports it). No
interactive opt-out was added that could accidentally reach packaging.

## Cross-shell and cross-producer package contract

`scripts/omp/DeterministicArtifactEncoding.cs` is compiled into Artifacts and
loaded by `scripts/omp/deterministic-artifact.ps1` in either PowerShell engine.
Generated artifact JSON uses ordinal keys, compact formatting (zero indentation),
invariant numbers, ASCII escapes, UTF-8 without BOM and no trailing newline.
Installer refresh delegates to the canonical packer, including its legacy
configuration entry names. Input file contents are preserved; supplied payload
zips are re-encoded to remove order, compression and timestamp differences.

Both archive layers use the same streaming ZIP64/store encoding, ordinal entry
order, UTF-8 names, zero external attributes and 1980-01-01 timestamps. Store
(no compression) deliberately avoids differences between .NET Framework and
modern .NET compression implementations. Packages are larger; ZIP64 is used
even for small files so the encoding also supports files exceeding 4 GiB.
Empty directories carry no artifact semantics. Reparse points are skipped.

Run `python scripts/omp/test-artifact-producers.py --output artifacts/producers`
with a fresh output directory. The test invokes the actual installer functions
without executing installation, and the canonical packer, under both Windows
PowerShell 5.1 and PowerShell 7, plus `ArtifactPackageWriter`. It hashes extracted
payloads with the production `ArtifactHash` and compares complete zip SHA-256.
The blocking local-ci and GitHub CI steps run this same five-leg matrix.
Both also assert the recorded golden hashes, so a different CI machine must
produce the same bytes as the local run, not just agree with itself.
No tools are installed and missing shells fail the test instead of skipping it.

### Producer regression evidence (2026-10-09)

Using the producer implementations from `18f26e15`, the test failed with two
payload hashes and five different zip hashes. The same fixture after this change
passes all five legs (including independent Python ZIP CRC validation).

| Producer | Shell | Red payload | Red zip SHA-256 |
| --- | --- | --- | --- |
| Installer refresh | 5.1 | A | `fcc43da7b4a48e819f4239e65f9954075a17bd1539896b62d68bec35a3a6e13b` |
| Canonical | 5.1 | A | `b9ccfafdac4a36f30d05681aff9ea457024ec5c50a79fdd365932f8bc05c6357` |
| Installer refresh | 7 | B | `269a36a2e68cebedc290aeaf8174dfe7c97b3e940f0d2951bf8eeede58cfb6d9` |
| Canonical | 7 | B | `8b71c485852c9b63262edb5fc5374b1d677a901420b7cd05d7d72bbb757ea31e` |
| C# writer | .NET 10 | B | `bdb5ae2a36ca947cb361c31a4991dfdf42772914edfef4650c9c667c202d719c` |

Red payload A: `a35ffe32f0b77de692ec21cbdeb3d95194fc7655568de29faaf7b20abc5ae1fe`.
Red payload B: `7fc040865661c2d480c25b244b97a53a645f1a98e88caf9148f070485ecd3a06`.
Green payload (all five): `02aeb6e185f6897c6ce0b462804dcfd22decce4f63815ccd629ebf803a62c858`.
Green zip (all five): `8995639c22fcf77c81cfa710ceefde59caded062b36573d0190815ebe4995814`.
These are fixture hashes, not hashes of released artifact packages.

This contract concerns artifact envelopes and their payloads. The installer
distribution's bootstrap settings and creation-time audit manifest are separate,
environment-specific inputs, not immutable artifact payloads. Signing also
changes the input bytes; determinism does not remove signatures or timestamps
inside signed binaries.

## Regression check

```powershell
& './scripts/omp/test-deterministic-publish.ps1'
& './scripts/omp/test-deterministic-publish.ps1' -UseIsolatedBuildRoots
```

The check copies tracked build inputs from the working tree, publishes the real
WorkerProcessHost, Portal and their OMP dependencies in separate paths (including
spaces), and changes source timestamps between copies. It compares SHA-256 for
every published file and the production artifact packer's complete zip. With
`-UseIsolatedBuildRoots`, the first build is in-tree and the other two use
different isolated roots. Use a scratch directory outside any OMP checkout for
that variant: the existing isolation guard correctly rejects roots underneath
an OMP checkout. `-OutputRoot` selects a fresh evidence directory; publish logs
and `hashes.json` are retained there on both success and failure.

The default WorkerProcessHost/Portal check runs in local-ci. CI checks all eight
web apps, WorkerProcessHost and isolated roots under both Windows PowerShell 5.1
and PowerShell 7, then compares every file and package hash between the two
independent Windows runners. `-Project` can select a smaller project set.
The script explicitly sets `GITHUB_ACTIONS=false` during the probe, so CI tests
the installer/local contract rather than accidentally hiding its regression.
Pester also checks archive order/timestamp independence, generated worker
metadata and sensitivity to a real content change.

## Recorded red/green evidence (2026-10-09)

Windows, .NET SDK 10.0.401, Release/net10.0, PowerShell 7.6.6. Baseline:
`f57059b9`. Two copies of the same sources, timestamps 2020-01-02 versus
2024-05-06. Before the fix, 8 of 45 files differed: DLL/PDB pairs for Artifacts,
EventPublisher.Abstractions, Worker.Abstractions and WorkerProcessHost; all
37 other files matched. After the fix, all 45 files and the complete packages
matched.

| Evidence | First path | Second path |
| --- | --- | --- |
| Red WorkerProcessHost DLL SHA-256 | `F15BF87BDFBB23A849F561FFF5C12895791981EF8FA2944F0BDDE86F7829EB64` | `0786681BC7B00D4DFD9EDA24DECA08870B418DCBDC65D20ABD7F727975D905A0` |
| Red WorkerProcessHost MVID | `07c3fe13-39c9-4930-8216-54d47b4847a9` | `deeb553b-17e1-445e-a57e-d56b158b1161` |
| Red package SHA-256 | `24FC3AE469AC2D7E89FD3657B72940F5D7A82F94D59774E55F40342711E92BD8` | `03065ACD420EC8904DFEFA11E25473ACAE9DDB54000D23E4C51F4A646F4C01FE` |
| Green WorkerProcessHost DLL SHA-256 | `710227ED7501014A9BFBEDAA2A8E9E86FFC5A2DDECCC79996E528630A87676CB` | same |
| Green WorkerProcessHost MVID | `0aaeb256-c5d6-49df-801e-45c689e43257` | same |
| Green package SHA-256 | `6579185C7E76F7D4A525F00152CA70F33502C77B962F8A05C91D5F0DED5859B2` | same |

The red PE CodeView paths contained each absolute checkout path. Both green
paths were `/_/openmoduleplatform/OpenModulePlatform.WorkerProcessHost/obj/Release/net10.0/OpenModulePlatform.WorkerProcessHost.pdb`.
Packages use fixture identity version 1.0.0 and are regression evidence, not
deployment packages. The component and repository versions must still advance
once when introducing these new assembly bytes.

## Consumers

The root props file is OMP-owned; referenced OMP projects load it directly.
Consumers must also sync these Check 15 files for their own web/Razor projects:

- `build/OpenModulePlatform.DeterministicStaticWebAssets.targets`
- `build/DeterministicRazor/DeterministicRazor.csproj`
- `build/DeterministicRazor/DeterministicRazorGenerator.cs`
- `scripts/omp/bump-version.ps1` (published definition JSON now uses LF immediately)

Keep the root `Directory.Build.targets` import. Do not replace consumer props
wholesale: preserve their own root and isolated-output PathMap policy. The
shared target supplies a project-local map only when PathMap is empty. Referenced
OMP binaries and consumer web outputs change once with this fix, so bump every
artifact containing them before packaging. This includes non-web artifacts that
actually reference Web.Shared. Unrelated services need no bump.

Consumers using the canonical artifact packer receive its fix from the updated
OMP checkout; independently copied packers must be refreshed. Update the OMP
checkout as a unit (packer, PowerShell loader, encoding source and build files).
Consumer changes belong in their own repositories.

## Razor source and static endpoint contract

Compiler PathMap alone does not normalize the text produced by the SDK Razor
generator. Absolute paths remain in `#pragma checksum` and `#line`, and generated
source is embedded in portable PDBs. TagHelper IDs contain generated-text offsets,
so different path lengths also change IL. Rewriting directives after generation
fixes Web.Shared but still leaves Portal different.

The build-only [Razor adapter](../build/DeterministicRazor/README.md) maps the
generator's AdditionalText paths and project directory through the compiler's
source resolver before generation. Contents and TargetPath/CssScope metadata are
preserved. Both Razor Pages and Blazor components keep the selected SDK compiler,
portable symbols and embedded sources; no debug information is discarded.
On Windows, Csc resolves a virtual `/_/` directive against the current drive.
The target maps that drive-qualified virtual root back to `/_/` as well. The
metadata probe rejects any physical PDB document and requires generated Razor
documents, so two builds on the same drive cannot hide a cross-drive regression.
Razor command-line builds use a fresh compiler process to avoid retained SDK
parsing state across relocated builds with identical virtual paths. The bump
helper writes LF JSON so a local post-bump publish and a fresh checkout publish
use the same definition bytes, as declared by `.gitattributes`.

The static-assets target now sorts Endpoints, Selectors, ResponseHeaders and
EndpointProperties ordinally and pins Last-Modified. It preserves all other
values and unknown arrays. `test-static-web-assets.py` deliberately reverses the
sets and changes timestamps, then verifies byte equality, idempotence, preserved
ETags/selection metadata and sensitivity to an actual asset change.

Baseline `58edc7b9`, SDK 10.0.401, two directories of different lengths and source
timestamps 2020/2024: 4 of 397 Portal publish files differed (Portal and Web.Shared
DLL/PDB). All 93 embedded Portal Razor sources matched after accounting for
physical source paths and TagHelper IDs; there were no other source differences.
View identifiers and RazorCompiledItem attributes were unchanged.
Static manifests, scoped CSS, compressed files and asset fingerprints all matched
in that baseline run; the adversarial ordering test covers the intermittent
manifest-order failure separately. The red package hashes were
`4181ECB0665979012E9A6C018D15932156BE8730E521515E9D62866E04337C15` and
`21F0D6BC9CF41A527827F3A951308F2E26A10C0721874B73D5D29FF2D944D9F4`.

After the fix, all 397 Portal publish files matched, including DLL/PDB, manifests,
CSS, compressed assets and fingerprints. Both package SHA-256 values were
`1E238FFC50DA52D3CC8C958585EE63739E16ED76F3859E714B77F61A3190FB04`.
The metadata probe measured 506 virtual PDB documents, including 103 generated
Razor sources. As a negative control, it rejected the intermediate implementation
that left Windows drive letters in Razor document names. WorkerProcessHost also
remained byte-identical (45 files). The eight-web-app two-path matrix passed;
CI repeats the complete matrix with the final virtual-root guard in both shells.
These recorded fixture hashes predate subsequent upstream asset/version changes;
each current run retains its complete `hashes.json` for comparison.

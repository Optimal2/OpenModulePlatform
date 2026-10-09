# Deterministic local artifact builds

Artifact identity must survive a fresh checkout or an isolated build directory.
`Directory.Build.props` enables `ContinuousIntegrationBuild` and `Deterministic`
for local builds as well as CI, uses portable PDBs, and maps source paths to
`/_/openmoduleplatform/`. Isolated `obj/<project>` and `bin/<project>` directories
map to the equivalent virtual `<project>/obj` and `<project>/bin` paths.
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
WorkerProcessHost and its three OMP dependencies in separate paths (including
spaces), and changes source timestamps between copies. It compares SHA-256 for
every published file and the production artifact packer's complete zip. With
`-UseIsolatedBuildRoots`, the first build is in-tree and the other two use
different isolated roots. Use a scratch directory outside any OMP checkout for
that variant: the existing isolation guard correctly rejects roots underneath
an OMP checkout. `-OutputRoot` selects a fresh evidence directory; publish logs
and `hashes.json` are retained there on both success and failure.

The normal check runs in local-ci and CI; CI additionally checks isolated roots.
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

The changed root props file is OMP-owned, not a Check 15 verbatim shared file;
referenced OMP projects load it directly. No existing shared `build/*.targets`
file changed. Consumers need the updated OMP checkout and new component versions
when their packages contain the rebuilt OMP binaries. Their own assembly paths
require an equivalent policy in their own repository-specific build props;
do not replace those props wholesale with OMP's. Consumers using the canonical
artifact packer receive the zip fix from the updated OMP checkout; independently
copied packers must be refreshed. Keep consumer changes in their owning repos.
The inspected consumer build scripts call the packer in their OMP checkout;
they do not need a copied helper sync. Update that checkout as a unit (packer,
PowerShell loader and C# encoding source). Consumer artifact versions must
advance before publishing their new-format packages.

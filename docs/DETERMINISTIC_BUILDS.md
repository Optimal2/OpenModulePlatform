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
retains SourceLink. This is a same-input, same-toolchain guarantee, not a promise
of equal bytes across SDK/runtime versions, signing operations or local/CI
SourceLink settings.

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

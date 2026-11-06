# Work package 4: name the projected value with `Select`

Rule: `cs/linq/missed-select`

Validated base: `9a88513babab1019382d744ee8c5dd4c82aa3b84`

## Scope and intent

This change is deliberately limited to three projections:

- `MaintenanceQuarantine.EnumerateEntries` projects the two existing lazy path
  enumerations with `Select(directory => new DirectoryInfo(directory))` and
  `Select(file => new FileInfo(file))`. The loops now name the information object
  that their bodies actually consume. The directory and file passes remain in
  their original order, and their bodies still read the same lengths and
  `LastWriteTimeUtc` values. Recursive enumeration still uses
  `OmpReparsePointGuard.RecursiveNoFollow`.
- `PackageLibraryDefinitionGate.FindSkippedSourceRoots` iterates
  `configuredRoots.Select(NormalizeSourceRoot)`. The normalized root is therefore
  the loop variable, matching the adjacent `resolvedRoots` projection. The
  stateful `seen` and `resolved` checks, callbacks, skip list order, and reasons
  remain inside the original loop and in the same order.

`Enumerable.Select` is deferred and preserves source order. These edits add no
`ToList`, `ToArray`, filtering, `OfType`, exception-handling change, or alternate
file-system API. In particular, the quarantine does not switch to
`DirectoryInfo(root).EnumerateDirectories/Files`, so the existing metadata access
pattern is retained. One info object is constructed per source path immediately
before the unchanged body consumes it.

## Direct characterization coverage

`EnumerateEntries_CountsOwnedContentsAndSidecarWithoutFollowingAJunction` calls
`MaintenanceQuarantine.EnumerateEntries` directly. Its unique fixture contains:

- one quarantined directory with ordinary files of 3 and 5 bytes;
- one reason sidecar of 7 bytes;
- a junction inside that directory to another directory owned by the same
  fixture, containing a 1,024-byte file outside the quarantine.

The test asserts two entries in directory-then-sidecar order and sizes of 8 and
7 bytes. Thus the assertion covers the actual caller's directory contents and
sidecar accounting, not only the shared reparse-point guard. Both junction
endpoints stay inside the isolated fixture. Cleanup removes the junction itself
non-recursively before the fixture's recursive cleanup, so cleanup does not
traverse the link. Junction creation uses the repository's hidden `cmd.exe /c
mklink /J` process pattern and requires no privilege change.

## Focused test commands

The final and post-restoration runs used these commands from the repository
root. `TEMP` and `TMP` were redirected to an isolated job-owned directory.

```sh
dotnet test OpenModulePlatform.HostAgent.Runtime.Tests/OpenModulePlatform.HostAgent.Runtime.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~OpenModulePlatform.HostAgent.Runtime.Tests.Services.MaintenanceQuarantineTests" --logger "console;verbosity=normal"
dotnet test OpenModulePlatform.Bootstrapper.Tests/OpenModulePlatform.Bootstrapper.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~OpenModulePlatform.Bootstrapper.Tests.PackageLibraryDefinitionGateTests" --logger "console;verbosity=normal"
```

The original baseline used the same commands without `--no-restore`, because a
fresh worktree had no NuGet assets files.

| Stage | Quarantine result | Gate result | Exit codes |
| --- | --- | --- | --- |
| Original source and tests | 19 passed, 0 failed | 21 passed, 0 failed | 0, 0 |
| New direct test, before either `Select` rewrite | 20 passed, 0 failed | unchanged baseline: 21 passed, 0 failed | 0, 0 |
| Final `Select` candidate | 20 passed, 0 failed | 21 passed, 0 failed | 0, 0 |
| Exact candidate bytes restored after each mutation | 20 passed, 0 failed | 21 passed, 0 failed | 0, 0 |

The new direct test was green before the clarity rewrite and remained green
after it. Only runs with a nonzero console test total are counted as evidence;
preliminary exact-class equality filters matched zero tests and were discarded.

## Causal mutation proof

The mutations below were made individually to the actual final production
projection, not to a copy of its logic. Each compiled, produced a native test
failure, and was then restored byte-for-byte.

### Normalization must remain in the final gate projection

Mutation:

```diff
-foreach (var root in configuredRoots.Select(NormalizeSourceRoot))
+foreach (var root in configuredRoots)
```

Running the focused gate class produced exit code 1: 21 total, 20 passed, 1
failed. `SkippedConfiguredSourceRootsAreNamedWithTheirReason` failed its
existing duplicate/trailing-separator assertion with expected 2 skipped roots
but actual 3. Restoring the candidate produced SHA-256
`86c9797e50ec3c3adbb3ed2d9c7a0d6486e4cdd446ae36f0849ccf4990310bf5`
for `OpenModulePlatform.Bootstrapper/PackageLibraryDefinitionGate.cs`; the same
focused class then passed 21/21 with exit code 0.

### The actual quarantine caller must retain no-follow enumeration

Mutation:

```diff
-foreach (var file in info.EnumerateFiles("*", OmpReparsePointGuard.RecursiveNoFollow))
+foreach (var file in info.EnumerateFiles("*", SearchOption.AllDirectories))
```

Running the focused quarantine class produced exit code 1: 20 total, 19 passed,
1 failed. The new direct `EnumerateEntries` assertion reported expected size 8
but actual 1,032, exactly the 1,024 bytes behind the artificial junction being
added. This is direct evidence for this caller; no unrelated
`OmpReparsePointGuard` test is used as a substitute. Restoring the candidate
produced SHA-256
`46d3ce3cf15576cf502460f533f4bf28871ed84ff904ce036a8d211a36d1e182`
for `OpenModulePlatform.HostAgent.Runtime/Services/MaintenanceQuarantine.cs`;
the same focused class then passed 20/20 with exit code 0.

## Limitations

- Validation was intentionally narrow: only the two inspected, fixture-free test
  classes were run, never either whole test assembly and never full local CI.
  No SQL, import, installation, service, deployment, release, or shared runtime
  operation was performed.
- The direct junction test is Windows-only and reports a skip on other operating
  systems. In the recorded validation it executed and passed; it was not skipped.
- The fixture proves no-follow sizing and entry accounting for one owned NTFS
  junction. It does not attempt to characterize every file-system error,
  reparse-point type, or non-Windows platform.
- These results support a patch proposal. They are not a claim of final source
  integration, implementation acceptance, version preparation, commit, push, or
  CI completion.

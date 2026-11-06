# WP2 — cs/local-not-disposed (OpenModulePlatform)

Campaign: github-fynd-standardfynd-publika-repon-wp2. Scope owner: cs/local-not-disposed only.
Base snapshot: scan of main@3d5feaff (2026-11-04); line numbers below are the scanned lines,
verified by content (the worktree has since moved a few lines through these edits).

## Result

15 owned findings: 1 removed at the source, 14 documented as correct-by-design with a
concise `// CodeQL cs/local-not-disposed: kept - ...` rationale comment at each site,
following the repository's existing convention (cf. `cs/unmanaged-code: kept` in
`NativeAccountInterop.cs` and `Program.cs`). No finding describes a real leak: flagged
allocations either transfer ownership to a caller or framework object that disposes them,
or are deliberate process-lifetime console writers that must remain open for later standard
output/error writes. The two test-side ownership contracts that carry four and one findings
now have regression tests that assert disposal.

No production behavior changed (comments in production files only, no logic edits). The
required packaged-component and repository version metadata is updated during integration.

CodeQL status remains unmeasured until a real rescan. An open analyzer warning is not a
failing test and is not RED evidence; the causal test experiments below provide that proof.

## Per-finding dispositions

| # | File:scanned line | Disposition |
|---|---|---|
| 1 | OpenModulePlatform.Installer.Tests/AccountPasswordValidatorTests.cs:33 | Removed at source: the NUL-guard FakeLogon must never run, so it now assigns `token = null!` instead of minting a SafeHandle. If the guard ever regresses, the test first fails on `Assert.Equal(0, calls)` (expected `0`, actual `1`) because the supplied FakeLogon runs. |
| 2 | OpenModulePlatform.Installer.Tests/AccountPasswordValidatorTests.cs:71 | Kept with rationale comment: the fake passes the token to `Validate` via `out`; `Validate` wraps it in `using` (NativeAccountInterop.cs). Proven by the existing `Assert.True(returnedToken.IsClosed)`. |
| 3 | OpenModulePlatform.Installer.Tests/AccountPasswordValidatorTests.cs:97 | Kept with rationale comment: same ownership transfer; `Validate` disposes even on the failure path. Proven by the existing `Assert.True(returnedToken.IsClosed)`. |
| 4 | OpenModulePlatform.Portal.Tests/Services/ModuleFragmentWidgetTests.cs:543 | Kept with rationale comment: the stub returns the message through `SendAsync` to the HttpClient pipeline; `PortalModuleFragmentService` disposes it (`using var response`). Proven by new `Fetch_DisposesStubbedFragmentResponse`. |
| 5 | OpenModulePlatform.Portal.Tests/Services/ModuleFragmentWidgetTests.cs:655 | Kept with rationale comment: same as 4 (non-success response with content). |
| 6 | OpenModulePlatform.Portal.Tests/Services/ModuleFragmentWidgetTests.cs:671 | Kept with rationale comment: same as 4. |
| 7 | OpenModulePlatform.Portal.Tests/Services/ModuleFragmentWidgetTests.cs:879 | Kept with rationale comment: same as 4 (cached 500 response; the cached path also goes through the same `using`). |
| 8 | OpenModulePlatform.Portal.Tests/Pages/ConfigOverlaysPageTests.cs:572 | Kept with rationale comment: created only on the success path and returned to the HttpClient caller, which disposes it (`using var response` in `PostFormDisposesContentForEverySendOutcome`); failure paths never receive it. |
| 9 | OpenModulePlatform.Installer/Program.cs:270 | Kept with rationale comment: `Console.SetOut` installs a process-lifetime writer for later standard-output writes; this does not assume automatic disposal by `Console`, and disposing the writer here would break later output. |
| 10 | OpenModulePlatform.Installer/Program.cs:269 | Kept with rationale comment: same as 9 for `Console.SetError`. |
| 11 | OpenModulePlatform.WorkerManager.WindowsService/Services/SystemLogNLogTarget.cs:59 | Kept with rationale comment: `AsyncTargetWrapper` is added to the NLog configuration and logging rules (`config.AddTarget`, `LogManager.Configuration`); NLog owns it for process life. |
| 12 | OpenModulePlatform.WorkerManager.WindowsService/Services/SystemLogNLogTarget.cs:40 | Kept with rationale comment: `DatabaseTarget` is owned by the wrapper above. |
| 13 | OpenModulePlatform.HostAgent.WindowsService/Services/SystemLogNLogTarget.cs:60 | Kept with rationale comment: same as 11 (HostAgent twin). |
| 14 | OpenModulePlatform.HostAgent.WindowsService/Services/SystemLogNLogTarget.cs:41 | Kept with rationale comment: same as 12 (HostAgent twin). |
| 15 | OpenModulePlatform.HostAgent.Runtime.Tests/Services/DeploymentHealthProbeTests.cs:27 | Kept with rationale comment: returned through `SendAsync`; `ProbeDeploymentAsync` disposes it (`using var response`). Proven by new `Probe_DisposesProbeResponse`. |

## Regression tests (new)

- `Fetch_DisposesStubbedFragmentResponse` (ModuleFragmentWidgetTests): serves a
  `DisposeTrackingResponse`, asserts `IsDisposed == false` before the call (tracking is
  honest, not stuck-true) and `== true` plus `IsLoaded` after. Guards findings 4-7.
- `Probe_DisposesProbeResponse` (DeploymentHealthProbeTests): `Handler` now records every
  returned `TrackedResponse`; asserts exactly one response was served and it is disposed.
  Guards finding 15.

No new tests were added for findings 1-3 (existing `IsClosed` assertions already prove
disposal) or 8 (the existing `using var response` in the test owns the returned response).
Findings 9-14 have no dedicated runtime-disposal test in this change. That is a coverage
limitation, not a claim that NLog configuration ownership or the process-lifetime console-writer
rationale cannot be tested with a purpose-built, isolated harness.

## Causal RED/GREEN evidence

Each experiment changed only the relevant production response declaration from `using var`
to `var`, ran the single owning test, restored the declaration, verified a byte-identical
source hash and an empty diff for that production file, and ran the same test again. No
mutation was committed. No solution build or full CI run occurred while a mutation was present.

### Findings 4-7: fragment response

Initial GREEN — source SHA-256
`0d64ba189351de14aa061aedfce9686e83294a0bca37a5a096c8c1e6eeaae635`:

```sh
dotnet test OpenModulePlatform.Portal.Tests/OpenModulePlatform.Portal.Tests.csproj --configuration Release --filter "FullyQualifiedName~ModuleFragmentWidgetTests.Fetch_DisposesStubbedFragmentResponse" --nologo
```

Native exit 0; 1 passed, 0 failed.

RED after removing only the response `using` — mutated source SHA-256
`a6b36ae7d8a52e5fc9145702c6ae694b0ae0130fa66e57d23a534f78bec686e5`:

```sh
dotnet test OpenModulePlatform.Portal.Tests/OpenModulePlatform.Portal.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~ModuleFragmentWidgetTests.Fetch_DisposesStubbedFragmentResponse" --nologo
```

Native exit 1; 0 passed, 1 failed. `Fetch_DisposesStubbedFragmentResponse` failed at its
`Assert.True(response.IsDisposed)` assertion (line 568): expected `True`, actual `False`.
This was a test assertion failure, not a build failure.

Restored GREEN — source SHA-256 returned to
`0d64ba189351de14aa061aedfce9686e83294a0bca37a5a096c8c1e6eeaae635`; `git diff --exit-code
-- OpenModulePlatform.Portal/Services/PortalModuleFragmentService.cs` returned 0:

```sh
dotnet test OpenModulePlatform.Portal.Tests/OpenModulePlatform.Portal.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~ModuleFragmentWidgetTests.Fetch_DisposesStubbedFragmentResponse" --nologo
```

Native exit 0; 1 passed, 0 failed.

### Finding 15: health-probe response

Initial GREEN — source SHA-256
`fd303c2a7dbbf1fa38bb1449525d6f58a0e9a93e1998ff8e64159906554c11c3`:

```sh
dotnet test OpenModulePlatform.HostAgent.Runtime.Tests/OpenModulePlatform.HostAgent.Runtime.Tests.csproj --configuration Release --filter "FullyQualifiedName~DeploymentHealthProbeTests.Probe_DisposesProbeResponse" --nologo
```

Native exit 0; 1 passed, 0 failed.

RED after removing only the response `using` — mutated source SHA-256
`29cdd55afbf38bd59bb05e6dd553e5e33351d657a8fabfff5061defbdf2a5f4a`:

```sh
dotnet test OpenModulePlatform.HostAgent.Runtime.Tests/OpenModulePlatform.HostAgent.Runtime.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~DeploymentHealthProbeTests.Probe_DisposesProbeResponse" --nologo
```

Native exit 1; 0 passed, 1 failed. `Probe_DisposesProbeResponse` failed at its
`Assert.True(response.IsDisposed)` assertion (line 30): expected `True`, actual `False`.
This was a test assertion failure, not a build failure.

Restored GREEN — source SHA-256 returned to
`fd303c2a7dbbf1fa38bb1449525d6f58a0e9a93e1998ff8e64159906554c11c3`; `git diff --exit-code
-- OpenModulePlatform.HostAgent.Runtime/Services/WebAppHealthMonitor.cs` returned 0:

```sh
dotnet test OpenModulePlatform.HostAgent.Runtime.Tests/OpenModulePlatform.HostAgent.Runtime.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~DeploymentHealthProbeTests.Probe_DisposesProbeResponse" --nologo
```

Native exit 0; 1 passed, 0 failed.

## Validation (this worktree, exit codes are native)

- `dotnet test OpenModulePlatform.Installer.Tests/OpenModulePlatform.Installer.Tests.csproj --configuration Release --filter "FullyQualifiedName~AccountPasswordValidatorTests" --nologo`: exit 0, 6/6 passed.
- `dotnet test OpenModulePlatform.HostAgent.Runtime.Tests/OpenModulePlatform.HostAgent.Runtime.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~DeploymentHealthProbeTests" --nologo`: exit 0, 4/4 passed (3 existing + 1 new).
- `dotnet test OpenModulePlatform.Portal.Tests/OpenModulePlatform.Portal.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~ModuleFragmentWidgetTests|FullyQualifiedName~ConfigOverlaysPageTests" --nologo`: exit 0, 176/176 passed (including 1 new).
- `dotnet build OpenModulePlatform.slnx --configuration Release --nologo`: exit 0, 0 warnings, 0 errors.
- `git diff --check`: clean.
- Remaining warnings seen during scoped test builds (CA1416 in Web.Shared, CS8604 in Auth,
  xUnit1031/2031 in unrelated test files) are pre-existing and outside this scope.

## Confidence and coverage

Confidence is high for findings 4-7 and 15 because the mutation experiments demonstrate a
causal disposal assertion, not merely a passing test. Existing assertions cover findings 1-3;
the adjacent ownership path covers finding 8. Findings 9-10 rely on the deliberate
process-lifetime use of the console writers, without an automatic-disposal or cleanup-transfer
guarantee from `Console`; findings 11-14 rely on NLog configuration ownership verified by code
reading. Those six have no dedicated dynamic disposal test here. CodeQL clearance remains unmeasured
until a fresh rescan. If a documented site is still flagged, the next step is the GitHub
Dismiss flow (an operator decision per the playbook), not another ownership rewrite.

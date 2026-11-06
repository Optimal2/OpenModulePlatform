# WP4 — cs/missed-using-statement clarification

Campaign: github-fynd-standardfynd-publika-repon. Baseline:
`9a88513babab1019382d744ee8c5dd4c82aa3b84` (`repositoryVersion` 0.3.1110).

## Change and rationale

`ObserveTasksAndDisposeCancellationSourceAsync` previously disposed its owned
`CancellationTokenSource` in the null-tasks branch and again in a `finally` block. One lexical
`using (cancellationSource)` scope now covers the complete method body. This states the ownership
once, removes duplicate disposal paths, and still disposes only after the task observation has
finished. The null return, `ConfigureAwait(false)`, catch/aggregate behavior, task ownership, and
timeout handling are unchanged.

The new `ImportCancellationSource_WhenTasksAreNull_ReturnsNullAndDisposesSource` test guards the
null-tasks ownership path: the returned aggregate is null and subsequent token registration throws
`ObjectDisposedException`. The existing late-failure test continues to prove that the CTS remains
usable while `WhenAll` is pending, that a late failure is returned, and that disposal occurs after
all controlled tasks settle.

This is a clarity cleanup of already-correct behavior, not a reproduced runtime defect. No
production source, API, SQL, runtime configuration, sibling missed-using site, or version matrix
entry changed. The accompanying WP2 documentation edit only corrects its description of
process-lifetime console writers; it does not claim that `Console` assumes disposal ownership.

## Characterization versus causal proof

Both focused tests passed against the **original** helper before its disposal structure was
rewritten. That run characterizes the existing ownership contract; it is not evidence that the old
code was defective. The later mutation runs are the causal proof: removing disposal caused the
missing-disposal assertions to fail, and disposing before awaiting unsettled tasks caused the
existing pending-CTS check to fail. Analyzer or scanner counts are not runtime-test RED evidence.

The method bodies use only controlled tasks, `TaskCompletionSource`, and a CTS; they perform no real
package import or SQL operation. They are not database-free invocations, however: this xUnit class
uses `IClassFixture<SeedSqlOrderingTestFixture>`, whose existing lifecycle provisions a uniquely
named isolated local test database, applies the real core setup script, and drops that same test
database. Every run below completed two or one genuine test-body cases with nonzero native executed
counts. Fixture setup failure would be infrastructure failure, not the intended causal RED.

## Commands and native results

All commands ran from the repository root with real `dotnet test` builds; none used `--no-build`.
TRX counters below are read from the native result files, not inferred. `$AO_JOB_SCRATCH` is the
approved temporary-output root.

Original-helper characterization, after adding only the null-tasks test (source SHA-256
`900bd8015717ad630a3ee7f6b4cbf1ff9a540fbc76f9364de6aeea1ec5ed9ac3`):

```sh
dotnet test OpenModulePlatform.Portal.Tests/OpenModulePlatform.Portal.Tests.csproj --configuration Release --nologo --filter "FullyQualifiedName~UniversalPackageSeedSqlOrderingTests.ImportCancellationSource_" --results-directory "$AO_JOB_SCRATCH/wp4-trx/original" --logger "trx;LogFileName=original.trx"
```

Exit 0; TRX total 2, executed 2, passed 2, failed 0.

Final using-scope GREEN (source SHA-256
`e05c35186e293f906d5a1c37f1b2a81e94699f6ed7df7314495c8c648248e159`):

```sh
dotnet test OpenModulePlatform.Portal.Tests/OpenModulePlatform.Portal.Tests.csproj --configuration Release --nologo --filter "FullyQualifiedName~UniversalPackageSeedSqlOrderingTests.ImportCancellationSource_" --results-directory "$AO_JOB_SCRATCH/wp4-trx/final-green" --logger "trx;LogFileName=final-green.trx"
```

Exit 0; TRX total 2, executed 2, passed 2, failed 0.

### Mutation 1: no disposal

Removed the lexical disposal scope while leaving a valid method body (mutated source SHA-256
`bd8e103a56628aef3045fdcda4df70c61530063de0c7d08c453561742470b829`):

```sh
dotnet test OpenModulePlatform.Portal.Tests/OpenModulePlatform.Portal.Tests.csproj --configuration Release --nologo --filter "FullyQualifiedName~UniversalPackageSeedSqlOrderingTests.ImportCancellationSource_" --results-directory "$AO_JOB_SCRATCH/wp4-trx/red-no-disposal" --logger "trx;LogFileName=red-no-disposal.trx"
```

Exit 1; TRX total 2, executed 2, passed 0, failed 2. Both tests reached their genuine
`Assert.Throws<ObjectDisposedException>` assertions, which failed because no exception was thrown.
This was not a compile error, missing asset, zero-test run, or fixture failure.

Restored the exact final source bytes (SHA-256 returned to
`e05c35186e293f906d5a1c37f1b2a81e94699f6ed7df7314495c8c648248e159`) and reran:

```sh
dotnet test OpenModulePlatform.Portal.Tests/OpenModulePlatform.Portal.Tests.csproj --configuration Release --nologo --filter "FullyQualifiedName~UniversalPackageSeedSqlOrderingTests.ImportCancellationSource_" --results-directory "$AO_JOB_SCRATCH/wp4-trx/restored-green-1" --logger "trx;LogFileName=restored-green-1.trx"
```

Exit 0; TRX total 2, executed 2, passed 2, failed 0.

### Mutation 2: premature disposal

Added one disposal call immediately before the `try`/`await` of the unsettled tasks (mutated source
SHA-256 `dd6853c7ea3d1b0cf9b8b7af5189e8293996ed67bbd13f043acaa6dd64f05c25`):

```sh
dotnet test OpenModulePlatform.Portal.Tests/OpenModulePlatform.Portal.Tests.csproj --configuration Release --nologo --filter "FullyQualifiedName~UniversalPackageSeedSqlOrderingTests.ImportCancellationSource_WhenCleanupObservationTimesOut_StaysAliveAndObservesLateFailures" --results-directory "$AO_JOB_SCRATCH/wp4-trx/red-premature-disposal" --logger "trx;LogFileName=red-premature-disposal.trx"
```

Exit 1; TRX total 1, executed 1, passed 0, failed 1. The existing test failed at its pending-CTS
registration with `ObjectDisposedException: The CancellationTokenSource has been disposed.` This
directly demonstrates why disposal must remain after observation. It was not a compile error,
missing asset, zero-test run, or fixture failure.

Restored the exact final source bytes again (SHA-256 returned to
`e05c35186e293f906d5a1c37f1b2a81e94699f6ed7df7314495c8c648248e159`) and reran both cases:

```sh
dotnet test OpenModulePlatform.Portal.Tests/OpenModulePlatform.Portal.Tests.csproj --configuration Release --nologo --filter "FullyQualifiedName~UniversalPackageSeedSqlOrderingTests.ImportCancellationSource_" --results-directory "$AO_JOB_SCRATCH/wp4-trx/restored-green-2" --logger "trx;LogFileName=restored-green-2.trx"
```

Exit 0; TRX total 2, executed 2, passed 2, failed 0. No mutation was committed.

## Scope and limitations

These controlled-task tests prove CTS ownership and observation timing only. They cannot exercise
real concurrent SQL imports, database lock behavior, package-import failures, or production runtime
cleanup. The existing class fixture's isolated database lifecycle does not turn the method bodies
into integration tests. No scanner-clearance claim is made without a fresh scan.

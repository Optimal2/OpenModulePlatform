# Cross-Repository Error-Handling Convention

> Scope: OMP+ODV ecosystem (10 repositories).  
> Status: Documentation-only audit. No source code was changed.  
> Date: 2026-07-16

> **Line anchors (note added 2026-09-21).** The `file.cs:NN` references in
> this document were measured when the audit was written and many have
> drifted since. Resolve every reference by the **symbol or file name**, not by
> the line number; the same note applies in `configuration.md`.

This document summarizes how each repository currently handles errors, highlights divergences, and recommends a single standard pattern per ecosystem (.NET and JS/npm).

---

## 1. Per-Repository Error-Handling Map

### OpenModulePlatform (.NET)

#### Custom exception types
- Domain-specific exceptions exist but are sparse:
  - `HtmlContentFileException` — `OpenModulePlatform.Web.ContentWebAppModule/Services/HtmlContentFileLoader.cs:168` (thrown at `:55`, `:61`, `:143`).
  - `ServerReportException` — `OpenModulePlatform.Web.ContentWebAppModule/Services/ServerReportDefinitionLoader.cs:198` (thrown at `:66`, `:72`, `:87`, `:156`, `:166`, `:173`, `:179`).
  - `TestDbException` (test-only) — `OpenModulePlatform.HostAgent.Runtime.Tests/Services/HostAgentEngineTierDTests.cs:266`.
- Standard BCL exceptions are used heavily for guards/state violations:
  - `ArgumentNullException.ThrowIfNull(...)` — `OpenModulePlatform.Web.Shared/Services/BannerService.cs:46`, `OpenModulePlatform.HostAgent.Runtime/Services/ArtifactProvisioner.cs:24`.
  - `InvalidOperationException` — `OpenModulePlatform.WorkerProcessHost/Models/WorkerProcessSettings.cs:34`, `OpenModulePlatform.Web.Shared/Services/MessageService.cs:699`, `OpenModulePlatform.Portal/Services/OmpAdminRepository.Editor.cs:369`.
  - `ArgumentException` / `ArgumentOutOfRangeException` — `OpenModulePlatform.Web.Shared/Services/BannerService.cs:650`, `OpenModulePlatform.Web.Shared/Services/MessageService.cs:422`.

#### Try/catch patterns
- Specific typed catches are preferred:
  - `OpenModulePlatform.Web.Shared/Services/MessageService.cs:475` — `catch (SqlException ex) when (ex.Number is 2601 or 2627)` with rollback + retry.
  - `OpenModulePlatform.HostAgent.WindowsService/Services/HostAgentRpcHostedService.cs:100-112` — separate catches for `IOException`, `ObjectDisposedException`, `InvalidOperationException`, `UnauthorizedAccessException`.
  - `OpenModulePlatform.Web.ContentWebAppModule/Services/ServerReportRenderer.cs:39` — `catch (ServerReportException ex)`.
- Filtered broad catches are common for expected failures:
  - `OpenModulePlatform.WorkerManager.WindowsService/Services/WorkerManagerHostedService.cs:93` — `catch (Exception ex) when (IsRecoverableWorkerManagerFailure(ex))`.
  - `OpenModulePlatform.HostAgent.Runtime/Services/HostAgentEngine.cs:366` — `catch (Exception ex) when (IsExpectedDeploymentFailure(ex))`.
  - `OpenModulePlatform.Portal/Services/PortableModulePackageService.cs:507` — `catch (Exception ex) when (IsExpectedUniversalImportFailure(ex))`.
- Bare broad catches are rare and mostly at top-level loops:
  - `OpenModulePlatform.HostAgent.WindowsService/Program.cs:101` — startup emergency handler.
  - `OpenModulePlatform.EventPublisher.Sql/SqlPushEventPublisher.cs:42` — logs then rethrows.
  - `OpenModulePlatform.HostAgent.Runtime/Services/MaintenanceScanScheduler.cs:100` — loop logs and continues.
- `throw;` is the norm; no `throw ex;` patterns were found.

#### Global error handling
- Shared web pipeline in `OpenModulePlatform.Web.Shared/Extensions/OmpWebHostingExtensions.cs`:
  - `:237` — `app.UseDeveloperExceptionPage()` in Development.
  - `:241` — `app.UseExceptionHandler("/Error")` in non-Development.
  - `:251` — `app.UseStatusCodePagesWithReExecute("/status/{0}")`.
  - `:255-279` — `/error` endpoint builds a fallback 500 HTML page via `OmpErrorDisplayModelFactory`.
  - `:281-307` — `/status/{statusCode}` endpoint handles status-code pages.
- Web apps consume the shared pipeline:
  - `OpenModulePlatform.Portal/Program.cs:74` — `app.UseOmpWebDefaults(..., mapRazorPages: true)`.
  - `OpenModulePlatform.Web.ContentWebAppModule/Program.cs:31` — `app.UseOmpWebDefaults(...)`.
  - `OpenModulePlatform.Web.iFrameWebAppModule/Program.cs:12` — `app.UseOmpWebDefaults(...)`.
- **Divergence:** `OpenModulePlatform.Auth/Program.cs` does not call `UseOmpWebDefaults` and has no explicit `UseExceptionHandler` / `UseStatusCodePages` configuration (only `UseOmpSecurityHeaders` at `:72`).

#### Log vs user-facing
- `_logger.LogError` inside catches:
  - `OpenModulePlatform.Web.Shared/Services/RbacService.cs:98`
  - `OpenModulePlatform.Web.ContentWebAppModule/Services/ServerReportRenderer.cs:57`
  - `OpenModulePlatform.HostAgent.WindowsService/Services/HostAgentHostedService.cs:102`
  - `OpenModulePlatform.HostAgent.Runtime/Services/HostAgentEngine.cs:368`
  - `OpenModulePlatform.HostAgent.Runtime/Services/HostAgentJobProcessor.cs:165`
  - `OpenModulePlatform.HostAgent.Runtime/Services/MaintenanceScanScheduler.cs:102`
- User-facing Razor/ModelState errors:
  - `OpenModulePlatform.Web.ContentWebAppModule/Pages/Admin/Edit.cshtml.cs:150` — `ModelState.AddModelError("Input.Slug", T(ex.Message))`.
  - `OpenModulePlatform.Portal/Pages/Messages/New.cshtml.cs:82` — `ModelState.AddModelError(nameof(DirectUserId), ex.Message)`.
  - `OpenModulePlatform.Portal/Pages/Admin/AppInstanceEdit.cshtml.cs:185` — `ModelState.AddModelError(string.Empty, T(ex.Message))`.
- API error responses:
  - `OpenModulePlatform.Portal/Pages/Index.cshtml.cs:281` — `return BadRequest(new { ok = false, message = ex.Message })`.
  - `OpenModulePlatform.Web.Shared/Extensions/OmpWebHostingExtensions.cs:338-440` — minimal-API endpoints return `Results.BadRequest()`.

#### Result/Either pattern
- No `OneOf`, `LanguageExt`, `Result<T>`, `Either<,>`, or `Try<>` library usage.
- Hand-rolled result records and status enums are common:
  - `OpenModulePlatform.Portal/Services/PortableModulePackageService.cs:2817` — `UniversalPackageImportResult`.
  - `OpenModulePlatform.Portal/Services/PortableModulePackageService.cs:2832` — `UniversalPackageImportItemResult`.
  - `OpenModulePlatform.HostAgent.Runtime/Models/ArtifactZipImportModels.cs:18` — `ArtifactZipImportResult`.
  - `OpenModulePlatform.HostAgent.Runtime/Models/AppDeploymentResult.cs:3` — `AppDeploymentResult`.
  - `OpenModulePlatform.Portal/Services/OmpUserAdminRepository.cs:1623` — `AddAuthLinkResult` / `CreateUserResult`.
  - Status enums: `CreateUserStatus` `:1631`, `AddAuthLinkStatus` `:1640`, `AddLocalPasswordLoginStatus` `:1650`.

#### Swallowed exceptions
- Test-only empty catch:
  - `OpenModulePlatform.HostAgent.Runtime.Tests/Services/OmpHostArtifactRepositoryHostDeploymentLeaseTests.cs:327` — `catch { // Best-effort cleanup; do not fail the test because cleanup failed. }`.
- Production log-and-continue / degrade:
  - `OpenModulePlatform.Portal/Pages/Admin/HostDeployments.cshtml.cs:272` — logs warning and renders without OmpAuth comparison data.
  - `OpenModulePlatform.Portal/Pages/Admin/HostDeployments.cshtml.cs:451` — audit-log write failures caught and logged so they do not block the operator action.
  - `OpenModulePlatform.HostAgent.Runtime/Services/MaintenanceScanScheduler.cs:100` — scheduled-scan loop logs error and continues.
  - `OpenModulePlatform.WorkerManager.WindowsService/Services/WorkerManagerHostedService.cs:455` — heartbeat publish failure logged as warning and skipped.
  - `OpenModulePlatform.WorkerManager.WindowsService/Services/WorkerManagerHostedService.cs:564` — worker runtime observation failure logged and skipped.
  - `OpenModulePlatform.WorkerManager.WindowsService/Services/WorkerManagerHostedService.cs:777` / `:800` / `:866` — startup orphan-scan failures logged and skipped.
  - `OpenModulePlatform.WorkerProcessHost/Services/WorkerProcessHostedService.cs:72` — top-level startup/run failure logs critical and stops application without rethrowing.

---

### Contoso (.NET)

#### Custom exception types
- No domain-specific `*Exception` classes.
- Standard BCL exceptions:
  - `InvalidOperationException` for config/runtime preconditions — `Contoso.Runtime/Services/ContosoBatchWriter.cs:32`, `:37`, `:42`, `:447`, `:457`, `:462`, `:467`, `:475`, `:483`, `:488`, `:496`, `:506`, `:513`; `Contoso.Runtime/Services/ContosoRepository.cs:256`, `:415`, `:423`, `:486`, `:1481`, `:1657`; `Contoso.ChannelTypes.FileDrop/FileDropChannelOptions.cs:134-396`; `Contoso.ChannelTypes.FileDrop/FileDropChannelType.cs:43`, `:748`, `:805`, `:859`, `:864`, `:870`, `:918`, `:923`, `:929`, `:1104`, `:1130`, `:1139`, `:1156`, `:1161`, `:1171`, `:1176`, `:1183`, `:1355`, `:1399`, `:1410`, `:1467`, `:1480`, `:1691`, `:1696`.
  - `ArgumentException` — `Contoso.Runtime/Services/ContosoRepository.cs:415`, `:423`; `Contoso.ChannelTypes.FileDrop/FileDropIndexFieldExtractor.cs:445`, `:496`, `:861`, `:1107`.
  - `IOException` / `FileNotFoundException` for IFS/batch file system errors — `Contoso.Runtime/Services/ContosoBatchWriter.cs:206`, `:231`, `:238`, `:271`, `:302`, `:357`, `:385`; `Contoso.ChannelTypes.FileDrop/FileDropChannelType.cs:1375`, `:1455`.
  - `NotSupportedException` — `Contoso.Tests/ReconcileChannelTypeArtifactRequirementsTests.cs:433`, `:462`, `:468` and unsupported enum/command cases.
  - `JsonException` rethrown with localized message — `Contoso.Web/Pages/ChannelConfigs/Index.cshtml.cs:470`.

#### Try/catch patterns
- Mostly specific catches (`SqlException`, `JsonException`, `IOException`, `DbException`, `UnauthorizedAccessException`, `InvalidOperationException`, `ArgumentException`, `FormatException`, `NotSupportedException`, `TimeoutException`, `OperationCanceledException`, `TargetInvocationException`, `ReflectionTypeLoadException`).
- Filtered broad catches:
  - `Contoso.Runtime/Services/ContosoWorkerEngine.cs:189` — broad `catch (Exception ex)` after many specific catches, used to log/publish channel failure and delay retry.
  - `Contoso.Runtime/Services/ContosoWorkerEngine.cs:217` — `catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException)` for heartbeat failures.
  - `Contoso.ChannelTypes.FileDrop/FileDropChannelType.cs:704` / `:1308` — filtered broad catches for job-lease renewal and command dispatch.
  - `Contoso.ChannelTypes.FileDrop/FileDropChannelType.cs:1771` — `catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)` for snapshot capture fallback.
- All rethrows use `throw;`; no `throw ex;` found.

#### Global error handling
- `Contoso.Web/Program.cs` has no explicit `UseExceptionHandler`, `UseDeveloperExceptionPage`, or `UseStatusCodePages`; it relies on `UseOmpWebDefaults`.
- `Contoso.Web/Pages/Error.cshtml.cs` is an `OmpErrorPageModelBase` subclass, so global error rendering is delegated to the shared OMP framework.

#### Log vs user-facing
- `_logger.LogError` / `LogWarning` inside catches:
  - `Contoso.Runtime/Services/ContosoWorkerEngine.cs:251`
  - `Contoso.ChannelTypes.FileDrop/FileDropChannelType.cs:1219`, `:1327`, `:2038-2084`
  - `Contoso.Runtime/Services/HostAgentRpcClient.cs:96`
- User-facing errors use `FeedbackError` / `FeedbackMessage` bound via `[TempData]`:
  - `Contoso.Web/Pages/ManualReview/Viewer.cshtml.cs:211-222`
  - `Contoso.Web/Pages/Jobs/Index.cshtml.cs:91-96`
  - `Contoso.Web/Pages/Lists/Index.cshtml.cs:654`
  - `Contoso.Web/Pages/ChannelConfigs/Index.cshtml.cs:260-269`
- No API controllers / `ProblemDetails` found.

#### Result/Either pattern
- Lightweight result object:
  - `Contoso.Abstractions/Models/ChannelConfigValidationResult.cs` — `IsValid` + `IReadOnlyList<string> Errors` with `Success()` / `Failed(...)` factories.
  - `Contoso.Runtime/Models/ContosoBatchWriteResult.cs` — data carrier for batch write output.
- `Contoso.Runtime/Services/HostAgentRpcClient.cs` returns `HostAgentEnsureArtifactResponse` with `Success`/`ErrorMessage` flags (`:64-68`, `:102-104`).
- No `OneOf`, `Either`, error-code enums, discriminated unions, or `Try` usage.

#### Swallowed exceptions
- Best-effort empty/comment-only catch blocks:
  - `Contoso.Runtime/Services/ContosoBatchWriter.cs:366-369` — catches `IOException` and ignores it ("Best effort cleanup only").
  - `Contoso.Runtime/Services/ContosoBatchWriter.cs:587-598` — catches `IOException`/`UnauthorizedAccessException`/`NotSupportedException` when setting hidden attribute.
  - `Contoso.Runtime/Services/ContosoBatchWriter.cs:614-625` — catches `IOException`/`UnauthorizedAccessException`/`JsonException` in `TryReadBatchMetadata` and returns `null`.
  - `Contoso.ChannelTypes.FileDrop/FileDropLockFiles.cs:93-102`, `:160-169`, `:214-223`, `:294-301` (`Debug.WriteLine` only), `:338-355`, `:377-381` — swallow file/JSON errors.
  - `Contoso.ChannelTypes.FileDrop/FileDropPdfValidator.cs:102-110` — catches `IOException`/`UnauthorizedAccessException` during cleanup and returns.
  - `Contoso.Web/Pages/Jobs/Index.cshtml.cs:113-120` — catches `ArgumentException` in `Path.GetFileName` and returns original path.
- Logged-but-swallowed:
  - `Contoso.ChannelTypes.FileDrop/FileDropChannelType.cs:2038-2084` — logs warnings but continues after source-archive/lock-file cleanup failures.
  - `Contoso.ChannelTypes.FileDrop/FileDropChannelType.cs:1571-1586` — logs warnings on command-lock reset failures then rethrows original via `catch { ... throw; }`.

---

### Fabrikam (.NET)

#### Custom exception types
- No domain-specific `*Exception` classes.
- Standard BCL exceptions:
  - `ArgumentException` / `ArgumentNullException` / `ArgumentOutOfRangeException` — `Fabrikam.Runtime/SearchIdentifierNormalizer.cs:10`, `:16`, `:22`, `:27`, `:34`, `:62`; `Fabrikam.Runtime/FabrikamRepository.cs:28`, `:37`, `:327-330`, `:408-410`, `:1458`, `:1470`; `Fabrikam.Runtime/FabrikamSourceSeeder.cs:13`.
  - `InvalidOperationException` — `Fabrikam.Runtime/FabrikamRepository.cs:279`, `:509`; `Fabrikam.Runtime/FabrikamJobProcessor.cs:281`, `:411`; `Fabrikam.Runtime/FabrikamReportBuilder.cs:67`; `Fabrikam.Runtime/FabrikamConnectionFactory.cs:24`, `:59`, `:76`, `:81`, `:86`, `:91`, `:107`.
  - `DbException` / `TimeoutException` / `SqlException` are caught but not thrown locally.

#### Try/catch patterns
- Specific catches:
  - `Fabrikam.Web/Pages/Index.cshtml.cs:327` — `catch (ArgumentException)` inside input validation.
  - `Fabrikam.Service/FabrikamWorker.cs:50` — `catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)`.
  - `Fabrikam.Service/FabrikamWorker.cs:54` — `catch (DbException ex)`.
  - `Fabrikam.Service/FabrikamWorker.cs:58` — `catch (TimeoutException ex)`.
  - `Fabrikam.Service/FabrikamWorker.cs:62` — `catch (InvalidOperationException ex)`.
  - `Fabrikam.Runtime/FabrikamJobProcessor.cs:103` — `catch (OperationCanceledException) when (!ct.IsCancellationRequested)`.
- Filtered broad catches:
  - `Fabrikam.Web/Program.cs:20` — `catch (Exception ex) when (IsOptionalSourceSeedingFailure(ex))`.
  - `Fabrikam.Service/FabrikamWorker.cs:96` — `catch (Exception ex) when (ex is not OperationCanceledException)`.
  - `Fabrikam.Service/FabrikamWorker.cs:138` — `catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException)`.
  - `Fabrikam.Runtime/FabrikamJobProcessor.cs:120` — `catch (Exception ex) when (ex is SqlException or InvalidOperationException or TimeoutException)`.
  - `Fabrikam.Runtime/FabrikamJobProcessor.cs:151` — `catch (Exception ex) when (ex is not OperationCanceledException)`.
- Transaction rollback catches rethrow with `throw;`:
  - `Fabrikam.Runtime/FabrikamRepository.cs:226-229`, `:296-299`, `:809-812`.
- No plain `catch (Exception)` without a filter; no `throw ex;`.

#### Global error handling
- No local middleware, exception filters, `UseExceptionHandler`, `UseDeveloperExceptionPage`, or `UseStatusCodePages`.
- Web app delegates to shared OMP defaults:
  - `Fabrikam.Web/Program.cs:14` — `app.UseOmpWebDefaults(...)`.
  - `Fabrikam.Web/Pages/Error.cshtml.cs:11` — `OmpErrorPageModelBase`.
  - `Fabrikam.Web/Pages/Status.cshtml.cs:13` — `OmpStatusPageModelBase`.
- Windows service host has no global exception handling around startup source seeding:
  - `Fabrikam.Service/Program.cs:25` — `await FabrikamSourceSeeder.ApplyConfiguredSourcesAsync(...)` with no try/catch.
- Web host guards startup seeding at `Fabrikam.Web/Program.cs:16-25`.

#### Log vs user-facing
- Background worker logs errors and persists job-level error records:
  - `Fabrikam.Service/FabrikamWorker.cs:56`, `:60`, `:64` — `_logger.LogError` for cycle-level failures.
  - `Fabrikam.Service/FabrikamWorker.cs:98` — `_logger.LogError(ex, "Fabrikam job {SearchJobId} failed.")` plus `FailJobAsync(..., ex.ToString(), ...)`.
  - `Fabrikam.Service/FabrikamWorker.cs:140` — `_logger.LogWarning` on lease-renewal failures.
- Job processor logs warnings and stores per-source/per-job error rows:
  - `Fabrikam.Runtime/FabrikamJobProcessor.cs:106`, `:122`, `:153` — `_logger.LogWarning` plus `InsertJobErrorAsync`.
- Web UI surfaces sanitized, localized messages:
  - `Fabrikam.Web/Pages/JobDetails.cshtml.cs:126-146` — `GetJobLastErrorMessage` / `GetProcessingErrorMessage`.
  - `Fabrikam.Web/Pages/JobDetails.cshtml.cs:148-167` — `GetSourceStatusMessage`.
  - `Fabrikam.Web/Pages/Index.cshtml.cs:80-83`, `:102-104`, `:121-124` — `StatusMessage`.
- Auth failures return `Forbid()` — `Fabrikam.Web/Security/FabrikamPageModel.cs:27-29`, `:42`.
- Missing resources return `NotFound()` — `Fabrikam.Web/Pages/JobDetails.cshtml.cs:68`, `:74`.
- No API `ProblemDetails`.

#### Result/Either pattern
- No `Result<T>`, `OneOf`, `Either`, `Try`, or discriminated unions.
- Closest patterns:
  - `Fabrikam.Runtime/FabrikamOptionsValidator.cs:72-74` — `ValidateOptionsResult.Success` / `Fail(failures)`.
  - `Fabrikam.Runtime/FabrikamReportBuilder.cs:12`, `:32`, `:38` — tuple return `(Reports, TotalBytes, ExceededMaxBytes)` used as a soft-cap flag.

#### Swallowed exceptions
- No empty `catch { }` blocks.
- Catches that absorb an exception and continue (intentional):
  - `Fabrikam.Service/FabrikamWorker.cs:54-65` — catches `DbException`/`TimeoutException`/`InvalidOperationException` in the main polling loop, logs, and continues polling.
  - `Fabrikam.Service/FabrikamWorker.cs:138-141` — catches DB/timeout/invalid-operation during lease renewal, logs warning, and continues.
  - `Fabrikam.Service/FabrikamWorker.cs:108-110` — catches `OperationCanceledException` from the lease-renewal task in `finally`.
  - `Fabrikam.Runtime/FabrikamJobProcessor.cs:120-135` — catches source-level SQL/timeout/invalid-operation, marks the source failed, logs warning, and continues with remaining sources.
  - `Fabrikam.Runtime/FabrikamJobProcessor.cs:151-160` — catches non-cancellation report-generation errors, logs warning, inserts a job error, and continues.
  - `Fabrikam.Web/Program.cs:20-25` — catches optional source-seeding failures, logs, and continues startup by design.
  - `Fabrikam.Web/Pages/Index.cshtml.cs:327-335` — catches `ArgumentException` during validation and converts it to a `ValidationResult`.

---

### Northwind (.NET)

#### Custom exception types
- No domain-specific `*Exception` classes.
- Standard BCL exceptions:
  - `ArgumentNullException.ThrowIfNull` — `Northwind.Runtime/FolderScanner.cs:12`, `Northwind.Web/Security/NorthwindPermissions.cs:28`, `Northwind.Runtime/NorthwindRepository.cs:704`.
  - `ArgumentException.ThrowIfNullOrWhiteSpace` — `NorthwindRepository.cs:81`, `:107`, `:108`, `:252-256`, `:351`, `:642`.
  - `InvalidOperationException` — `Northwind.Runtime/NorthwindConnectionFactory.cs:13`.
  - `ArgumentOutOfRangeException` — `NorthwindRepository.cs:639`.
  - `DataException` — `NorthwindRepository.cs:574`.
- Test-only: `SkipException` — `Northwind.Runtime.Tests/NorthwindRepositoryTierCTests.cs:677`.

#### Try/catch patterns
- `Northwind.Service/NorthwindWorker.cs:31-62` — try around each worker cycle, specific catches for `OperationCanceledException`, `DbException`, `IOException`, `UnauthorizedAccessException`, `InvalidOperationException`, `TimeoutException`, plus a broad `catch (Exception ex) when (ex is not OperationCanceledException)`.
- `Northwind.Runtime/NorthwindScanProcessor.cs:47-171` — try around the scan run; `OperationCanceledException` rethrown with `throw;`, general `Exception` logged and rethrown with `throw;`.
- `NorthwindScanProcessor.cs:66-83` — nested try for alarm-notification creation, broad `catch (Exception ex) when (ex is not OperationCanceledException)` logs warning and continues.
- `NorthwindScanProcessor.cs:194-211` — try around push-event publishing, broad catch logs warning and continues.
- `Northwind.Runtime/NorthwindRepository.cs:509-588` — try around DB transaction; bare `catch` rolls back and `throw;`.
- `Northwind.Runtime/FolderScanner.cs:23-150` — try around scan core; `OperationCanceledException` rethrown, file-system exceptions converted to `FolderCheckStatus` results.
- All rethrows use `throw;`; no `throw ex;` observed.

#### Global error handling
- `Northwind.Web/Program.cs:19` calls `app.UseOmpWebDefaults(...)` from `OpenModulePlatform.Web.Shared`; no explicit `UseExceptionHandler`, `UseDeveloperExceptionPage`, or `UseStatusCodePages`.
- `Northwind.Web/Pages/Error.cshtml` provides a static localized error page (`@L["The requested page could not be displayed."]`) with no exception details or request ID.
- `Northwind.Service/Program.cs` has no global exception handler; all service exception control lives inside `NorthwindWorker.ExecuteAsync`.

#### Log vs user-facing
- `_logger.LogError` / `LogWarning` in catches:
  - `NorthwindWorker.cs:41`, `:45`, `:49`, `:53`, `:57`, `:61`.
  - `NorthwindScanProcessor.cs:79`, `:150`, `:207`.
- `_logger.LogWarning` for model-state errors — `Northwind.Web/Pages/Targets.cshtml.cs:146`.
- `_logger.LogInformation` for normal worker/notification events.
- User-facing:
  - `TempData["StatusMessage"]` after CRUD actions in `Targets.cshtml.cs` (`:133-137`, `:241-245`).
  - `Forbid()` on authorization failures in `NorthwindPageModel.cs:35`, `:48`.
  - Static localized `Error.cshtml`.
- No `ProblemDetails` or explicit API error-response payloads.

#### Result/Either pattern
- No `Result<T>`, `OneOf`, `Either`, `Try`, or discriminated-union library usage.
- Domain status enums act as result codes: `FolderCheckStatus` and `ScanRunStatus` — `Northwind.Runtime/Models.cs:3-20`.
- Records such as `TargetScanResult` and `TargetScanPersistenceResult` (`Models.cs:113-130`) carry success/failure state but are not formal Result types.
- `Northwind.Runtime/NorthwindOptionsValidator.cs:23-25` returns `ValidateOptionsResult`.

#### Swallowed exceptions
- `Northwind.Runtime/FolderScanner.cs:262-269` — `catch (UnauthorizedAccessException) { continue; }` and `catch (IOException) { continue; }` while enumerating second-level directories. Silently skips inaccessible subdirectories.
- `FolderScanner.cs:282-285` — `catch (OverflowException) { return int.MaxValue; }` caps totals instead of failing.
- `NorthwindWorker.cs:39-62` — logs known exception types and continues the polling loop after a delay.
- `NorthwindScanProcessor.cs:77-83` and `:205-211` — log warnings and continue when alarm notifications or push-event publishing fail.
- `NorthwindRepository.cs:584-588` — bare `catch` only rolls back the transaction and rethrows; not swallowed.

---

### AdventureWorks (.NET)

#### Custom exception types
- No repo-specific `*Exception` classes.
- Standard framework exceptions:
  - `InvalidOperationException` for missing settings, missing tables, required fields, invalid identifiers — `Services/AdventureWorksSettingsService.cs:102`, `:133`; `Services/AdventureWorksSchemaCache.cs:108`; `Services/AdventureWorksImageService.cs:235`, `:246`, `:259`, `:457`; `Services/AdventureWorksFormService.cs:323`, `:328`, `:338`, `:355`, `:404`, `:414`; `Services/AdventureWorksDocumentService.cs:509`, `:546`; `Services/AdventureWorksDataStore.cs:26`, `:63`, `:68`.
  - `ArgumentNullException.ThrowIfNull(...)` — `Services/AdventureWorksDocumentService.cs:204`, `:309`.
  - `DirectoryNotFoundException` / `FileNotFoundException` when migration scripts are missing — `Services/DatabaseMigrationService.cs:33`, `:42`.

#### Try/catch patterns
- Exception filters are the dominant pattern: `catch (Exception ex) when (Is...Failure(ex))`.
  - `RazorPages/Pages/Index.cshtml.cs:320`, `:343`, `:400`, `:424`, `:494`, `:518`, `:827`.
  - `RazorPages/Pages/Settings.cshtml.cs:138`, `:160`.
  - `Controllers/DocumentViewerController.cs:103`.
  - `RazorPages/Program.cs:69`.
- Specific catches:
  - `SqlException` filtered by error number for missing user-settings table — `Services/AdventureWorksUserSettingsService.cs:53`, `:99`.
  - `JsonException` for corrupt stored settings — `Services/AdventureWorksUserSettingsService.cs:58`.
  - `ArgumentException`, `NotSupportedException`, `PathTooLongException` when normalizing image paths — `RazorPages/Infrastructure/AdventureWorksEndpointMapping.cs:37`.
- Bare `catch` blocks (always rethrow after rollback):
  - `Services/AdventureWorksFormService.cs:508`.
  - `Services/AdventureWorksDocumentService.cs:667`.
- `throw;` is used to preserve stack traces; no `throw ex;` found.

#### Global error handling
- No explicit `UseExceptionHandler`, `UseDeveloperExceptionPage`, `UseStatusCodePages`, or custom `IExceptionFilter`.
- `RazorPages/Program.cs:75` calls `app.UseOmpWebDefaults(...)`; global exception handling is delegated to the shared framework middleware.
- Startup migration failures are caught and logged at `RazorPages/Program.cs:69-72`.

#### Log vs user-facing
- Services log errors then rethrow; page handlers catch and surface localized messages:
  - `RazorPages/Pages/Settings.cshtml.cs:140-144` — `_logger.LogError` + `ModelState.AddModelError` + `L[...]`.
  - `RazorPages/Pages/Index.cshtml.cs:322-327`, `:345-347`, `:402-408`, `:426-428`, `:496-502`, `:520-522` — `_logger.LogError` + `ModelState.AddModelError` / `ShowNotice`.
- API controller returns a generic 500 JSON body — `Controllers/DocumentViewerController.cs:105-106`.
- Non-fatal conditions are logged as warnings and allowed to continue — `Services/AdventureWorksFormService.cs:376`, `:385`, `:437`, `:446`; `Services/AdventureWorksDocumentService.cs:482`, `:491`, `:555`, `:564`; `RazorPages/Pages/Index.cshtml.cs:1071`.

#### Result/Either pattern
- None found. No `Result<T>`, `OneOf`, `Either<T>`, `Try`, `ProblemDetails`, `ValidationProblem`, or error-code enums.

#### Swallowed exceptions
- No empty `catch { }` blocks.
- Log-and-continue with fallback (intentional graceful degradation):
  - `Services/AdventureWorksUserSettingsService.cs:53-57` and `:99-102` — catch missing-table `SqlException` / `JsonException`, log warning, return `null`.
  - `RazorPages/Infrastructure/AdventureWorksEndpointMapping.cs:37-41` — catch path-normalization exceptions, log warning, return `Results.NotFound()`.
  - `Controllers/DocumentViewerController.cs:103-107` — catch session-build failures, log error, return `StatusCode(500, ...)`.
- `AdventureWorksFormService.cs:508` and `AdventureWorksDocumentService.cs:667` use bare `catch` to roll back transactions and rethrow.

---

### Tailwind (.NET)

#### Custom exception types
- `UnknownPrinterDatabaseException` (custom domain exception) — `src/PrintConnect.Web/Infrastructure/Configuration/UnknownPrinterDatabaseException.cs:3`; inherits `InvalidOperationException` and exposes `DatabaseKey`. Thrown at `src/PrintConnect.Web/Infrastructure/Configuration/PrinterDatabaseCatalog.cs:30`.
- `InvalidOperationException` used for configuration/validation failures:
  - `src/PrintConnect.Web/Infrastructure/Configuration/PrinterDatabaseCatalog.cs:37` (no databases configured), `:46` (duplicate key), `:53` (invalid key), `:58` (missing DisplayName), `:63` (missing ConnectionStringName).
  - `src/PrintConnect.Web/Infrastructure/Data/SqlIdentifier.cs:11` (empty identifier), `:18` (invalid multipart identifier), `:24` (invalid characters).
  - `src/PrintConnect.Web/Infrastructure/Data/SqlConnectionFactory.cs:18` (missing connection string).
  - `src/PrintConnect.Web/Infrastructure/Zebra/JsonZebraConfigStore.cs:152` (Zebra disabled), `:157` (missing file path), `:387` (missing directory), `:393` (missing file name).
- `FileNotFoundException` / `IOException` used for Zebra JSON file problems — `JsonZebraConfigStore.cs:55`, `:122`, `:134`, `:337`, `:344`, `:350`.

#### Try/catch patterns
- Broad `catch (Exception ex)` used for cross-cutting concerns and consistently rethrows or translates:
  - `src/PrintConnect.Web/Program.cs:121` — request diagnostics, logs then `throw;`.
  - `src/PrintConnect.Web/Program.cs:181` — startup catch, writes to bootstrap log then `throw;`.
  - `src/PrintConnect.Web/Program.cs:209` — API pipeline; converts to JSON response.
  - `src/PrintConnect.Web/Application/Zebra/ZebraConfigService.cs:76`, `:104`, `:123`, `:187`, `:248`, `:275`, `:302`, `:335`, `:473` — each catches `Exception` only when `IsExpectedException` returns true, then maps to `ServiceResult` failure.
- Specific catches for known I/O races in `JsonZebraConfigStore`:
  - `src/PrintConnect.Web/Infrastructure/Zebra/JsonZebraConfigStore.cs:128` — `catch (IOException ex) when (!tempFileCreated)`.
  - `:259` — `catch (FileNotFoundException)`, `:264` — `catch (DirectoryNotFoundException)`, `:335` — `catch (FileNotFoundException ex)`.
- Specific SQL handling:
  - `src/PrintConnect.Web/Application/Printers/PrinterService.cs:46`, `:76` — `catch (SqlException ex) when (IsUserCorrectableSqlError(ex))`.
- No `throw ex;` usage found; all rethrows use `throw;`.

#### Global error handling
- Custom request diagnostics middleware — `src/PrintConnect.Web/Program.cs:97`.
- Custom `/api/*` exception wrapper middleware — `src/PrintConnect.Web/Program.cs:166`.
  - Disables status-code pages for API requests (`Program.cs:189`).
  - Rethrows client-canceled requests (`Program.cs:205`), otherwise returns JSON errors (`Program.cs:220`).
- Process-level handlers installed before startup — `src/PrintConnect.Web/Diagnostics/BootstrapDiagnostics.cs:10`:
  - `AppDomain.CurrentDomain.UnhandledException` (`:12`).
  - `TaskScheduler.UnobservedTaskException` (`:17`).
  - `AppDomain.CurrentDomain.ProcessExit` (`:22`).
- Error page:
  - `src/PrintConnect.Web/Pages/Error.cshtml:1` delegates to shared OMP `OmpError` component via `OmpErrorPageModelBase`.
  - `src/PrintConnect.Web/Pages/Status.cshtml:1` uses `OmpStatusPageModelBase`.
- No repo-owned calls to `UseExceptionHandler`, `UseDeveloperExceptionPage`, or `UseStatusCodePages`; OMP shared middleware likely supplies generic web error handling.

#### Log vs user-facing
- Request failures logged at `LogError` — `src/PrintConnect.Web/Program.cs:124`.
- Unexpected API failures logged at `LogError` — `src/PrintConnect.Web/Program.cs:215`.
- Warning for non-fatal config ambiguity — `src/PrintConnect.Web/Infrastructure/Zebra/JsonZebraConfigStore.cs:215`.
- User-facing errors:
  - `ModelState.AddModelError(string.Empty, error)` in Razor Page handlers — `src/PrintConnect.Web/Pages/Printers/Edit.cshtml.cs:90`, `src/PrintConnect.Web/Pages/PrinterConnections/Create.cshtml.cs:127`, `src/PrintConnect.Web/Pages/Zebra/Create.cshtml.cs:105`.
  - API JSON error bodies with localized messages — `Program.cs:220` (500), `Program.cs:200`/`258`/`271` (404/405).
  - Localized resource strings for all business/database/Zebra errors in `src/PrintConnect.Web/Resources/Localization.PrintConnectResource.resx` (`:95-136`).

#### Result/Either pattern
- Home-grown `ServiceResult` / `ServiceResult<T>` records — `src/PrintConnect.Web/Application/Common/ServiceResult.cs:3` and `:12`.
- Used throughout application services:
  - `src/PrintConnect.Web/Application/Printers/PrinterService.cs:30`, `:52`, `:82`.
  - `src/PrintConnect.Web/Application/PrinterConnections/PrinterConnectionService.cs:28`, `:43`, `:60`.
  - `src/PrintConnect.Web/Application/Zebra/ZebraConfigService.cs` (all public methods return `ServiceResult`/`ServiceResult<T>`).
- Errors are plain localized strings; no error-code enum, `OneOf`, or discriminated union.

#### Swallowed exceptions
- Best-effort diagnostics suppress all failures intentionally:
  - `src/PrintConnect.Web/Diagnostics/BootstrapDiagnostics.cs:41`, `:61`, `:77` — empty catch bodies; filter `ShouldSuppressBestEffortDiagnosticsFailure` returns `true` for any exception.
- Temporary-file cleanup suppression:
  - `src/PrintConnect.Web/Infrastructure/Zebra/JsonZebraConfigStore.cs:234` — `catch (Exception ex) when (ShouldSuppressTempCleanupFailure(ex))` with empty body.
- Backup-race continuation:
  - `src/PrintConnect.Web/Infrastructure/Zebra/JsonZebraConfigStore.cs:259` — `catch (FileNotFoundException)` and `:264` — `catch (DirectoryNotFoundException)` continue without backup.

---

### Globex (.NET)

#### Custom exception types
- `RegistrationWriteException` — domain-specific write error carrying an HTTP-ish `StatusCode` — `Globex.Application/Services/RegistrationWriteException.cs:3`. Thrown by `RegistrationWriteService` for validation/auth/not-found/conflict failures — `Globex.Application/Services/RegistrationWriteService.cs:79`, `:157`, `:309`, `:321`, `:342`, `:349`.
- `InvalidOperationException` used as a guard/unsupported-state exception in application layer — `Globex.Application/Services/OmpConnectionFactory.cs:17`, `SqlConnectionFactory.cs:27`/`32`, `IboSyncService.cs:343`/`468`/`479`/`484`, `DashboardRepository.cs:805`/`813`, `WorkOrderExecutor.cs:21`, `WorkOrderRepository.cs:804`/`849`.
- `TimeoutException` used for SQL app-lock acquisition failure — `Globex.Application/Services/WorkOrderRepository.cs:633`.

#### Try/catch patterns
- Specific catches first, then broader fallbacks:
  - `Globex.Backend/Services/WorkOrderBackgroundService.cs:41-60` (`OperationCanceledException`, `DbException`, `TimeoutException`, `InvalidOperationException`, then `Exception` excluding `OperationCanceledException`).
- Catch-rollback-rethrow using bare `catch`:
  - `Globex.Application/Services/RegistrationWriteService.cs:110-114`, `:165-169`, `:206-210`.
  - `Globex.Application/Services/WorkOrderRepository.cs:79-82`, `:341-344`, `:606-610`.
- Filtered fallback catch in web data service:
  - `Globex.Web/Services/GlobexDataService.cs:330-339` (`OperationCanceledException` vs `Exception` excluding `OperationCanceledException`).
- `throw;` is used correctly; no `throw ex;` found.

#### Global error handling
- No custom middleware, exception filters, `UseExceptionHandler`, `UseDeveloperExceptionPage`, or `UseStatusCodePages` inside `Globex.Web`.
- Web app delegates to OpenModulePlatform shared defaults:
  - `Globex.Web/Program.cs:13` — `AddOmpWebDefaults`.
  - `Globex.Web/Program.cs:56` — `UseOmpWebDefaults`.
- Error/status pages inherit OMP bases:
  - `Globex.Web/Pages/Error.cshtml:1` / `Error.cshtml.cs:9` (`OmpErrorPageModelBase`).
  - `Globex.Web/Pages/Status.cshtml:1` / `Status.cshtml.cs:9` (`OmpStatusPageModelBase`).
- Backend Windows Service has no global exception handler; per-loop try/catch in `WorkOrderBackgroundService` prevents crashes — `Globex.Backend/Services/WorkOrderBackgroundService.cs:30-63`.

#### Log vs user-facing
- Backend logs errors with context — `Globex.Backend/Services/WorkOrderBackgroundService.cs:47`, `:51`, `:55`, `:59`, `:128`, `:178`.
- Web data service logs warnings and returns typed responses rather than throwing — `Globex.Web/Services/GlobexDataService.cs:332`, `:337`, `:353`, `:358`, `:363`.
- User-facing messages flow through `WriteOperationResponse.Message` / `WorkOrderQueueResult.Message` and are rendered via `StatusMessage`/`ModelState`:
  - `Globex.Web/Pages/Import/Index.cshtml.cs:118`.
  - `Globex.Web/Pages/Registrations/Index.cshtml.cs:58-60`.
  - `Globex.Web/Pages/Dashboard/Reload.cshtml.cs:95` / `:131-139`.
  - `Globex.Web/Pages/Admin/Index.cshtml.cs:87-89` / `:181-187`.
- Diagnostics health endpoint returns JSON directly — `Globex.Web/Program.cs:58-81`.

#### Result/Either pattern
- Hand-rolled result records instead of `Result<T>`/`OneOf`/`Either`:
  - `WriteOperationResponse(bool Success, string? Message, int? RegistrationId)` — `Globex.Contracts/WriteOperationResponse.cs:3`.
  - `WorkOrderQueueResult(...)` with `WorkOrderQueueOutcome` enum — `Globex.Application/Models/WorkOrderQueueResult.cs:3`, `WorkOrderQueueOutcome.cs:3`.
- Used consistently across the web-to-application boundary to avoid exceptions for expected failures.

#### Swallowed exceptions
- `GlobexDataService` swallows non-cancellation exceptions and returns fallback values (`null`, empty list, default response):
  - `Globex.Web/Services/GlobexDataService.cs:335-339` (reads) and `:361-365` (writes). Risk: database/auth/config failures silently degrade pages without surfacing severity to the user.
- `WorkOrderBackgroundService` swallows `DbException`, `TimeoutException`, `InvalidOperationException`, and unexpected exceptions, then retries after the poll delay:
  - `Globex.Backend/Services/WorkOrderBackgroundService.cs:45-60`. Risk: persistent failures are only visible in logs; retry can continue indefinitely.
- Heartbeat cancellation is intentionally ignored:
  - `Globex.Backend/Services/WorkOrderBackgroundService.cs:150-153` (`catch (OperationCanceledException) { /* expected */ }`).
- Username resolver falls back to claim name on SQL/IO failures:
  - `Globex.Web/Services/GlobexUserNameResolver.cs:38-46`. Degraded identity resolution is logged as warning.

---

### ODVGateway (.NET)

#### Custom exception types
- `SourcePackPayloadTooLargeException` (inherits `InvalidOperationException`) — `src/ODVGateway/Program.cs:1341`.
- `InvalidDataException` for empty/malformed prep payload — the external viewer prep-payload reader (see the ODVGateway repository documentation).
- `InvalidDataException` for empty decoded sessiondata — the external viewer session-data decoder.
- `InvalidOperationException` for startup `TrustedSourceRoots` validation failures — `src/ODVGateway/Program.cs:1248`, `:1252`.

#### Try/catch patterns
- All catches are specific; no `catch (Exception)` or bare `catch { }` found.
- `catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)` distinguishes timeout from caller cancellation — `src/ODVGateway/Program.cs:795`, `:1131`.
- Specific IO/path exception handling (`FileNotFoundException`, `DirectoryNotFoundException`, `IOException`, `UnauthorizedAccessException`, `ArgumentException`, `NotSupportedException`, `HttpRequestException`, `JsonException`, `FormatException`) is used throughout.
- No `throw;` or `throw ex;` patterns found; exceptions are either wrapped in custom exceptions or converted to result payloads.

#### Global error handling
- No global exception middleware registered: no `UseExceptionHandler`, `UseDeveloperExceptionPage`, `UseStatusCodePages`, `IExceptionHandler`, or exception filters.
- Only custom middleware at `src/ODVGateway/Program.cs:76-86` adds security response headers.
- Unhandled exceptions fall through to ASP.NET Core's default behavior.

#### Log vs user-facing
- `_logger.LogError` for configuration/dist errors:
  - `src/ODVGateway/Services/OpenDocViewerDistResolver.cs:34`, `:41`.
  - `src/ODVGateway/Services/DirectSourceFileResolver.cs:47`.
- `_logger.LogWarning` inside catch blocks and for operational issues:
  - `src/ODVGateway/Program.cs:478`, `:484`, `:813`, `:1149`.
  - `src/ODVGateway/Services/OpenDocViewerIndexRenderer.cs:107`.
- User-facing error responses use `Results.BadRequest(...)`, `Results.NotFound(...)`, `Results.Json(..., statusCode: ...)` and `GatewayHtml.StatusPage(...)`.
- Examples: capacity rejection returns 429 with message — `src/ODVGateway/Program.cs:214-220`; source-pack frame failure surfaces error in stream — `src/ODVGateway/Program.cs:699-703`; proxy size limit returns 502 — `src/ODVGateway/Program.cs:1085-1090`.

#### Result/Either pattern
- Lightweight result records:
  - `GatewaySessionStoreResult` — `src/ODVGateway/Services/GatewaySessionStore.cs:237`.
  - `HandoffGuardResult` — the external viewer handoff guard.
  - The fallback URL result record — the external viewer fallback URL builder.
  - `SourcePackPayload` record with `Ok`/`Error`/`Bytes`/`ContentStream` — `src/ODVGateway/Program.cs:1349`.
- No `OneOf`, `FluentResults`, `Either`, `Try<T>`, or discriminated-union libraries.

#### Swallowed exceptions
- `DirectSourceFileResolver` swallows `ArgumentException`, `IOException`, `NotSupportedException`, and `UnauthorizedAccessException` during path normalization and file probing, returning `false`/`null`:
  - `src/ODVGateway/Services/DirectSourceFileResolver.cs:79-94`, `:133-148`, `:211-226`. Risk: `UnauthorizedAccessException` silently hides real permission problems.
- `GatewaySessionStore.FileTicket` swallows `ArgumentException`/`NotSupportedException` in path/extension helpers:
  - `src/ODVGateway/Services/GatewaySessionStore.cs:318-325`, `:334-341`, `:362-369`. Risk: low; defensive parsing of malformed tickets.
- `OpenDocViewerIndexRenderer` catches IO exceptions and returns an HTML status page with logging — `src/ODVGateway/Services/OpenDocViewerIndexRenderer.cs:48-63` (converted to user-facing response, not silently swallowed).
- `Program.cs` source-pack loop catches HTTP/IO exceptions and emits per-frame failure payloads — `src/ODVGateway/Program.cs:596-619` (surfaced inside the stream).

---

### OpenDocViewer (JS/npm)

#### Custom exception types
- No custom `extends Error` subclasses. Factory functions attach discriminator properties to plain `Error` objects:
  - `src/workers/imageWorker.js:44` — `createFallbackMainThreadError` sets `error.fallbackMainThread = true`.
  - `src/workers/pdfPageWorker.js:39` — sets `fallbackMainThread`, `code`, and `phase`.
  - `src/utils/pdfPageWorkerPool.js:114` — `createUnavailableError` builds an error with `fallbackMainThread: true` plus worker-history details.
  - `src/components/DocumentLoader/documentLoaderUtils.js:129`, `:137`, `:148` — create `TimeoutError` / `DocumentFetchTooLargeError` / HTTP errors with `code`, `status`, `isHttpError`.
  - `src/components/DocumentLoader/DocumentLoader.js:682`, `:710`, `:726`, `:742` — create `createPrefetchHttpError`, `createSourceUnavailableSessionError`, `createPrefetchTimeoutError`, `createInvalidSourcePayloadError`.
  - `src/utils/printPdf.js:248` and `src/utils/renderDecodeBenchmark.js:293` — create abort-style errors with `name = 'AbortError'`.
- Plain invariant `throw new Error(...)` is common; some wraps preserve the original cause — `src/utils/printPdf.js:864`, `:1652`, `:2087` (`{ cause: error }`).

#### Try/catch patterns
- JavaScript has no typed `catch`, so all catches are generic (`catch (error)` / `catch (e)`). A few inspect properties to emulate specific handling:
  - `src/workers/imageWorker.js:404` — checks `error?.fallbackMainThread` before rethrowing.
  - `src/utils/pageAssetRenderer.js:680` — checks `error?.fallbackMainThread`.
  - `src/components/DocumentLoader/documentLoaderUtils.js:250` — checks `error?.code === 'document-fetch-timeout'`.
- Stack-preserving rethrow with `throw error;`:
  - `src/workers/imageWorker.js:409`
  - `src/workers/pdfPageWorker.js:238`
  - `src/utils/pageAssetRenderer.js:539`
  - `src/components/DocumentLoader/documentLoaderUtils.js:254`
- No stack-destroying `throw ex;` pattern observed.

#### Global error handling
- React Error Boundary — `src/ErrorBoundary.jsx:130` class component; `componentDidCatch` at `:152` logs full message/stack/componentStack via `systemLogger.error`; UI renders a localized fallback with reload/reset/copy-details buttons (`:252-289`). Stack traces are gated by the `exposeStackTraces` runtime flag (`:211`).
- Express servers have final 4-argument error handlers:
  - `server/system-log-server.js:217`
  - `server/user-log-server.js:251`
  Both write to `error-YYYY-MM-DD.log` and return `500` with a short text message.
- No global browser handlers: no `window.onerror`, `window.addEventListener('error')`, or `unhandledrejection` listeners in `src/index.jsx` or the app shell. Errors outside React boundaries fall through to the browser default.
- Worker-scope `onerror` handlers route failures:
  - `src/components/DocumentLoader/batchHandler.js:124`
  - `src/utils/pdfWorkerDispatcher.js:260`
  - `src/utils/pdfPageWorkerPool.js:89`
  - `src/utils/pageAssetWorkerPool.js:106`

#### Log vs user-facing
- `src/logging/systemLogger.js:377` — `log()` writes to console and, when configured, POSTs to the backend. Backend send failures are surfaced via `console.error` (`:394`, `:449`) and non-retryable HTTP codes (`401/403/404`) disable backend logging (`:438`).
- `src/ErrorBoundary.jsx:154` logs the crash to the backend but shows a generic user-facing message unless `exposeStackTraces` is enabled.
- User-facing session-level failure UI — `src/components/ViewerProblemNotice.jsx:215-244` displays localized title/message, optional technical details, and reload/reset/dismiss actions.
- Soft failures are warned but not surfaced as blocking UI:
  - `src/integrations/sessionUrl.js:109` — catches fetch errors, logs a `console.warn`, and returns `null`.
- Server error responses: JSON `{ error: '...' }` for auth/rate-limit/content-type errors; plain text `Internal Server Error` / `Failed to write log` for 5xx. No ProblemDetails-style envelope.

#### Result/Either pattern
- No `Result<T>`, `OneOf`, `Either`, `Try`, or `neverthrow` usage.
- Manual `{ ok: boolean, ... }` result objects are used throughout:
  - `src/utils/printParse.js:58`, `:97-98` — returns `{ ok: false, error }` or `{ ok: true, sequence }`.
  - `src/components/DocumentToolbar/usePrintRangeDialog.js:291`, `:294`, `:300` — returns `{ ok: false, msg }` or `{ ok: true, ... }`.
  - `src/app/bootConfig.js:50`, `:53`, `:66-67`, `:105-110` — returns `{ ok: true, url }` / `{ ok: false, src }`.
  - `src/schemas/portableBundle.js:311` — returns `{ ok: errors.length === 0, errors, version }`.

#### Swallowed exceptions
- Empty/defensive `try { ... } catch {}` is pervasive, especially around cleanup, disposal, timers, and worker termination. Representative:
  - `src/workers/imageWorker.js:147` (`bitmap.close()`)
  - `src/workers/pdfPageWorker.js:140` (`loadingTask.destroy()`)
  - `src/utils/pageAssetRenderer.js:65`, `:83-84`, `:146`, `:155`, `:173`, `:397`, `:401`, `:409-410`, `:414`, `:445-446`
  - `src/contexts/ViewerProvider.jsx:690`, `:949-950`, `:1050`
  - `src/PerformanceMonitor.jsx:522-525`
  - `src/ErrorBoundary.jsx:179` (`catch { /* ignore */ }`)
  - `src/schemas/portableBundle.js:115` (`catch { /* ignore */ }`)
- Catches that only log and continue (masking the real failure):
  - `src/components/DocumentLoader/DocumentLoader.js:378` — logs file-type detection failure and falls back to MIME/extension.
  - `src/utils/printPdf.js` — many `logger.debug(...)` catches that swallow cleanup/render failures.
  - `src/logging/userLogger.js:230`, `:295` — silently drops `fetch` failures with `.catch(() => debug('...failed (silent)'))`.
- Risk: the volume of silent catches makes operational degradation hard to detect; while justified for cleanup, some suppress config/storage/worker signals that could indicate real problems.

---

### AgentDocMap (JS/npm)

#### Custom exception types
- No domain-specific `*Exception` classes. The repo throws plain `Error` instances for argument/JSDoc/output-guard failures.
- `throw new Error('JSDoc is not installed...')` — `src/lib/jsdocDoclets.js:8`.
- `throw new Error('Unknown argument: ${arg}')` — `src/cli.js:54`.
- `throw new Error('Unknown command: ${command}')` / `Missing required option...` / `--source-metadata must be...` — `src/cli.js:69`, `73`, `77`, `81`.
- `throw new Error('Refusing to clean...')` — `src/lib/outputGuard.js:74`, `78`.
- `throw new Error('JSDoc failed with exit code ${result.status}...')` — `src/lib/jsdocDoclets.js:30`.

#### Try/catch patterns
- `catch { }` (bare, catches any error) is common for filesystem/git probing:
  - `src/lib/fsUtils.js:8-15`
  - `src/lib/fileInventory.js:79-84`
  - `src/lib/gitInfo.js:23-28`
- `catch (error) { ... throw new Error(...) }` wraps/replaces the original error:
  - `src/lib/jsdocDoclets.js:33-37` wraps `JSON.parse` failure.
- `catch (error) { result.parseError = ...; return result; }` converts parse failure to data instead of throwing:
  - `src/lib/sourceAnalyzer.js:65-75`
- `catch (error) { throw error; }` preserves original error in cleanup helper:
  - `test/testUtils.js:19-24`

#### Global error handling
- Only CLI-level handler — `src/cli.js:89-92`:
  - `main().catch((error) => { console.error(...); process.exitCode = 1; });`
- No middleware, exception filters, `UseExceptionHandler`, `UseDeveloperExceptionPage`, status-code pages, React error boundaries, etc. (the project is a Node CLI, not a server/React app).

#### Log vs user-facing
- User-facing CLI error output:
  - `console.error(error instanceof Error ? error.message : String(error));` — `src/cli.js:90`.
- Cleanup warning in test helper:
  - `console.warn('Failed to remove temporary test directory...')` — `test/testUtils.js:30-31`.
- No structured logger (`_logger.LogError`), no API `ProblemDetails`, no error-response DTOs. JSDoc/process errors are surfaced as thrown `Error` messages or captured as `parseError` strings in the generated map (`src/lib/sourceAnalyzer.js:73`).

#### Result/Either pattern
- None observed. No `Result<T>`, `OneOf`, error-code enums, discriminated unions, `Try`, or `Either`. Functions either return values directly or throw plain `Error`s.

#### Swallowed exceptions
- `pathExists` treats any `fs.access` failure as "does not exist" — `src/lib/fsUtils.js:8-15`. Risk: permission/IO errors are silently ignored.
- `walk` silently skips directories it cannot read — `src/lib/fileInventory.js:79-84`. Risk: unreadable source directories are treated as empty.
- `git()` silently returns `null` on any git failure — `src/lib/gitInfo.js:23-28`. Risk: missing/outdated git metadata is indistinguishable from a real error.
- `withTempDir` cleanup logs (but does not throw) when the callback already failed — `test/testUtils.js:26-36`. Risk: temporary directories may be left behind; intentional trade-off to preserve the original test failure.

---

## 2. Comparison Table + Divergences

| Repository | Custom exceptions | Catch pattern | Global handling | Log vs user-facing | Result/Either pattern | Notable swallowed exceptions |
|---|---|---|---|---|---|---|
| **OpenModulePlatform** | Sparse domain exceptions (`HtmlContentFileException`, `ServerReportException`); heavy BCL use | Specific typed catches + filtered `catch (Exception) when (...)`; `throw;` only | Shared `UseOmpWebDefaults` pipeline (`UseExceptionHandler`, `UseStatusCodePages`, `/error`, `/status`). `OpenModulePlatform.Auth` omits it | `LogError` in catches; Razor `ModelState` + localized messages; minimal-API `Results.BadRequest` | Hand-rolled result records (`UniversalPackageImportResult`, `AppDeploymentResult`) + status enums | Several log-and-continue loops in worker/scheduler code |
| **Contoso** | None; BCL only (`InvalidOperationException`, `IOException`, `JsonException`) | Mostly specific; filtered broad catches in worker/channel; `throw;` only | Delegated to `UseOmpWebDefaults`; no explicit middleware | `LogError`/`LogWarning`; Razor `FeedbackError`/`FeedbackMessage` via `[TempData]` | `ChannelConfigValidationResult` (IsValid/Errors); RPC response DTOs | Many best-effort empty/comment catches in lock-file and batch-cleanup paths |
| **Fabrikam** | None; BCL only | Specific first, filtered broad second; `throw;` only | Delegated to `UseOmpWebDefaults`; service host has no startup guard | Worker persists errors to DB; web UI shows localized status messages; `Forbid()`/`NotFound()` | `ValidateOptionsResult`; tuple soft-cap flags | Worker loops swallow DB/timeout/invalid-operation and continue; service startup seeding unguarded |
| **Northwind** | None; BCL only | Specific + filtered broad; `throw;` only | Delegated to `UseOmpWebDefaults`; service host has no global handler | `LogError`/`LogWarning`; `TempData["StatusMessage"]`, static `Error.cshtml`, `Forbid()` | Domain status enums (`FolderCheckStatus`, `ScanRunStatus`) + result records | `FolderScanner` silently skips `UnauthorizedAccessException`/`IOException` subdirectories; notification/push failures logged and continued |
| **AdventureWorks** | None; BCL only | Exception-filter helpers (`Is...Failure`); bare `catch` for rollback; `throw;` only | Delegated to `UseOmpWebDefaults` | Services log + rethrow; pages catch and use `ModelState.AddModelError` / `ShowNotice`; generic 500 JSON | None | Missing-table / corrupt-user-settings caught and returned as `null`; path normalization returns `NotFound` |
| **Tailwind** | One domain exception (`UnknownPrinterDatabaseException`) | Broad but filtered catches in Zebra/API pipeline; specific SQL/IO catches; `throw;` only | Custom API exception wrapper middleware + bootstrap diagnostics + OMP shared error pages | `LogError` for request/API failures; `ModelState.AddModelError`; localized API JSON | Home-grown `ServiceResult` / `ServiceResult<T>` | Bootstrap diagnostics suppress all failures; temp-cleanup and backup-race catches suppressed |
| **Globex** | One domain exception (`RegistrationWriteException`) | Specific + filtered broad; `throw;` only | Delegated to `UseOmpWebDefaults`; backend service has no global handler | Backend `LogError`; web data service returns typed responses; `StatusMessage`/`ModelState` | `WriteOperationResponse`, `WorkOrderQueueResult` + enum | `GlobexDataService` swallows non-cancellation exceptions and returns defaults; backend retry loop swallows and continues indefinitely |
| **ODVGateway** | One custom exception (`SourcePackPayloadTooLargeException`); `InvalidDataException` for prep/sessiondata | All specific; no `catch (Exception)`; exceptions wrapped or converted to results | **None** — no `UseExceptionHandler`, `UseStatusCodePages`, or exception filters | `LogError`/`LogWarning`; endpoint-local `Results.BadRequest`/`NotFound`/`Json` + HTML status pages | Lightweight result records (`GatewaySessionStoreResult`, `HandoffGuardResult`, `SourcePackPayload`) | `DirectSourceFileResolver` swallows `UnauthorizedAccessException`/IO during probing; `GatewaySessionStore` defensive parsing catches |
| **OpenDocViewer** | No `extends Error`; domain error factories with discriminator properties | All catches generic (`catch (error)`); some inspect `error.code`; `throw error;` preserved | React Error Boundary + Express final error handlers; **no** `window.onerror` / `unhandledrejection` | `systemLogger.error` POSTs to backend; `ErrorBoundary` shows localized fallback; `ViewerProblemNotice` for session failures | Manual `{ ok: boolean }` objects (`printParse`, `bootConfig`, `portableBundle`) | Pervasive empty/defensive `catch {}` for cleanup/disposal/timers/workers; `userLogger` silently drops fetch failures |
| **AgentDocMap** | None; plain `Error` throws | Bare `catch { }` for optional I/O; wrap/replace in JSON parse | CLI-level `main().catch(...)` only | `console.error` to stderr; `parseError` field in output map | None | `pathExists`, `walk`, `git()` treat any failure as non-existent/null; `withTempDir` cleanup logs but does not throw |

### Key divergences

1. **Missing global exception handling**
   - `OpenModulePlatform.Auth` does not call `UseOmpWebDefaults` and has no explicit `UseExceptionHandler` / `UseStatusCodePages`.
   - `ODVGateway` has no global exception middleware at all; unhandled exceptions fall back to ASP.NET Core defaults.
   - Several backend Windows services (`Fabrikam.Service`, `Northwind.Service`, `Globex.Backend`) have no global startup exception handler; all resilience is per-loop inside the worker.

2. **Heavy reliance on swallowed / log-and-continue exceptions**
   - `OpenModulePlatform` worker loops (`WorkerManagerHostedService`, `MaintenanceScanScheduler`) swallow expected failures after logging.
   - `Contoso` has numerous intentional best-effort empty catches in file/lock cleanup.
   - `Fabrikam` worker and job processor swallow DB/timeout/invalid-operation and continue.
   - `Northwind` silently skips inaccessible directories during enumeration.
   - `Globex` `GlobexDataService` returns default values after swallowing non-cancellation exceptions.
   - `ODVGateway` `DirectSourceFileResolver` swallows `UnauthorizedAccessException`, masking real permission issues.
   - `OpenDocViewer` has pervasive empty `catch {}` around cleanup and worker termination.
   - `AgentDocMap` treats all I/O/git failures as benign null/false results.

3. **Inconsistent result abstractions**
   - `Tailwind` uses a consistent home-grown `ServiceResult<T>`.
   - `Globex` uses hand-rolled `WriteOperationResponse` / `WorkOrderQueueResult`.
   - `OpenModulePlatform`, `Contoso`, `ODVGateway` use ad-hoc result records for specific flows.
   - `Fabrikam`, `Northwind`, `AdventureWorks`, `AgentDocMap` have no formal result abstraction.

4. **Inconsistent custom exception usage**
   - `OpenModulePlatform` has a few domain exceptions but mostly throws BCL types.
   - `Tailwind` and `Globex` each have one meaningful domain exception.
   - `ODVGateway` defines one custom exception for payload-too-large.
   - Most other repos use only BCL exceptions.

5. **JS ecosystem lacks global browser error coverage**
   - `OpenDocViewer` has a React Error Boundary and Express error handlers but no `window.onerror` / `unhandledrejection` listener for non-React main-thread errors.
   - `AgentDocMap` is a CLI and only has a top-level `main().catch(...)`.

---

## 3. Recommended Standard Pattern

### .NET ecosystem

**Recommended pattern:** shared OMP exception-handling pipeline + specific catches + structured logging + typed problem-details responses.

1. **Global pipeline (already implemented in `OpenModulePlatform.Web.Shared`)**
   - Use `UseOmpWebDefaults` (or equivalent) in every ASP.NET Core host.
   - Ensure `UseExceptionHandler("/Error")`, `UseStatusCodePagesWithReExecute("/status/{0}")`, and the shared `OmpErrorPageModelBase` / `OmpStatusPageModelBase` pages are wired.
   - **Action:** `OpenModulePlatform.Auth` should adopt the same shared pipeline. `ODVGateway` should add a minimal global exception handler/status-code middleware or explicitly opt into a shared variant.

2. **Catch specificity**
   - Prefer specific typed catches (`SqlException`, `IOException`, `OperationCanceledException`, `TimeoutException`).
   - Use filtered broad catches (`catch (Exception ex) when (IsExpected...Failure(ex))`) only for top-level resilience boundaries (worker loops, background services).
   - Always use `throw;` to preserve stack traces. Never use `throw ex;`.

3. **Structured logging**
   - Use `_logger.LogError(exception, "...")` inside catch blocks so the logging provider (NLog, per `logging.md`) captures the full exception object.
   - Include contextual identifiers (job id, search id, channel id, etc.) in log messages.

4. **User-facing responses**
   - Razor Pages: use `ModelState.AddModelError` with localized strings; do not expose raw `ex.Message` unless it is known-safe.
   - API endpoints: return `ProblemDetails` (or the shared `Results.Problem(...)`) with a stable error code and a request correlation id.
   - Background services: persist failure state to the database so operators can see it in UI; do not rely only on logs.

5. **Result/Either for expected failures**
   - For expected business failures (validation, not-found, conflict), prefer a small, consistent `Result<T>` / `ServiceResult<T>` record over throwing exceptions.
   - Keep exceptions for exceptional / unexpected conditions.

6. **Do not swallow silently**
   - Empty `catch { }` should be rare and documented.
   - If an exception is intentionally swallowed, log at least a `LogDebug`/`LogWarning` with the reason, except in tight cleanup paths where allocation matters.
   - Never swallow `UnauthorizedAccessException` or authentication/authorization failures without explicit, logged intent.

### JS/npm ecosystem

**Recommended pattern:** global error listeners + React error boundaries + try/catch with explicit user-facing error states + structured logging.

1. **Global browser coverage**
   - Register `window.onerror` and `window.addEventListener('unhandledrejection', ...)` in the app shell to capture errors outside React boundaries.
   - Forward captured errors to `systemLogger` with context (URL, user agent, runtime version).

2. **React Error Boundaries**
   - Keep `ErrorBoundary.jsx` as the last line of defense for render-phase errors.
   - Provide a clear localized fallback, a reload/reset action, and an optional "copy details" button gated by `exposeStackTraces`.

3. **Try/catch discipline**
   - Avoid bare `catch {}`. At minimum log the failure at `debug`/`warn` level.
   - Use `throw error;` to preserve stacks; wrap only when adding context with `{ cause: error }`.
   - Reserve silent catches for idempotent cleanup that cannot affect correctness (closing already-closed resources, worker termination).

4. **User-facing error states**
   - For recoverable session failures, render a component like `ViewerProblemNotice` with a localized title/message and actionable buttons.
   - Never show raw exception messages or stack traces to end users unless explicitly enabled by configuration.

5. **Result objects for expected failures**
   - Standardize on a small `{ ok: boolean, error?: ... }` result type for business-logic boundaries.
   - Avoid mixing thrown errors and result objects inconsistently within the same module.

6. **Server logging**
   - Keep Express final error handlers; return a short safe message to clients and write full details to rotating log files.

---

## 4. Migration Notes per Diverging Repo

### OpenModulePlatform
- **Current state:** Mature shared pipeline for most web apps; typed/filtered catches; structured logging; ad-hoc result records; several production log-and-continue catches.
- **Migration:** Add the shared `UseOmpWebDefaults` pipeline to `OpenModulePlatform.Auth`. Review worker/scheduler log-and-continue catches (`WorkerManagerHostedService`, `MaintenanceScanScheduler`, `WorkerProcessHostedService`) and convert the most operationally significant ones to persisted health-state or circuit-breaker behavior instead of relying only on logs.
- **Priority:** Medium.

### Contoso
- **Current state:** No custom exceptions; mostly specific catches; many intentional best-effort swallowed catches in file/lock cleanup.
- **Migration:** Keep cleanup suppression but add at least a `LogDebug` reason in every empty catch. Adopt the shared OMP web pipeline explicitly if not already fully wired. Consider a lightweight `Result<T>` for config validation instead of `ChannelConfigValidationResult`.
- **Priority:** Low.

### Fabrikam
- **Current state:** No custom exceptions; good filtered catches; worker swallows DB/timeout/invalid-operation; service startup seeding unguarded.
- **Migration:** Add a try/catch around `FabrikamSourceSeeder.ApplyConfiguredSourcesAsync` in `Fabrikam.Service/Program.cs`. Promote swallowed worker-loop errors to persisted job-state with severity so the UI can surface them. Consider a small `Result<T>` for report-builder soft-cap logic.
- **Priority:** Medium.

### Northwind
- **Current state:** No custom exceptions; disciplined per-component handling; `FolderScanner` silently skips inaccessible directories; no service global handler.
- **Migration:** Add a top-level try/catch in `Northwind.Service/Program.cs` for startup failures. Make `FolderScanner` directory-skip behavior observable (increment a counter or log a warning) so permission problems are detectable.
- **Priority:** Low.

### AdventureWorks
- **Current state:** No custom exceptions; exception-filter helpers; services log + rethrow; pages surface localized messages; no result abstraction.
- **Migration:** Introduce a small `Result<T>`/`OperationResult` for the existing `Is...Failure` filter flows to reduce control-flow by exception. Keep shared OMP web pipeline.
- **Priority:** Low.

### Tailwind
- **Current state:** Custom `ServiceResult<T>`; custom API exception wrapper; bootstrap diagnostics; broad but filtered catches; good localization.
- **Migration:** Ensure the custom API pipeline and bootstrap diagnostics route into the shared OMP `UseOmpWebDefaults` pipeline consistently (or document why it does not). Reduce the number of broad `catch (Exception)` in `ZebraConfigService` by introducing more specific typed filters where possible.
- **Priority:** Low.

### Globex
- **Current state:** One custom exception; hand-rolled result records; backend worker swallows and retries indefinitely; `GlobexDataService` swallows and returns defaults.
- **Migration:** Add a circuit-breaker / backoff and health-state persistence to `WorkOrderBackgroundService` so persistent failures are not only in logs. Change `GlobexDataService` to return a `Result<T>` instead of default values, so callers can decide whether to degrade or show an error.
- **Priority:** Medium.

### ODVGateway
- **Current state:** No global exception middleware; endpoint-local error responses; lightweight result records; `DirectSourceFileResolver` swallows `UnauthorizedAccessException`.
- **Migration:** Add `UseExceptionHandler` + `UseStatusCodePages` (or a shared variant) so unhandled exceptions produce consistent JSON/HTML responses. Stop swallowing `UnauthorizedAccessException` in `DirectSourceFileResolver`; treat it as an explicit failure and log at warning/error level.
- **Priority:** High.

### OpenDocViewer
- **Current state:** Good React Error Boundary and Express handlers; pervasive empty/defensive catches; no global `window.onerror` / `unhandledrejection` coverage.
- **Migration:** Add global browser error/rejection listeners in the app shell that forward to `systemLogger`. Audit empty catches and add minimal logging unless they are provably harmless cleanup. Standardize the ad-hoc `{ ok }` result objects into a shared result helper.
- **Priority:** Medium.

### AgentDocMap
- **Current state:** Simple CLI; plain `Error` throws; bare `catch { }` for optional I/O; no structured logging or result abstraction.
- **Migration:** Keep simplicity, but distinguish "file does not exist" from permission/IO errors in `fsUtils.js` and `fileInventory.js`. Add a minimal `Result<T>` or `{ ok, error }` return type for `sourceAnalyzer.js` instead of mutating a `parseError` field.
- **Priority:** Low.
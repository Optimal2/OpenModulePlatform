namespace OpenModulePlatform.Installation;

// Database boundary of the existing guarded executor, substituted only by native offline tests.
internal sealed record ModuleDefinitionSqlRepairOperations(
    Func<Task> AcquireLock,
    Func<IEnumerable<PortableModuleDefinitionSqlScript>, Task<bool>> HasUnexecuted,
    Func<string, Task<ModuleDefinitionValidationResult>> Validate,
    Func<PortableModuleDefinitionSqlScript, string, Task<long>> Start,
    Func<string, Task> PlatformMigration,
    Func<string, Task> Execute,
    Func<long, string, string?, Task> Complete);

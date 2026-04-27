-- File: OpenModulePlatform.Web.ExampleServiceAppModule/sql/2-initialize-example-serviceapp.sql
/*
Seeds default values and OMP registration rows for the example Service App module.

Prerequisites:
- Run ../../sql/1-setup-openmoduleplatform.sql
- Run ../../sql/2-initialize-openmoduleplatform.sql
- Run 1-setup-example-serviceapp.sql
*/
USE [OpenModulePlatform];
GO

IF NOT EXISTS (SELECT 1 FROM omp_example_serviceapp.Configurations WHERE VersionNo = 0)
BEGIN
    /*
    scanBatchSize is intentionally seeded as 1 for the canonical example dataset.
    This keeps the sample worker behavior deterministic and easy to observe during local evaluation,
    screenshots, demos and troubleshooting. It is not a production recommendation.
    Increase this value in the service configuration for real environments after validation.
    */
    INSERT INTO omp_example_serviceapp.Configurations(VersionNo, ConfigJson, Comment, CreatedBy)
    VALUES(0, N'{"scanBatchSize": 1, "sampleMode": true}', N'Initial example service configuration', N'install-script');
END
GO

DECLARE @InstanceId uniqueidentifier;
DECLARE @InstanceTemplateId int;
DECLARE @SampleHostId uniqueidentifier;
DECLARE @SampleTemplateHostId int;
DECLARE @PortalAdminsRoleId int;
DECLARE @ServiceModuleId int;
DECLARE @ServiceModuleInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111301';
DECLARE @ServiceTemplateModuleInstanceId int;
DECLARE @ServiceWebAppId int;
DECLARE @ServiceWebAppInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111302';
DECLARE @ServiceAppId int;
DECLARE @ServiceAppInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111303';
DECLARE @ServiceViewPermissionId int;
DECLARE @ServiceAdminPermissionId int;
DECLARE @InitialServiceConfigId int;
DECLARE @ServiceArtifactId int;

SELECT @InstanceId = InstanceId, @InstanceTemplateId = InstanceTemplateId
FROM omp.Instances
WHERE InstanceKey = N'default';

IF @InstanceId IS NULL
    THROW 50000, 'Default OMP instance not found. Run the core SQL setup/init scripts first.', 1;

SELECT @PortalAdminsRoleId = RoleId FROM omp.Roles WHERE Name = N'PortalAdmins';
SELECT @SampleHostId = HostId FROM omp.Hosts WHERE InstanceId = @InstanceId AND HostKey = N'sample-host';
SELECT @SampleTemplateHostId = InstanceTemplateHostId
FROM omp.InstanceTemplateHosts
WHERE InstanceTemplateId = @InstanceTemplateId
  AND HostKey = N'sample-host';
SELECT TOP (1) @InitialServiceConfigId = ConfigId
FROM omp_example_serviceapp.Configurations
WHERE VersionNo = 0
ORDER BY ConfigId DESC;

-- Example ServiceApp registration
-------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'ExampleServiceAppModule.View')
    INSERT INTO omp.Permissions(Name, Description) VALUES(N'ExampleServiceAppModule.View', N'Read access to the Example ServiceApp');

IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'ExampleServiceAppModule.Admin')
    INSERT INTO omp.Permissions(Name, Description)
    VALUES(
        N'ExampleServiceAppModule.Admin',
        N'Administrative access to the Example ServiceApp');

SELECT @ServiceViewPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'ExampleServiceAppModule.View';
SELECT @ServiceAdminPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'ExampleServiceAppModule.Admin';

IF EXISTS (SELECT 1 FROM omp.Modules WHERE ModuleKey = N'example_serviceapp')
BEGIN
    UPDATE omp.Modules
    SET DisplayName = N'Example ServiceApp',
        ModuleType = N'HostAppModule',
        SchemaName = N'omp_example_serviceapp',
        Description = N'Combined web app and service app example for OpenModulePlatform',
        IsEnabled = 1,
        SortOrder = 400,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleKey = N'example_serviceapp';
END
ELSE
BEGIN
    INSERT INTO omp.Modules(
        ModuleKey,
        DisplayName,
        ModuleType,
        SchemaName,
        Description,
        IsEnabled,
        SortOrder)
    VALUES(
        N'example_serviceapp',
        N'Example ServiceApp',
        N'HostAppModule',
        N'omp_example_serviceapp',
        N'Combined web app and service app example for OpenModulePlatform',
        1,
        400);
END

SELECT @ServiceModuleId = ModuleId FROM omp.Modules WHERE ModuleKey = N'example_serviceapp';

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @ServiceModuleId AND AppKey = N'example_serviceapp_webapp')
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'Example ServiceApp',
        AppType = N'WebApp',
        Description = N'Web app definition for the example HostAppModule',
        IsEnabled = 1,
        SortOrder = 400,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @ServiceModuleId AND AppKey = N'example_serviceapp_webapp';
END
ELSE
BEGIN
    INSERT INTO omp.Apps(ModuleId, AppKey, DisplayName, AppType, Description, IsEnabled, SortOrder)
    VALUES(
        @ServiceModuleId,
        N'example_serviceapp_webapp',
        N'Example ServiceApp',
        N'WebApp',
        N'Web app definition for the example HostAppModule',
        1,
        400);
END

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @ServiceModuleId AND AppKey = N'example_serviceapp_service')
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'Example Service Worker',
        AppType = N'ServiceApp',
        Description = N'Service app definition for the example HostAppModule',
        IsEnabled = 1,
        SortOrder = 401,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @ServiceModuleId AND AppKey = N'example_serviceapp_service';
END
ELSE
BEGIN
    INSERT INTO omp.Apps(ModuleId, AppKey, DisplayName, AppType, Description, IsEnabled, SortOrder)
    VALUES(
        @ServiceModuleId,
        N'example_serviceapp_service',
        N'Example Service Worker',
        N'ServiceApp',
        N'Service app definition for the example HostAppModule',
        1,
        401);
END

SELECT @ServiceWebAppId = AppId FROM omp.Apps WHERE ModuleId = @ServiceModuleId AND AppKey = N'example_serviceapp_webapp';
SELECT @ServiceAppId = AppId FROM omp.Apps WHERE ModuleId = @ServiceModuleId AND AppKey = N'example_serviceapp_service';

IF NOT EXISTS (SELECT 1 FROM omp.AppPermissions WHERE AppId = @ServiceWebAppId AND PermissionId = @ServiceViewPermissionId)
    INSERT INTO omp.AppPermissions(AppId, PermissionId, RequireAll) VALUES(@ServiceWebAppId, @ServiceViewPermissionId, 0);

IF @PortalAdminsRoleId IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM omp.RolePermissions
       WHERE RoleId = @PortalAdminsRoleId
         AND PermissionId = @ServiceViewPermissionId
   )
    INSERT INTO omp.RolePermissions(RoleId, PermissionId)
    VALUES(@PortalAdminsRoleId, @ServiceViewPermissionId);

IF @PortalAdminsRoleId IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM omp.RolePermissions
       WHERE RoleId = @PortalAdminsRoleId
         AND PermissionId = @ServiceAdminPermissionId
   )
    INSERT INTO omp.RolePermissions(RoleId, PermissionId)
    VALUES(@PortalAdminsRoleId, @ServiceAdminPermissionId);

IF NOT EXISTS
(
    SELECT 1
    FROM omp.Artifacts
    WHERE AppId = @ServiceAppId
      AND Version = N'1.0.0'
      AND PackageType = N'folder'
      AND TargetName = N'win-x64'
)
    INSERT INTO omp.Artifacts(AppId, Version, PackageType, TargetName, RelativePath, IsEnabled)
    VALUES(@ServiceAppId, N'1.0.0', N'folder', N'win-x64', N'publish/ExampleServiceAppModule', 1);

/*
Resolve the artifact explicitly instead of assuming that the highest ArtifactId is the correct sample artifact.
The example install script seeds a canonical win-x64 folder artifact for version 1.0.0 and should keep resolving that exact row on repeated installs.
*/
SELECT @ServiceArtifactId = ArtifactId
FROM omp.Artifacts
WHERE AppId = @ServiceAppId
  AND Version = N'1.0.0'
  AND PackageType = N'folder'
  AND TargetName = N'win-x64';

IF NOT EXISTS (SELECT 1 FROM omp.ModuleInstances WHERE ModuleInstanceId = @ServiceModuleInstanceId)
BEGIN
    INSERT INTO omp.ModuleInstances(
        ModuleInstanceId,
        InstanceId,
        ModuleId,
        ModuleInstanceKey,
        DisplayName,
        Description,
        IsEnabled,
        SortOrder)
    VALUES(
        @ServiceModuleInstanceId,
        @InstanceId,
        @ServiceModuleId,
        N'example_serviceapp',
        N'Example ServiceApp',
        N'Example module instance with both web and service apps',
        1,
        400);
END
ELSE
BEGIN
    UPDATE omp.ModuleInstances
    SET InstanceId = @InstanceId,
        ModuleId = @ServiceModuleId,
        ModuleInstanceKey = N'example_serviceapp',
        DisplayName = N'Example ServiceApp',
        Description = N'Example module instance with both web and service apps',
        IsEnabled = 1,
        SortOrder = 400,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleInstanceId = @ServiceModuleInstanceId;
END

IF NOT EXISTS
(
    SELECT 1
    FROM omp.InstanceTemplateModuleInstances
    WHERE InstanceTemplateId = @InstanceTemplateId
      AND ModuleInstanceKey = N'example_serviceapp'
)
BEGIN
    INSERT INTO omp.InstanceTemplateModuleInstances(
        InstanceTemplateId,
        ModuleId,
        ModuleInstanceKey,
        DisplayName,
        Description,
        SortOrder)
    VALUES(
        @InstanceTemplateId,
        @ServiceModuleId,
        N'example_serviceapp',
        N'Example ServiceApp',
        N'Example module instance with both web and service apps',
        400);
END

SELECT @ServiceTemplateModuleInstanceId = InstanceTemplateModuleInstanceId
FROM omp.InstanceTemplateModuleInstances
WHERE InstanceTemplateId = @InstanceTemplateId
  AND ModuleInstanceKey = N'example_serviceapp';

IF NOT EXISTS (SELECT 1 FROM omp.AppInstances WHERE AppInstanceId = @ServiceWebAppInstanceId)
BEGIN
    INSERT INTO omp.AppInstances(
        AppInstanceId,
        ModuleInstanceId,
        HostId,
        AppId,
        AppInstanceKey,
        DisplayName,
        Description,
        RoutePath,
        InstallationName,
        IsEnabled,
        IsAllowed,
        DesiredState,
        SortOrder)
    VALUES(
        @ServiceWebAppInstanceId,
        @ServiceModuleInstanceId,
        @SampleHostId,
        @ServiceWebAppId,
        N'example_serviceapp_webapp',
        N'Example ServiceApp',
        N'Primary web app instance for the example HostAppModule',
        N'ExampleServiceAppModule',
        N'webapp',
        1,
        1,
        1,
        400);
END
ELSE
BEGIN
    UPDATE omp.AppInstances
    SET ModuleInstanceId = @ServiceModuleInstanceId,
        HostId = @SampleHostId,
        AppId = @ServiceWebAppId,
        AppInstanceKey = N'example_serviceapp_webapp',
        DisplayName = N'Example ServiceApp',
        Description = N'Primary web app instance for the example HostAppModule',
        RoutePath = N'ExampleServiceAppModule',
        InstallationName = N'webapp',
        IsEnabled = 1,
        IsAllowed = 1,
        DesiredState = 1,
        SortOrder = 400,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE AppInstanceId = @ServiceWebAppInstanceId;
END

IF NOT EXISTS
(
    SELECT 1
    FROM omp.InstanceTemplateAppInstances
    WHERE InstanceTemplateModuleInstanceId = @ServiceTemplateModuleInstanceId
      AND AppInstanceKey = N'example_serviceapp_webapp'
)
BEGIN
    INSERT INTO omp.InstanceTemplateAppInstances(
        InstanceTemplateModuleInstanceId,
        InstanceTemplateHostId,
        AppId,
        AppInstanceKey,
        DisplayName,
        Description,
        RoutePath,
        InstallationName,
        DesiredState,
        SortOrder)
    VALUES(
        @ServiceTemplateModuleInstanceId,
        @SampleTemplateHostId,
        @ServiceWebAppId,
        N'example_serviceapp_webapp',
        N'Example ServiceApp',
        N'Primary web app instance for the example HostAppModule',
        N'ExampleServiceAppModule',
        N'webapp',
        1,
        400);
END

IF NOT EXISTS (SELECT 1 FROM omp.AppInstances WHERE AppInstanceId = @ServiceAppInstanceId)
BEGIN
    INSERT INTO omp.AppInstances(
        AppInstanceId,
        ModuleInstanceId,
        HostId,
        AppId,
        AppInstanceKey,
        DisplayName,
        Description,
        InstallPath,
        InstallationName,
        ArtifactId,
        ConfigId,
        ExpectedLogin,
        ExpectedClientHostName,
        ExpectedClientIp,
        IsEnabled,
        IsAllowed,
        DesiredState,
        SortOrder)
    VALUES(
        @ServiceAppInstanceId,
        @ServiceModuleInstanceId,
        @SampleHostId,
        @ServiceAppId,
        N'example_serviceapp_service',
        N'Example Service Worker',
        N'Primary service app instance for the example HostAppModule',
        N'C:\Program Files\OpenModulePlatform\ServiceApps\ExampleServiceAppModule',
        N'default',
        @ServiceArtifactId,
        @InitialServiceConfigId,
        /*
        Example identity placeholders below are descriptive on purpose so the expected format is obvious.
        Replace them with the real service account, host name and client IP before starting the worker.
        */
        N'DOMAIN\\ServiceAccountName',
        N'hostname.example.com',
        N'192.168.1.100',
        1,
        1,
        1,
        401);
END
ELSE
BEGIN
    UPDATE omp.AppInstances
    SET ModuleInstanceId = @ServiceModuleInstanceId,
        HostId = @SampleHostId,
        AppId = @ServiceAppId,
        AppInstanceKey = N'example_serviceapp_service',
        DisplayName = N'Example Service Worker',
        Description = N'Primary service app instance for the example HostAppModule',
        InstallPath = N'C:\Program Files\OpenModulePlatform\ServiceApps\ExampleServiceAppModule',
        InstallationName = N'default',
        ArtifactId = @ServiceArtifactId,
        ConfigId = @InitialServiceConfigId,
        ExpectedLogin = N'DOMAIN\\ServiceAccountName',
        ExpectedClientHostName = N'hostname.example.com',
        ExpectedClientIp = N'192.168.1.100',
        IsEnabled = 1,
        IsAllowed = 1,
        DesiredState = 1,
        SortOrder = 401,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE AppInstanceId = @ServiceAppInstanceId;
END

IF NOT EXISTS
(
    SELECT 1
    FROM omp.InstanceTemplateAppInstances
    WHERE InstanceTemplateModuleInstanceId = @ServiceTemplateModuleInstanceId
      AND AppInstanceKey = N'example_serviceapp_service'
)
BEGIN
    INSERT INTO omp.InstanceTemplateAppInstances(
        InstanceTemplateModuleInstanceId,
        InstanceTemplateHostId,
        AppId,
        AppInstanceKey,
        DisplayName,
        Description,
        InstallPath,
        InstallationName,
        DesiredArtifactId,
        DesiredConfigId,
        ExpectedLogin,
        ExpectedClientHostName,
        ExpectedClientIp,
        DesiredState,
        SortOrder)
    VALUES(
        @ServiceTemplateModuleInstanceId,
        @SampleTemplateHostId,
        @ServiceAppId,
        N'example_serviceapp_service',
        N'Example Service Worker',
        N'Primary service app instance for the example HostAppModule',
        N'C:\Program Files\OpenModulePlatform\ServiceApps\ExampleServiceAppModule',
        N'default',
        @ServiceArtifactId,
        @InitialServiceConfigId,
        N'DOMAIN\\ServiceAccountName',
        N'hostname.example.com',
        N'192.168.1.100',
        1,
        401);
END


-------------------------------------------------------------------------------
-- Seed sample jobs for the service-backed example
-------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM omp_example_serviceapp.Jobs)
BEGIN
    INSERT INTO omp_example_serviceapp.Jobs(RequestType, PayloadJson, Status, RequestedUtc, RequestedBy)
    VALUES
        (N'sample.run', N'{"message":"hello from OMP"}', 0, SYSUTCDATETIME(), N'install-script'),
        (N'sample.run', N'{"message":"second sample job"}', 0, SYSUTCDATETIME(), N'install-script');
END
GO


-------------------------------------------------------------------------------
GO

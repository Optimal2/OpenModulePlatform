-- File: OpenModulePlatform.Portal/sql/3-sync-omp-portal-entries.sql
/*
Synchronizes Portal entry rows from the currently registered OMP app instances.

HostAgent-first packages initialize several modules after the Portal schema has
already been created. Keeping this as a separate, idempotent final step ensures
that every enabled Portal/WebApp app instance is visible in the Portal menu
without requiring each module script to know about Portal internals.
*/
USE [OpenModulePlatform];
GO

;WITH app_entries AS
(
    SELECT N'app:' + LOWER(REPLACE(CONVERT(nvarchar(36), ai.AppInstanceId), N'-', N'')) + N':home' AS entry_key,
           ai.DisplayName AS display_name,
           ai.Description AS description,
           N'app:' + LOWER(REPLACE(CONVERT(nvarchar(36), ai.AppInstanceId), N'-', N'')) + N':home' AS target_entry_key,
           ai.AppInstanceId AS source_app_instance_id,
           ai.SortOrder AS default_sort_order
    FROM omp.AppInstances ai
    INNER JOIN omp.Apps a ON a.AppId = ai.AppId
    WHERE ai.IsEnabled = 1
      AND ai.IsAllowed = 1
      AND a.AppType IN (N'Portal', N'WebApp')
)
MERGE omp_portal.portal_entries AS target
USING app_entries AS source
    ON target.entry_key = source.entry_key
WHEN MATCHED THEN
    UPDATE SET display_name = source.display_name,
               description = source.description,
               target_entry_key = source.target_entry_key,
               source_app_instance_id = source.source_app_instance_id,
               default_sort_order = source.default_sort_order,
               updated_at = SYSUTCDATETIME()
WHEN NOT MATCHED BY TARGET THEN
    INSERT(entry_key, display_name, description, target_entry_key, source_app_instance_id, is_enabled, default_sort_order)
    VALUES(source.entry_key, source.display_name, source.description, source.target_entry_key, source.source_app_instance_id, 1, source.default_sort_order);
GO

;WITH child_entry_definitions AS
(
    SELECT *
    FROM (VALUES
        (N'omp_portal', N'portal:admin-overview', N'Admin overview', N'Portal administration overview.', N'/admin/overview', 10),
        (N'omp_portal', N'portal:admin-portal-entries', N'Portal entries', N'Manage global Portal Entries.', N'/admin/portalentries', 20),
        (N'omp_portal', N'portal:admin-users', N'Users', N'Manage OMP users and auth links.', N'/admin/users', 30),
        (N'omp_portal', N'portal:admin-security', N'Security', N'Manage roles and permissions.', N'/admin/security', 40),
        (N'omp_portal', N'portal:admin-config-settings', N'Config settings', N'Manage OMP configuration settings.', N'/admin/configsettings', 50),

        (N'content_webapp_webapp', N'content:admin', N'Admin', N'Manage content pages.', N'/content/admin', 10),
        (N'content_webapp_webapp', N'content:create', N'Create page', N'Create a new content page.', N'/content/admin/create', 20),

        (N'example_webapp_webapp', N'example-webapp:configurations', N'Configurations', N'Manage example web app configurations.', N'/ExampleWebAppModule/configurations', 10),
        (N'example_webapp_webapp', N'example-webapp:opendocviewer-demo', N'OpenDocViewer demo', N'Open the OpenDocViewer integration demo.', N'/ExampleWebAppModule/opendocviewer-demo', 20),

        (N'example_webapp_blazor_webapp', N'example-webapp-blazor:configurations', N'Configurations', N'Manage example Blazor app configurations.', N'/ExampleWebAppBlazorModule/configurations', 10),
        (N'example_webapp_blazor_webapp', N'example-webapp-blazor:opendocviewer-demo', N'OpenDocViewer demo', N'Open the OpenDocViewer integration demo.', N'/ExampleWebAppBlazorModule/opendocviewer-demo', 20),

        (N'example_serviceapp_webapp', N'example-serviceapp:app-instances', N'App instances', N'Manage service app instances.', N'/ExampleServiceAppModule/appinstances', 10),
        (N'example_serviceapp_webapp', N'example-serviceapp:configurations', N'Configurations', N'Manage service app configurations.', N'/ExampleServiceAppModule/configurations', 20),
        (N'example_serviceapp_webapp', N'example-serviceapp:jobs', N'Jobs', N'Queue and inspect service app jobs.', N'/ExampleServiceAppModule/jobs', 30),
        (N'example_serviceapp_webapp', N'example-serviceapp:opendocviewer-demo', N'OpenDocViewer demo', N'Open the OpenDocViewer integration demo.', N'/ExampleServiceAppModule/opendocviewer-demo', 40),

        (N'example_workerapp_webapp', N'example-workerapp:app-instances', N'App instances', N'Manage worker app instances.', N'/ExampleWorkerAppModule/appinstances', 10),
        (N'example_workerapp_webapp', N'example-workerapp:configurations', N'Configurations', N'Manage worker app configurations.', N'/ExampleWorkerAppModule/configurations', 20),
        (N'example_workerapp_webapp', N'example-workerapp:jobs', N'Jobs', N'Queue and inspect worker app jobs.', N'/ExampleWorkerAppModule/jobs', 30),
        (N'example_workerapp_webapp', N'example-workerapp:opendocviewer-demo', N'OpenDocViewer demo', N'Open the OpenDocViewer integration demo.', N'/ExampleWorkerAppModule/opendocviewer-demo', 40)
    ) AS entries(parent_app_instance_key, entry_key, display_name, description, target_url, default_sort_order)
),
child_entries AS
(
    SELECT parent.portal_entry_id AS parent_entry_id,
           child.entry_key,
           child.display_name,
           child.description,
           child.target_url,
           child.default_sort_order
    FROM child_entry_definitions child
    INNER JOIN omp.AppInstances ai ON ai.AppInstanceKey = child.parent_app_instance_key
    INNER JOIN omp_portal.portal_entries parent
        ON parent.source_app_instance_id = ai.AppInstanceId
       AND parent.parent_entry_id IS NULL
    WHERE ai.IsEnabled = 1
      AND ai.IsAllowed = 1
)
MERGE omp_portal.portal_entries AS target
USING child_entries AS source
    ON target.entry_key = source.entry_key
WHEN MATCHED THEN
    UPDATE SET parent_entry_id = source.parent_entry_id,
               display_name = source.display_name,
               description = source.description,
               target_url = source.target_url,
               target_entry_key = NULL,
               source_app_instance_id = NULL,
               default_sort_order = source.default_sort_order,
               updated_at = SYSUTCDATETIME()
WHEN NOT MATCHED BY TARGET THEN
    INSERT(entry_key, parent_entry_id, display_name, description, target_url, is_enabled, default_sort_order)
    VALUES(source.entry_key, source.parent_entry_id, source.display_name, source.description, source.target_url, 1, source.default_sort_order);
GO

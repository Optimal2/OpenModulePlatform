-- File: OpenModulePlatform.Portal/sql/4-ensure-topbar-hover-user-setting.sql
/*
Seeds Portal user setting definitions that may be needed after a HostAgent-only
Portal artifact update.

This targeted patch exists for installations where the Portal artifact has been
updated through HostAgent artifact import without rerunning the Portal setup
SQL. The full setup script already contains the same definition.

Prerequisite:
- Run ../../sql/1-setup-openmoduleplatform.sql
- Run 1-setup-omp-portal.sql at least once
*/
USE [OpenModulePlatform];
GO

IF OBJECT_ID(N'omp_portal.user_setting_definitions', N'U') IS NULL
BEGIN
    THROW 52012, 'omp_portal.user_setting_definitions is missing. Run OpenModulePlatform.Portal/sql/1-setup-omp-portal.sql before this patch.', 1;
END
GO

MERGE omp_portal.user_setting_definitions AS target
USING
(
    SELECT N'Portal' AS setting_category,
           N'TopbarDropdownsOpenOnHover' AS setting_name,
           CAST(1 AS tinyint) AS value_kind,
           CAST(1 AS int) AS default_int_value,
           CAST(NULL AS nvarchar(max)) AS default_string_value,
           N'Controls whether top bar dropdown menus open on hover for the signed-in user.' AS description,
           20 AS sort_order,
           CAST(1 AS bit) AS is_enabled
    UNION ALL
    SELECT N'Portal' AS setting_category,
           N'ShowPortalNavbar' AS setting_name,
           CAST(1 AS tinyint) AS value_kind,
           CAST(1 AS int) AS default_int_value,
           CAST(NULL AS nvarchar(max)) AS default_string_value,
           N'Controls whether the Portal navbar below the topbar is shown for the signed-in user.' AS description,
           30 AS sort_order,
           CAST(1 AS bit) AS is_enabled
) AS source
ON target.setting_category = source.setting_category
AND target.setting_name = source.setting_name
WHEN MATCHED THEN
    UPDATE SET value_kind = source.value_kind,
               default_int_value = source.default_int_value,
               default_string_value = source.default_string_value,
               description = source.description,
               sort_order = source.sort_order,
               is_enabled = source.is_enabled,
               updated_at = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(setting_category, setting_name, value_kind, default_int_value, default_string_value, description, sort_order, is_enabled)
    VALUES(source.setting_category, source.setting_name, source.value_kind, source.default_int_value, source.default_string_value, source.description, source.sort_order, source.is_enabled);
GO

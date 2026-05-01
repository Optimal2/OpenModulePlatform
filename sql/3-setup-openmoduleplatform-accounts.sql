-- File: sql/3-setup-openmoduleplatform-accounts.sql
/*
OpenModulePlatform accounts setup script.

Creates the first account/user model for OMP core. The model separates the
stable internal OMP user from authentication-provider specific identities.

Design notes:
- omp.users is the internal account/user table used by OMP.
- omp.user_auth links an internal user to one or more authentication provider
  identities.
- omp.auth_providers stores configured authentication providers and allows a
  provider to be disabled from the database.
- omp.auth_provider_lpwd stores local password login identities. It is provider
  specific data and is intentionally not stored on omp.users.
- omp.config_settings stores instance-wide or scoped OMP configuration values.

Prerequisite:
- Run 1-setup-openmoduleplatform.sql first so the omp schema and RBAC tables
  exist.

This script creates only objects under the omp schema.
*/
USE [OpenModulePlatform];
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'omp')
    EXEC('CREATE SCHEMA [omp]');
GO

-------------------------------------------------------------------------------
-- Accounts
-------------------------------------------------------------------------------
IF OBJECT_ID(N'omp.users', N'U') IS NULL
BEGIN
    CREATE TABLE omp.users
    (
        user_id int IDENTITY(1,1) NOT NULL,

        -- User-facing name. This is intentionally not unique and should be used
        -- together with user_id in administrative screens when users need to be
        -- distinguished from each other.
        display_name nvarchar(200) NOT NULL,

        -- Integer status instead of physical deletion. Suggested initial values:
        -- 1 = active, 2 = disabled, 3 = deleted/reserved. The application owns
        -- the final enum mapping.
        account_status int NOT NULL CONSTRAINT DF_omp_users_account_status DEFAULT(1),

        -- Last successful login/authentication resolve for this OMP user. This
        -- is intended for support/admin troubleshooting, not online presence.
        last_login_at datetime2(3) NULL,

        created_at datetime2(3) NOT NULL CONSTRAINT DF_omp_users_created_at DEFAULT SYSUTCDATETIME(),
        updated_at datetime2(3) NOT NULL CONSTRAINT DF_omp_users_updated_at DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_omp_users PRIMARY KEY(user_id)
    );
END
GO

-------------------------------------------------------------------------------
-- Authentication providers
-------------------------------------------------------------------------------
IF OBJECT_ID(N'omp.auth_providers', N'U') IS NULL
BEGIN
    CREATE TABLE omp.auth_providers
    (
        provider_id int IDENTITY(1,1) NOT NULL,

        -- Human-readable provider name shown in administration and diagnostics.
        -- Provider-specific code decides how each provider is handled.
        display_name nvarchar(200) NOT NULL,

        -- Allows an authentication provider to be disabled from the database
        -- without deleting provider metadata or existing account links.
        is_enabled bit NOT NULL CONSTRAINT DF_omp_auth_providers_is_enabled DEFAULT(1),

        updated_at datetime2(3) NOT NULL CONSTRAINT DF_omp_auth_providers_updated_at DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_omp_auth_providers PRIMARY KEY(provider_id),
        CONSTRAINT UQ_omp_auth_providers_display_name UNIQUE(display_name)
    );
END
GO

-------------------------------------------------------------------------------
-- User-to-authentication mapping
-------------------------------------------------------------------------------
IF OBJECT_ID(N'omp.user_auth', N'U') IS NULL
BEGIN
    CREATE TABLE omp.user_auth
    (
        user_id int NOT NULL,
        provider_id int NOT NULL,

        -- Provider-specific user key. For example:
        -- - AD/Windows provider: DOMAIN\user
        -- - Local password provider: local login user name
        -- The provider implementation owns the exact value format.
        provider_user_key nvarchar(256) NOT NULL,

        -- Last time this linked provider identity was successfully used.
        last_used_at datetime2(3) NULL,
        created_at datetime2(3) NOT NULL CONSTRAINT DF_omp_user_auth_created_at DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_omp_user_auth PRIMARY KEY(user_id, provider_id, provider_user_key),
        CONSTRAINT FK_omp_user_auth_user FOREIGN KEY(user_id) REFERENCES omp.users(user_id),
        CONSTRAINT FK_omp_user_auth_provider FOREIGN KEY(provider_id) REFERENCES omp.auth_providers(provider_id)
    );
END
GO

-------------------------------------------------------------------------------
-- Local password authentication provider data
-------------------------------------------------------------------------------
IF OBJECT_ID(N'omp.auth_provider_lpwd', N'U') IS NULL
BEGIN
    CREATE TABLE omp.auth_provider_lpwd
    (
        -- Login name for the local password provider. This is provider-specific
        -- auth data, not the internal OMP user identity.
        user_name nvarchar(256) NOT NULL,

        -- Stores only a password hash. Raw passwords must never be stored here.
        password_hash nvarchar(1000) NOT NULL,

        CONSTRAINT PK_omp_auth_provider_lpwd PRIMARY KEY(user_name)
    );
END
GO

-------------------------------------------------------------------------------
-- OMP configuration settings
-------------------------------------------------------------------------------
IF OBJECT_ID(N'omp.config_settings', N'U') IS NULL
BEGIN
    CREATE TABLE omp.config_settings
    (
        config_setting_id int IDENTITY(1,1) NOT NULL,

        -- Logical settings category, for example: general, authentication,
        -- security, portal-defaults.
        category nvarchar(100) NOT NULL,

        -- Setting key within the category, for example: allow_password_login.
        setting nvarchar(200) NOT NULL,

        -- Stored as text to allow simple scalar values such as true/false,
        -- numbers, names, or serialized values when required by future settings.
        value nvarchar(max) NULL,

        -- Optional scope. NULL means instance-wide/default setting. A scoped row
        -- may target a specific role or user when the setting model needs that.
        role_id int NULL,
        user_id int NULL,

        CONSTRAINT PK_omp_config_settings PRIMARY KEY(config_setting_id),
        CONSTRAINT FK_omp_config_settings_role FOREIGN KEY(role_id) REFERENCES omp.Roles(RoleId),
        CONSTRAINT FK_omp_config_settings_user FOREIGN KEY(user_id) REFERENCES omp.users(user_id),
        CONSTRAINT UQ_omp_config_settings_scope UNIQUE(category, setting, role_id, user_id)
    );
END
GO

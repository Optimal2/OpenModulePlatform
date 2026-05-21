@{
    EnvironmentName = 'local-dev'

    # Source mode builds from repositories before installation.
    # Package mode installs from an already expanded package folder.
    DeploymentMode = 'Source'
    RepositoryRoot = ''
    OpenDocViewerRoot = ''
    PackageRoot = ''
    PublicBaseUrl = 'http://localhost:8088/'

    Version = '0.3.3'
    Configuration = 'Release'

    RuntimeRoot = 'C:\OMP'
    WebRoot = 'C:\OMP\Sites'
    WebAppsRoot = 'C:\OMP\WebApps'
    ServicesRoot = 'C:\OMP\Services'
    ArtifactStoreRoot = 'C:\OMP\ArtifactStore'
    ArtifactCacheRoot = 'C:\OMP\ArtifactCache'
    # Use a shared folder for every IIS node in a load-balanced environment.
    DataProtectionKeyPath = 'C:\OMP\DataProtectionKeys'

    SqlServer = 'localhost'
    Database = 'OpenModulePlatform'
    SqlAuthentication = 'Integrated' # Integrated or SqlLogin.
    SqlUser = ''
    SqlPassword = ''

    BootstrapPortalAdminPrincipals = @('DOMAIN\UserOrGroup')
    BootstrapPortalAdminPrincipalType = 'ADUser' # ADUser or ADGroup. Legacy User is normalized to ADUser.

    # Instance-wide config settings stored in omp.config_settings after SQL setup.
    # Category/Setting must exist in omp.config_setting_definitions.
    ConfigSettings = @(
        @{
            Category = 'branding'
            Setting = 'platformName'
            Value = 'OMP'
        },
        @{
            Category = 'branding'
            Setting = 'portalName'
            Value = 'Portal'
        }
    )

    RunAsUser = ''
    RunAsPassword = ''

    # Leave HostKey empty when Hosts contains this machine; the installer then
    # resolves the host profile from COMPUTERNAME or the short FQDN prefix.
    HostKey = ''
    HostName = ''
    Hosts = @(
        @{
            HostKey = 'sample-host'
            DisplayName = 'Sample host'
            CertificateThumbprint = ''
            CertificateSerialNumber = ''
            SortOrder = 10
        }
    )

    Portal = @{
        Title = 'OpenModulePlatform'
        DefaultCulture = 'sv-SE'
        SupportedCultures = @('sv-SE', 'en-US')
        AllowAnonymous = $false
        UseForwardedHeaders = $false
        # When UseForwardedHeaders is true behind a load balancer, prefer listing
        # the trusted proxy IPs or CIDR networks. TrustAll is only for isolated
        # deployments where all traffic is guaranteed to pass through the proxy.
        ForwardedHeadersTrustAllProxies = $false
        ForwardedHeadersKnownProxies = @()
        ForwardedHeadersKnownNetworks = @()
        PermissionMode = 'Any'
        # Leave empty to use PublicBaseUrl, or set '/' for a root-relative portal.
        PortalBaseUrl = '/'
    }

    ContentWebApp = @{
        AppInstanceId = '11111111-1111-1111-1111-111111111232'
        HomeSlug = 'home'
        # Content reads from these paths inside the IIS app. Keep App_Data for
        # HostAgent-managed deployments so runtime content stays outside the
        # immutable app artifact.
        ServerReportsPath = 'App_Data/ContentReports'
        HtmlFilesPath = 'App_Data/ContentPages'
        # Optional shared source folders mirrored by HostAgent to the local
        # App_Data paths above on every server. Use these for multi-server
        # deployments where editors maintain HTML/JSON files on a shared disk.
        SharedServerReportsPath = ''
        SharedHtmlFilesPath = ''
        AllowedServerReportDatabases = @('OpenModulePlatform')
        ServerReportDefaultMaxRows = 100
        ServerReportMaxRowsLimit = 1000
        ServerReportQueryTimeoutSeconds = 30
        # Optional package-relative folder for installer-seeded Content pages
        # and server report JSON definitions. Leave the default value to import
        # content-webapp-seed when that folder is included in a package.
        SeedPath = 'content-webapp-seed'
    }

    OpenDocViewer = @{
        DisplayName = 'OpenDocViewer'
        # Leave empty to read OpenDocViewer\package.json during packaging or
        # manifest.json during package installation. Set explicitly only when a
        # prebuilt ODV payload should be registered with a known external version.
        Version = ''
        # Optional deployment-owned odv.site.config.js source file. When empty,
        # the HostAgent-first package includes a neutral site config file that
        # can later be edited through OMP artifact configuration files.
        SiteConfigPath = ''
    }

    Iis = @{
        SiteName = 'OpenModulePlatform'
        Protocol = 'http'
        Port = 8088
        HostHeader = ''
        CertificateThumbprint = ''
        CertificateSerialNumber = ''
        RemoveOtherBindings = $false
        PortalPhysicalPath = ''
        OpenDocViewerAppPath = 'opendocviewer'
        ContentWebAppPath = 'content'
        AppPools = @{
            Portal = 'OMP_Portal'
            Auth = 'OMP_Auth'
            OpenDocViewer = 'OMP_OpenDocViewer'
            ContentWebApp = 'OMP_ContentWebAppModule'
            ExampleWebApp = 'OMP_ExampleWebAppModule'
            ExampleWebAppBlazor = 'OMP_ExampleWebAppBlazorModule'
            ExampleServiceWebApp = 'OMP_ExampleServiceAppModule'
            ExampleWorkerWebApp = 'OMP_ExampleWorkerAppModule'
            IFrameWebApp = 'OMP_iFrameWebAppModule'
        }
    }

    Services = @{
        HostAgent = 'OpenModulePlatform.HostAgent'
        WorkerManager = 'OpenModulePlatform.WorkerManager'
        ExampleService = 'OpenModulePlatform.Service.ExampleServiceAppModule'
    }

    HostAgent = @{
        # Disabled by default. Local lab installs can enable this so dropping a
        # valid artifact zip in the import folder registers it and selects it as
        # the desired version automatically.
        ArtifactZipImportEnabled = $false
        ArtifactZipImportPath = 'C:\OMP\ArtifactImports'
        ArtifactZipImportProcessedPath = ''
        ArtifactZipImportFailedPath = ''

        # Optional per-web-app IIS app-pool identities. Keys are matched against
        # app instance key, route path, or final app-pool name.
        # Entries not listed here use RunAsUser/RunAsPassword.
        IisAppPoolOverrides = @{
            # 'some_web_app' = @{
            #     UserName = 'DOMAIN\DedicatedWebAccount'
            #     Password = ''
            # }
        }
    }

    HostAgentFirst = @{
        # Full uninstall removes the HostAgent service and any services found
        # below ServicesRoot automatically. Keep explicit names here for older
        # service names that may no longer point at the configured runtime root.
        AdditionalServiceNamesToRemove = @(
            'OpenModulePlatform.WorkerManager',
            'OpenModulePlatform.Service.ExampleServiceAppModule'
        )

        AdditionalSqlFiles = @()

        # Optional module definition JSON files from customer/module repositories.
        # The source file should live at the module root and be listed in that
        # repository's omp-components.json. These extra files are copied to the
        # package-local module-definitions import folder and imported by the
        # bootstrapper after SQL initialization.
        AdditionalModuleDefinitionFiles = @()

        # Optional prebuilt artifact zip files from external module repositories.
        # Source is resolved relative to this config file. Payload is the package
        # relative path used by bootstrap.local.sample.json. Target is the
        # ArtifactStore relative folder where the extracted artifact is copied.
        # These artifacts are also copied to available-artifacts so they remain
        # available for later Portal-driven module installation.
        AdditionalArtifactFiles = @(
            # @{
            #     Source = '..\SomeModule\artifacts\SomeModule.Web.zip'
            #     Payload = 'payload\some_module__some_app__web-app__some-target__1.0.0.zip'
            #     Target = 'some-module/web/1.0.0'
            # }
        )

        # Optional folders containing standard OMP artifact package zips for
        # modules that should be available in the package but not necessarily
        # installed during the initial bootstrap. RuntimeRoot\ArtifactArchive is
        # also scanned automatically when it exists, as are artifacts folders
        # below configured DeveloperSource repository roots.
        AvailableArtifactArchiveRoots = @()
    }

    Package = @{
        OutputRoot = 'artifacts\suite-release'
        KeepStaging = $false
        SkipRestore = $false
        SkipOpenDocViewerBuild = $false
        SkipOpenDocViewerNpmInstall = $false
        # Optional path to a prebuilt OpenDocViewer zip. If it is a legacy dist
        # zip, the package script wraps it in the standard OMP artifact package
        # envelope together with OpenDocViewer.SiteConfigPath. If it is already
        # an OMP artifact package, it is copied as-is.
        OpenDocViewerPackageZip = ''
        # Set this to true only for protected/customer-specific packages where
        # the package should include the active install config next to the
        # installer as omp-suite.local.psd1.
        IncludeInstallConfig = $false
        InstallConfigFileName = 'omp-suite.local.psd1'
    }

    Options = @{
        InstallOpenDocViewer = $true
        InstallContentWebApp = $true
        InstallIFrameWebApp = $true
        InstallExamples = $true
        InstallRuntimeServices = $true
        InstallExampleService = $true
        ConfigureIis = $true
        RunSql = $true
        # Services are installed/configured for Automatic startup, but operators
        # usually start them manually after validating a deployment.
        StartServices = $false
        # Production/customer databases should normally be created and secured by a DBA.
        CreateDatabase = $false
        GrantRunAsDatabaseAccess = $false
        RemoveIis = $true
        RemoveServices = $true
        RemoveFiles = $true
        RemoveDatabaseObjects = $false
        DropSchemas = $false
    }

    DatabaseSchemas = @(
        'omp_opendocviewer',
        'omp_content',
        'omp_iframe',
        'omp_example_workerapp',
        'omp_example_serviceapp',
        'omp_example_webapp_blazor',
        'omp_example_webapp',
        'omp_portal',
        'omp'
    )

    RemovePaths = @(
        'Sites\Portal',
        'WebApps\auth',
        'WebApps\opendocviewer',
        'WebApps\content',
        'WebApps\ExampleWebAppModule',
        'WebApps\ExampleWebAppBlazorModule',
        'WebApps\ExampleServiceAppModule',
        'WebApps\ExampleWorkerAppModule',
        'WebApps\iFrameWebAppModule',
        'Services\HostAgent',
        'Services\WorkerManager',
        'Services\WorkerProcessHost',
        'Services\ExampleServiceAppModule',
        'ArtifactStore\opendocviewer',
        'ArtifactStore\example-workerapp',
        'ArtifactCache',
        'DataProtectionKeys'
    )
}

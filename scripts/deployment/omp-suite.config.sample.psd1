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
        ServerReportsPath = 'App_Data/ContentReports'
        HtmlFilesPath = 'App_Data/ContentPages'
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

    Package = @{
        OutputRoot = 'artifacts\suite-release'
        KeepStaging = $false
        SkipRestore = $false
        SkipOpenDocViewerBuild = $false
        SkipOpenDocViewerNpmInstall = $false
        # Optional path to a prebuilt OpenDocViewer zip. Use this for customer
        # packages that need a site-local odv.site.config.js and help/site manual.
        # When set, the package script uses this zip as payload/OpenDocViewer.dist.zip
        # instead of zipping OpenDocViewer\dist directly.
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

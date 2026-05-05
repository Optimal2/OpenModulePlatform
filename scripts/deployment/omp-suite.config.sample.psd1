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
    DataProtectionKeyPath = 'C:\OMP\DataProtectionKeys'

    SqlServer = 'localhost'
    Database = 'OpenModulePlatform'
    SqlAuthentication = 'Integrated' # Integrated or SqlLogin.
    SqlUser = ''
    SqlPassword = ''

    BootstrapPortalAdminPrincipals = @('DOMAIN\UserOrGroup')
    BootstrapPortalAdminPrincipalType = 'User' # User or ADGroup.

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
        PermissionMode = 'Any'
        # Leave empty to use PublicBaseUrl, or set '/' for a root-relative portal.
        PortalBaseUrl = '/'
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
        AppPools = @{
            Portal = 'OMP_Portal'
            Auth = 'OMP_Auth'
            OpenDocViewer = 'OMP_OpenDocViewer'
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
    }

    Options = @{
        InstallOpenDocViewer = $true
        InstallExamples = $true
        InstallRuntimeServices = $true
        InstallExampleService = $true
        ConfigureIis = $true
        RunSql = $true
        StartServices = $true
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
        'WebApps\ExampleWebAppModule',
        'WebApps\ExampleWebAppBlazorModule',
        'WebApps\ExampleServiceAppModule',
        'WebApps\ExampleWorkerAppModule',
        'WebApps\iFrameWebAppModule',
        'Services\HostAgent',
        'Services\WorkerManager',
        'Services\WorkerProcessHost',
        'Services\ExampleServiceAppModule',
        'ArtifactStore\example-workerapp',
        'ArtifactCache',
        'DataProtectionKeys'
    )
}

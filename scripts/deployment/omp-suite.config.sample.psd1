@{
    EnvironmentName = 'local-dev'

    # Source mode builds from repositories before installation.
    # Package mode installs from an already expanded package folder.
    DeploymentMode = 'Source'
    RepositoryRoot = ''
    OpenDocViewerRoot = ''
    PackageRoot = ''

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
    DatabaseOwnerPrincipal = ''

    RunAsUser = ''
    RunAsPassword = ''

    HostKey = 'sample-host'
    HostName = ''

    Iis = @{
        SiteName = 'OpenModulePlatform'
        Protocol = 'http'
        Port = 8088
        HostHeader = ''
        CertificateThumbprint = ''
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
        RemoveIis = $true
        RemoveServices = $true
        RemoveFiles = $true
        RemoveDatabaseObjects = $true
        DropSchemas = $true
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

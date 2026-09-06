# Pester 5 parameters come from the pinned module, not the inbox 3.4.0 profile.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 5 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()

# Exercise the production SQL-copy section without publishing apps or installing a runtime.
Describe 'HostAgent-first module seed source' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'ArtifactPackageWorkerHostRoundTrip.TestHelpers.ps1')
        Import-HostAgentArtifactPackageFunctions

        $source = Get-Content -LiteralPath $hostAgentPackageScript -Raw
        $copySection = [regex]::Match($source,
            "(?s)Write-Step 'Copying SQL scripts'(?<body>.*?)Write-Step 'Copying module definitions'")
        $copySection.Success | Should -BeTrue
        $copySqlScripts = [scriptblock]::Create($copySection.Groups['body'].Value)
        $rootCheck = [regex]::Match($source,
            '(?s)if \(-not \(Test-Path -LiteralPath \$OpenDocViewerRoot -PathType Container\)\) \{.*?\}')
        $rootCheck.Success | Should -BeTrue
        $checkModuleRoot = [scriptblock]::Create($rootCheck.Value)
        $sqlList = $copySqlScripts.Ast.Find({
            param($node)
            $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left.Extent.Text -eq '$sqlFiles'
        }, $true)
        $sqlList | Should -Not -BeNullOrEmpty
        $initializeSqlList = [scriptblock]::Create($sqlList.Extent.Text)
    }

    BeforeEach {
        $fixtureRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $RepositoryRoot = Join-Path $fixtureRoot 'platform'
        $OpenDocViewerRoot = Join-Path $fixtureRoot 'module'
        $sqlRoot = Join-Path $fixtureRoot 'package-sql'
        $seedRelativePath = 'sql\3-initialize-opendocviewer.sql'
        $seedDestination = Join-Path $sqlRoot 'OpenModulePlatform\3-initialize-opendocviewer.sql'
        New-Item -ItemType Directory -Path (Join-Path $OpenDocViewerRoot 'sql') -Force | Out-Null

        # Supply ordinary platform inputs plus a stale seed, so a fallback would
        # succeed and the negative tests must distinguish it from a required source.
        . $initializeSqlList
        foreach ($file in $sqlFiles) {
            $path = Join-Path $RepositoryRoot $file.Source
            New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
            [System.IO.File]::WriteAllText($path, 'SELECT 1;')
        }
        [System.IO.File]::WriteAllText((Join-Path $RepositoryRoot $seedRelativePath), 'SELECT N''stale platform seed'';')
    }

    It 'copies the owning repository seed to the unchanged installer destination' {
        $seedSource = Join-Path $OpenDocViewerRoot $seedRelativePath
        [System.IO.File]::WriteAllText($seedSource, 'SELECT N''current module seed'';')
        . $checkModuleRoot
        . $copySqlScripts
        # Compare bytes via .NET rather than Get-FileHash: on a workstation whose PSModulePath also
        # lists PowerShell 7 module folders, Windows PowerShell 5.1 loads a hybrid Utility module
        # without Get-FileHash, and the gate then fails for an environmental reason.
        [Convert]::ToBase64String([System.Security.Cryptography.SHA256]::Create().ComputeHash([System.IO.File]::ReadAllBytes($seedDestination))) |
            Should -Be ([Convert]::ToBase64String([System.Security.Cryptography.SHA256]::Create().ComputeHash([System.IO.File]::ReadAllBytes($seedSource))))
        @($sqlFiles | Where-Object { $_.Source -eq $seedRelativePath }).Count | Should -Be 0
    }

    It 'fails clearly when the module repository root is missing' {
        $OpenDocViewerRoot = Join-Path $fixtureRoot 'missing-module'
        { . $checkModuleRoot; . $copySqlScripts } | Should -Throw '*repository root was not found*'
        Test-Path -LiteralPath $seedDestination | Should -BeFalse
    }

    It 'fails when the module seed is missing even though a stale platform seed exists' {
        . $checkModuleRoot
        { . $copySqlScripts } | Should -Throw '*Required file not found*3-initialize-opendocviewer.sql*'
        Test-Path -LiteralPath $seedDestination | Should -BeFalse
    }
}

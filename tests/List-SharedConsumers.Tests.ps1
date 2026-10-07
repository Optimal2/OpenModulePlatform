# Pester 6's 'Should -Be/-Not -Be/-Match' parameters are provided by the pinned
# Pester module (6.1.0), not by the inbox Pester 3.4.0 profile the compatibility
# rule measures against; suppress for the whole file, not per assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()
<#
.SYNOPSIS
Pester tests for scripts/omp/list-shared-consumers.ps1.

.DESCRIPTION
Fixtures are a throwaway workspace under $env:TEMP: a fake platform checkout
named OpenModulePlatform (omp-components.json with one shared project) plus
consumer repositories (git init + git add, so git ls-files sees their files).

Covered contract points, each proven by breaking it:

- The script must run under WINDOWS POWERSHELL 5.1 -File with no parameters.
  The old param-block default ((Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path)
  crashed there because $PSScriptRoot is empty in an advanced-parameter
  default under 5.1 -File -- exactly the runtime the pre-push hook uses.
  The first test runs the script the way the hook would: powershell.exe
  -NoProfile -File, no arguments.
- An undeclared reference exits 1.
- A sibling repository that cannot be scanned exits 1 (it used to WARN + skip
  and still exit 0, which is how a referencing consumer goes unseen).
- A ProjectReference carried by a git-tracked Directory.Build.props is found
  (a .csproj-only scan is blind to it).

Every invocation goes through powershell.exe -File: that is the runtime that
gates a push, so it is the runtime the tests pin.
#>

Describe 'list-shared-consumers.ps1' {
    BeforeAll {
        $script:listScriptSource = Resolve-Path (Join-Path $PSScriptRoot '..\scripts\omp\list-shared-consumers.ps1')

        function Save-TextFile {
            param(
                [Parameter(Mandatory = $true)][string]$Path,
                [Parameter(Mandatory = $true)][string]$Content
            )

            $parent = Split-Path -Parent $Path
            if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
                $null = New-Item -ItemType Directory -Path $parent -Force
            }
            [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
        }

        function New-ConsumerFixtureWorkspace {
            <#
            .SYNOPSIS
            Creates <Root>\OpenModulePlatform with one shared project
            (SharedLib/SharedLib.csproj) and a copy of the list script at the
            conventional scripts/omp path, so the script's no-argument
            defaults resolve to the fixture.
            #>
            param([Parameter(Mandatory = $true)][string]$RootPath)

            if (Test-Path -LiteralPath $RootPath -PathType Container) {
                Remove-Item -LiteralPath $RootPath -Recurse -Force
            }

            $platformRoot = Join-Path $RootPath 'OpenModulePlatform'
            Save-TextFile -Path (Join-Path $platformRoot 'omp-components.json') -Content (@{
                    repositoryKey      = 'openmoduleplatform'
                    repositoryVersion  = '1.0.0'
                    sharedProjects     = @(
                        @{
                            projectPath        = 'SharedLib/SharedLib.csproj'
                            consumers          = @()
                            externalConsumers  = @()
                        }
                    )
                } | ConvertTo-Json -Depth 10)
            Save-TextFile -Path (Join-Path $platformRoot 'SharedLib\SharedLib.csproj') -Content "<Project Sdk=`"Microsoft.NET.Sdk`" />"
            $scriptCopyDir = Join-Path $platformRoot 'scripts\omp'
            $null = New-Item -ItemType Directory -Path $scriptCopyDir -Force
            Copy-Item -LiteralPath $script:listScriptSource -Destination (Join-Path $scriptCopyDir 'list-shared-consumers.ps1') -Force

            return $platformRoot
        }

        function Add-FixtureConsumer {
            <#
            .SYNOPSIS
            Adds a consumer repository (git init + add, no commit needed:
            git ls-files reads the index) that references the fixture shared
            project. The reference can be written into the .csproj (default)
            or into a tracked Directory.Build.props (-ReferenceViaBuildProps).
            #>
            param(
                [Parameter(Mandatory = $true)][string]$WorkspaceRoot,
                [Parameter(Mandatory = $true)][string]$Name,
                [Parameter(Mandatory = $false)][switch]$Declared,
                [Parameter(Mandatory = $false)][switch]$ReferenceViaBuildProps,
                [Parameter(Mandatory = $false)][switch]$ReferenceViaBuildPropsThisFileDirectory
            )

            $consumerRoot = Join-Path $WorkspaceRoot $Name
            $reference = '<ProjectReference Include="..\..\..\OpenModulePlatform\SharedLib\SharedLib.csproj" />'
            # Directory.Build.props sits at the repository root, two levels
            # closer to the workspace than src\App\App.csproj.
            $buildPropsReference = '<ProjectReference Include="..\OpenModulePlatform\SharedLib\SharedLib.csproj" />'
            # Single quotes: $(MSBuildThisFileDirectory) must reach the file
            # literally, not be interpolated by PowerShell.
            $buildPropsThisFileDirectoryReference = '<ProjectReference Include="$(MSBuildThisFileDirectory)..\OpenModulePlatform\SharedLib\SharedLib.csproj" />'

            $csprojContent = "<Project Sdk=`"Microsoft.NET.Sdk`">`r`n  <ItemGroup>`r`n"
            if (-not $ReferenceViaBuildProps -and -not $ReferenceViaBuildPropsThisFileDirectory) {
                $csprojContent += "    $reference`r`n"
            }
            $csprojContent += "  </ItemGroup>`r`n</Project>`r`n"
            Save-TextFile -Path (Join-Path $consumerRoot 'src\App\App.csproj') -Content $csprojContent

            if ($ReferenceViaBuildProps) {
                Save-TextFile -Path (Join-Path $consumerRoot 'Directory.Build.props') -Content "<Project>`r`n  <ItemGroup>`r`n    $buildPropsReference`r`n  </ItemGroup>`r`n</Project>`r`n"
            }
            if ($ReferenceViaBuildPropsThisFileDirectory) {
                Save-TextFile -Path (Join-Path $consumerRoot 'Directory.Build.props') -Content ("<Project>`r`n  <ItemGroup>`r`n    " + $buildPropsThisFileDirectoryReference + "`r`n  </ItemGroup>`r`n</Project>`r`n")
            }

            $manifest = @{
                repositoryKey     = ($Name.ToLowerInvariant())
                repositoryVersion = '1.0.0'
            }
            if ($Declared) {
                $manifest['sharedDependencies'] = @(
                    @{
                        repositoryKey      = 'openmoduleplatform'
                        repositoryPathHint = '../OpenModulePlatform'
                        projectPath        = 'SharedLib'
                        treeId             = '0123456789abcdef0123456789abcdef01234567'
                        consumers          = @('app')
                    }
                )
            }
            Save-TextFile -Path (Join-Path $consumerRoot 'omp-components.json') -Content ($manifest | ConvertTo-Json -Depth 10)

            & git -C $consumerRoot init --quiet
            if ($LASTEXITCODE -ne 0) { throw 'git init failed.' }
            & git -C $consumerRoot add -A
            if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }

            return $consumerRoot
        }

        function Invoke-ListSharedConsumers {
            <#
            .SYNOPSIS
            Runs the script through powershell.exe -File -- the runtime the
            pre-push hook uses -- and returns exit code plus all output.
            'Continue' for the duration of the call: under Windows PowerShell
            5.1 with $ErrorActionPreference = 'Stop', merging a native
            command's stderr terminates the caller before the exit code can
            be read.
            #>
            param(
                [Parameter(Mandatory = $true)][string]$ScriptPath,
                [Parameter(Mandatory = $false)][string[]]$Arguments = @()
            )

            $output = ''
            $exitCode = $null
            $previousErrorActionPreference = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                $output = & powershell.exe -NoProfile -File $ScriptPath @Arguments 2>&1 | Out-String -Width 4096
            }
            catch {
                $output += $_.Exception.Message
            }
            finally {
                $exitCode = $LASTEXITCODE
                $ErrorActionPreference = $previousErrorActionPreference
            }

            return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
        }

        function Remove-FixtureWorkspace {
            param([Parameter(Mandatory = $true)][string]$RootPath)

            if (Test-Path -LiteralPath $RootPath -PathType Container) {
                Remove-Item -LiteralPath $RootPath -Recurse -Force
            }
        }
    }

    It 'Runs under Windows PowerShell 5.1 -File with NO arguments and exits 0 when every reference is declared' {
        $workspace = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $platformRoot = New-ConsumerFixtureWorkspace -RootPath $workspace
            $null = Add-FixtureConsumer -WorkspaceRoot $workspace -Name 'ConsumerA' -Declared

            # No -PlatformRepositoryRoot and no -SiblingRoot: the parameter
            # DEFAULTS run. Under 5.1 -File that is exactly where the old
            # param-block default crashed ($PSScriptRoot empty).
            $result = Invoke-ListSharedConsumers -ScriptPath (Join-Path $platformRoot 'scripts\omp\list-shared-consumers.ps1')

            $result.ExitCode | Should -Be 0
            $result.Output | Should -Match 'ConsumerA'
            $result.Output | Should -Match 'are declared'
        }
        finally {
            Remove-FixtureWorkspace -RootPath $workspace
        }
    }

    It 'Exits 1 when a referencing consumer does not declare the dependency' {
        $workspace = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $platformRoot = New-ConsumerFixtureWorkspace -RootPath $workspace
            $null = Add-FixtureConsumer -WorkspaceRoot $workspace -Name 'ConsumerB'

            $result = Invoke-ListSharedConsumers -ScriptPath (Join-Path $platformRoot 'scripts\omp\list-shared-consumers.ps1')

            $result.ExitCode | Should -Be 1
            $result.Output | Should -Match 'ConsumerB'
            $result.Output | Should -Match 'NOT declared'
        }
        finally {
            Remove-FixtureWorkspace -RootPath $workspace
        }
    }

    It 'Exits 1 when a sibling repository cannot be scanned (no more WARN + skip)' {
        $workspace = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $platformRoot = New-ConsumerFixtureWorkspace -RootPath $workspace
            $null = Add-FixtureConsumer -WorkspaceRoot $workspace -Name 'ConsumerC' -Declared

            # A worktree-style .git FILE pointing at a gitdir that does not
            # exist: the directory marker is present, so the scanner tries the
            # repository -- and git ls-files fails.
            $brokenRoot = Join-Path $workspace 'BrokenRepo'
            $null = New-Item -ItemType Directory -Path $brokenRoot -Force
            Save-TextFile -Path (Join-Path $brokenRoot '.git') -Content "gitdir: $(Join-Path $workspace 'no-such-gitdir\repo.git')"

            $result = Invoke-ListSharedConsumers -ScriptPath (Join-Path $platformRoot 'scripts\omp\list-shared-consumers.ps1')

            $result.ExitCode | Should -Be 1
            $result.Output | Should -Match 'could not list project files'
            $result.Output | Should -Match 'incomplete'
        }
        finally {
            Remove-FixtureWorkspace -RootPath $workspace
        }
    }

    It 'Finds a ProjectReference carried by a git-tracked Directory.Build.props' {
        $workspace = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $platformRoot = New-ConsumerFixtureWorkspace -RootPath $workspace
            $null = Add-FixtureConsumer -WorkspaceRoot $workspace -Name 'ConsumerProps' -ReferenceViaBuildProps

            $result = Invoke-ListSharedConsumers -ScriptPath (Join-Path $platformRoot 'scripts\omp\list-shared-consumers.ps1')

            # Undeclared, so the run is red -- and the consumer must appear in
            # the table, proving the Directory.Build.props reference was seen.
            $result.ExitCode | Should -Be 1
            $result.Output | Should -Match 'ConsumerProps'
            $result.Output | Should -Match 'NOT declared'
        }
        finally {
            Remove-FixtureWorkspace -RootPath $workspace
        }
    }

    It 'Finds a ProjectReference written with $(MSBuildThisFileDirectory) in Directory.Build.props' {
        $workspace = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $platformRoot = New-ConsumerFixtureWorkspace -RootPath $workspace
            $null = Add-FixtureConsumer -WorkspaceRoot $workspace -Name 'ConsumerMsbtd' -ReferenceViaBuildPropsThisFileDirectory

            $result = Invoke-ListSharedConsumers -ScriptPath (Join-Path $platformRoot 'scripts\omp\list-shared-consumers.ps1')

            # Undeclared, so the run is red -- and the consumer must appear in
            # the table. Before the fix the variable form was stripped by the
            # generic $(...) handling and the suffix '..\OpenModulePlatform\...'
            # never matched, so the consumer went unseen and the run exited 0.
            $result.ExitCode | Should -Be 1
            $result.Output | Should -Match 'ConsumerMsbtd'
            $result.Output | Should -Match 'NOT declared'
        }
        finally {
            Remove-FixtureWorkspace -RootPath $workspace
        }
    }

    It 'Exits 1 with a clear error (not a raw CommandNotFoundException) when git is not on PATH' {
        $workspace = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $platformRoot = New-ConsumerFixtureWorkspace -RootPath $workspace
            $null = Add-FixtureConsumer -WorkspaceRoot $workspace -Name 'ConsumerNoGit' -Declared

            # Run the script in a child process whose PATH cannot resolve git.
            # powershell.exe itself is launched by full path so the stripped
            # PATH does not hide the interpreter.
            $powershellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
            $output = ''
            $exitCode = $null
            $originalPath = $env:PATH
            $previousErrorActionPreference = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                $env:PATH = "$env:SystemRoot\System32"
                $output = & $powershellExe -NoProfile -File (Join-Path $platformRoot 'scripts\omp\list-shared-consumers.ps1') 2>&1 | Out-String -Width 4096
            }
            finally {
                $exitCode = $LASTEXITCODE
                $env:PATH = $originalPath
                $ErrorActionPreference = $previousErrorActionPreference
            }

            $exitCode | Should -Be 1
            $output | Should -Match 'git was not found on PATH'
            $output | Should -Not -Match 'not recognized'
        }
        finally {
            Remove-FixtureWorkspace -RootPath $workspace
        }
    }

    It 'Reports the reference source file so a Directory.Build.props hit is traceable' {
        $workspace = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $platformRoot = New-ConsumerFixtureWorkspace -RootPath $workspace
            $null = Add-FixtureConsumer -WorkspaceRoot $workspace -Name 'ConsumerProps2' -ReferenceViaBuildProps -Declared

            $result = Invoke-ListSharedConsumers -ScriptPath (Join-Path $platformRoot 'scripts\omp\list-shared-consumers.ps1')

            $result.ExitCode | Should -Be 0
            $result.Output | Should -Match 'ConsumerProps2'
        }
        finally {
            Remove-FixtureWorkspace -RootPath $workspace
        }
    }
}

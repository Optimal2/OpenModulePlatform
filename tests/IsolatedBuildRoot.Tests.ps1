# Pester 6's 'Should -Be/-Not -Be/-Match' parameters are provided by the pinned
# Pester module (6.1.0), not by the inbox Pester 3.4.0 profile the compatibility
# rule measures against; suppress for the whole file, not per assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()
<#
.SYNOPSIS
Pester tests for the OmpIsolatedBuildRoot validation in Directory.Build.props
and Directory.Build.targets.

.DESCRIPTION
A hand-run consumer build once passed -p:OmpIsolatedBuildRoot=artifacts\omp-isolated-build.
MSBuild resolves a relative value against EACH project, so the referenced OMP
projects (Web.Shared, EventPublisher.*, Web.Shared.Analyzers) wrote their
obj/bin into their own folders in this repository, and the next build globbed
the generated AssemblyInfo files in as sources (CS0579). These suites run a
cheap target on one OMP project and require that a relative root or a root
inside this repository stops the build with an actionable message, that an
absolute root outside the repository still builds, and that a leftover
artifacts folder inside a project is never compiled.
#>

Describe 'OmpIsolatedBuildRoot validation' {
    BeforeAll {
        $script:repositoryRoot = Split-Path -Parent $PSScriptRoot
        $script:project = Join-Path $script:repositoryRoot 'OpenModulePlatform.EventPublisher.Abstractions\OpenModulePlatform.EventPublisher.Abstractions.csproj'
        # TEMP may deliberately live inside a worktree. GetTargetPath only
        # evaluates a path; use a non-existent sibling to probe an external root
        # without creating output or requiring write access outside the checkout.
        $script:outsideRoot = Join-Path (Split-Path -Parent $script:repositoryRoot) ('omp-isolated-probe-' + [Guid]::NewGuid().ToString('N'))

        function script:Invoke-Probe {
            param([Parameter(Mandatory = $true)][string]$IsolatedBuildRoot)

            $output = & dotnet msbuild $script:project -t:GetTargetPath -nologo -v:q ('-p:OmpIsolatedBuildRoot=' + $IsolatedBuildRoot) 2>&1 |
                ForEach-Object { $_.ToString() }
            [pscustomobject]@{
                ExitCode = $LASTEXITCODE
                Text = [string]::Join([Environment]::NewLine, @($output))
            }
        }
    }

    It 'Fails a relative OmpIsolatedBuildRoot and tells the operator to pass an absolute path' {
        $result = Invoke-Probe -IsolatedBuildRoot 'artifacts\omp-isolated-build'
        $result.ExitCode | Should -Not -Be 0
        $result.Text | Should -Match 'OMPBUILD001'
        $result.Text | Should -Match 'absolute path'
    }

    It 'Fails an absolute OmpIsolatedBuildRoot inside this repository' {
        $result = Invoke-Probe -IsolatedBuildRoot (Join-Path $script:repositoryRoot 'artifacts\omp-isolated-build')
        $result.ExitCode | Should -Not -Be 0
        $result.Text | Should -Match 'OMPBUILD002'
    }

    It 'Fails a drive-relative OmpIsolatedBuildRoot such as C:artifacts\...' {
        # IsPathRooted('C:artifacts') is true, but the path resolves against the
        # current directory of that drive, which can be inside this repository.
        $driveRelative = $script:repositoryRoot.Substring(0, 2) + 'artifacts\omp-isolated-build'
        $result = Invoke-Probe -IsolatedBuildRoot $driveRelative
        $result.ExitCode | Should -Not -Be 0
        $result.Text | Should -Match 'OMPBUILD001'
    }

    It 'Fails an OmpIsolatedBuildRoot that reaches this repository through a junction' {
        # The link points at the repository itself; only the link is removed
        # afterwards, never what it points at. The probe target writes nothing.
        $linkParent = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-isolated-alias-' + [Guid]::NewGuid().ToString('N'))
        $link = Join-Path $linkParent 'omp-alias'
        [void](New-Item -ItemType Directory -Path $linkParent -Force)
        [void](New-Item -ItemType Junction -Path $link -Value $script:repositoryRoot)
        try {
            $result = Invoke-Probe -IsolatedBuildRoot (Join-Path $link 'artifacts\omp-isolated-build')
        }
        finally {
            [System.IO.Directory]::Delete($link)
            [System.IO.Directory]::Delete($linkParent)
        }
        $result.ExitCode | Should -Not -Be 0
        $result.Text | Should -Match 'OMPBUILD002'
    }

    It 'Passes an absolute OmpIsolatedBuildRoot outside this repository' {
        $result = Invoke-Probe -IsolatedBuildRoot $script:outsideRoot
        $result.Text | Should -Not -Match 'OMPBUILD00'
        $result.ExitCode | Should -Be 0
        Test-Path -LiteralPath $script:outsideRoot | Should -BeFalse
    }
}

Describe 'Leftover artifacts folders are not compiled' {
    BeforeAll {
        $script:repositoryRoot = Split-Path -Parent $PSScriptRoot
        $projectDirectory = Join-Path $script:repositoryRoot 'OpenModulePlatform.EventPublisher.Abstractions'
        $script:project = Join-Path $projectDirectory 'OpenModulePlatform.EventPublisher.Abstractions.csproj'
        $script:strayRoot = Join-Path $projectDirectory 'artifacts'
        $strayFolder = Join-Path $script:strayRoot 'omp-isolated-build\obj\Probe'
        $script:strayRootExisted = Test-Path -LiteralPath $script:strayRoot
        [void](New-Item -ItemType Directory -Path $strayFolder -Force)
        Set-Content -LiteralPath (Join-Path $strayFolder 'OmpStrayProbe.AssemblyInfo.cs') -Value '// stray' -Encoding ASCII
    }

    AfterAll {
        if (-not $script:strayRootExisted -and (Test-Path -LiteralPath $script:strayRoot)) {
            Remove-Item -LiteralPath $script:strayRoot -Recurse -Force
        }
    }

    It 'Excludes the artifacts folder of a project from the default Compile glob' {
        $compile = & dotnet msbuild $script:project -nologo -getItem:Compile 2>&1 | ForEach-Object { $_.ToString() }
        $LASTEXITCODE | Should -Be 0
        [string]::Join([Environment]::NewLine, @($compile)) | Should -Not -Match 'OmpStrayProbe'
    }
}

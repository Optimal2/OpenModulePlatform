#Requires -Version 5.1
# The 'Should -Be/-Not -Be/-Match' parameters are provided by the pinned
# Pester module, not by the inbox Pester 3.4.0 profile the compatibility
# rule measures against; suppress for the whole file, not per assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pinned Pester dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()
<#
.SYNOPSIS
    Proves package builds refuse dirty source trees and stamp source provenance.

.DESCRIPTION
    A customer environment once held an artifact with the same version but
    different content than the package carried: no committed change explained
    it, because every committed input change carried its version bump. The gap
    was in the package build itself, which never checked that the source tree
    was clean and never recorded which commit an artifact was built from.

    These proofs cover the closed gap from both sides: the dirty-tree gate in
    scripts/omp/export-universal-package.ps1 (fails without -AllowDirtySource,
    stamps sourceDirty=true with it) and the provenance stamp in every
    artifact manifest and in the universal package manifest (commit SHA plus
    the dirty flag, optional on read so older importers keep working).
#>

Set-StrictMode -Version Latest

Describe 'source provenance helper' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Universal-Package-Provenance.TestHelpers.ps1')
        . $script:SourceProvenanceScript
    }

    It 'Reports the HEAD commit SHA on a clean tree' {
        $repo = New-ProvenanceRepo
        try {
            $provenance = Get-OmpSourceProvenance -RepositoryRoot $repo.Root
            $provenance.CommitSha | Should -Be $repo.Head
            $provenance.Dirty | Should -Be $false
        }
        finally { Remove-ProvenanceRepo -Repo $repo }
    }

    It 'Flags a modified tracked file as dirty' {
        $repo = New-ProvenanceRepo -DirtyEdit
        try {
            $provenance = Get-OmpSourceProvenance -RepositoryRoot $repo.Root
            $provenance.Dirty | Should -Be $true
            $provenance.CommitSha | Should -Be $repo.Head
        }
        finally { Remove-ProvenanceRepo -Repo $repo }
    }

    It 'Flags an untracked file as dirty' {
        $repo = New-ProvenanceRepo -UntrackedFile
        try {
            $provenance = Get-OmpSourceProvenance -RepositoryRoot $repo.Root
            $provenance.Dirty | Should -Be $true
        }
        finally { Remove-ProvenanceRepo -Repo $repo }
    }

    It 'Ignores line-ending noise under core.autocrlf=true although git status lists the file' {
        # The Windows checkout: the file only looks changed. git status
        # --porcelain lists it, the content questions do not.
        $repo = New-ProvenanceRepo -AutoCrlf -CrlfOnlyChange
        try {
            $porcelain = (git -C $repo.Root status --porcelain | Out-String).Trim()
            ($porcelain -ne '') | Should -Be $true
            $provenance = Get-OmpSourceProvenance -RepositoryRoot $repo.Root
            $provenance.Dirty | Should -Be $false
            $provenance.ChangedPaths | Should -Be ''
        }
        finally { Remove-ProvenanceRepo -Repo $repo }
    }

    It 'Counts a CRLF rewrite that git records as a byte change as dirty' {
        # No normalization configured (core.autocrlf=false, no .gitattributes):
        # git diff reports the file, and the build would ship those bytes.
        # The former porcelain + --ignore-cr-at-eol verdict called this clean.
        $repo = New-ProvenanceRepo -CrlfOnlyChange
        try {
            $diff = (git -C $repo.Root diff --name-only | Out-String).Trim()
            $diff | Should -Be 'notes.txt'
            $provenance = Get-OmpSourceProvenance -RepositoryRoot $repo.Root
            $provenance.Dirty | Should -Be $true
            $provenance.ChangedPaths | Should -Be 'worktree: notes.txt'
        }
        finally { Remove-ProvenanceRepo -Repo $repo }
    }

    It 'Judges content, not timestamps' {
        $repo = New-ProvenanceRepo -TouchOnly
        try {
            $provenance = Get-OmpSourceProvenance -RepositoryRoot $repo.Root
            $provenance.Dirty | Should -Be $false
        }
        finally { Remove-ProvenanceRepo -Repo $repo }
    }

    It 'Flags a staged change as dirty and names the question that found it' {
        $repo = New-ProvenanceRepo -StagedEdit
        try {
            $provenance = Get-OmpSourceProvenance -RepositoryRoot $repo.Root
            $provenance.Dirty | Should -Be $true
            $provenance.ChangedPaths | Should -Be 'staged: notes.txt'
        }
        finally { Remove-ProvenanceRepo -Repo $repo }
    }

    It 'Throws instead of stamping clean when the folder is not a checkout' {
        $plain = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-provenance-plain-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $plain -Force | Out-Null
        try {
            $threw = $false
            try { Get-OmpSourceProvenance -RepositoryRoot $plain | Out-Null } catch { $threw = $true }
            $threw | Should -Be $true
        }
        finally { try { Remove-Item -LiteralPath $plain -Recurse -Force -ErrorAction Stop } catch { } }
    }

    It 'Throws without the switch and stamps dirty with it' {
        $repo = New-ProvenanceRepo -DirtyEdit
        try {
            $threw = $false
            try {
                Assert-OmpSourceTreeClean -RepositoryRoot $repo.Root | Out-Null
            }
            catch {
                $threw = $true
                ("$_" -match 'AllowDirtySource') | Should -Be $true
            }

            $threw | Should -Be $true
            $provenance = Assert-OmpSourceTreeClean -RepositoryRoot $repo.Root -AllowDirtySource
            $provenance.Dirty | Should -Be $true
            $provenance.CommitSha | Should -Be $repo.Head
        }
        finally { Remove-ProvenanceRepo -Repo $repo }
    }
}

Describe 'shared source gate' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Universal-Package-Provenance.TestHelpers.ps1')
        . $script:SourceProvenanceScript

        function Invoke-ObjectBuilder {
            param(
                [Parameter(Mandatory = $true)][hashtable]$Consumer,
                [Parameter(Mandatory = $true)][string]$ComponentKey,
                [string]$ReportPath = '',
                [switch]$AllowDirtySource
            )

            $arguments = @(
                '-RepositoryRoot', $Consumer.Root,
                '-OmpRepositoryRoot', $Consumer.Sibling,
                '-OutputRoot', (Join-Path $Consumer.Parent 'objects'),
                '-ComponentKey', $ComponentKey,
                '-SourceCommitSha', 'fixture',
                '-EnforceCleanSharedSources'
            )
            if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
                $arguments += @('-SharedSourceReportPath', $ReportPath)
            }
            if ($AllowDirtySource) {
                $arguments += '-AllowDirtySource'
            }

            return Invoke-ChildScript -ScriptPath $script:ObjectBuilderScript -Arguments $arguments
        }
    }

    BeforeEach {
        # The resolver honours OpenModulePlatformRoot like Check 14; keep a
        # developer's value out of the fixture.
        $script:SavedOmpRoot = $env:OpenModulePlatformRoot
        $env:OpenModulePlatformRoot = $null
    }

    AfterEach {
        $env:OpenModulePlatformRoot = $script:SavedOmpRoot
    }

    It 'Resolves the sibling behind sharedDependencies like Check 14, with its consumers' {
        $consumer = New-ProvenanceConsumer
        try {
            $roots = @(Get-OmpSharedSourceRoots -RepositoryRoot $consumer.Root)
            $roots.Count | Should -Be 1
            $roots[0].RepositoryKey | Should -Be 'provenance-sibling'
            $roots[0].RepositoryRoot | Should -Be ([System.IO.Path]::GetFullPath($consumer.Sibling))
            (Test-OmpSharedSourceConsumedBy -SharedSource $roots[0] -ComponentKey 'provenance-app') | Should -Be $true
            (Test-OmpSharedSourceConsumedBy -SharedSource $roots[0] -ComponentKey 'other-app') | Should -Be $false
        }
        finally { Remove-ProvenanceConsumer -Consumer $consumer }
    }

    It 'Refuses a dirty sibling without the switch and reports it with the switch' {
        $consumer = New-ProvenanceConsumer -DirtySibling
        try {
            $roots = @(Get-OmpSharedSourceRoots -RepositoryRoot $consumer.Root)
            $threw = $false
            try {
                Assert-OmpSharedSourcesClean -SharedSources $roots | Out-Null
            }
            catch {
                $threw = $true
                ("$_" -match 'dirty source tree') | Should -Be $true
                ("$_" -match 'AllowDirtySource') | Should -Be $true
            }

            $threw | Should -Be $true
            $shared = @(Assert-OmpSharedSourcesClean -SharedSources $roots -AllowDirtySource)
            $shared.Count | Should -Be 1
            $shared[0].Dirty | Should -Be $true
            $shared[0].CommitSha | Should -Be $consumer.SiblingHead
        }
        finally { Remove-ProvenanceConsumer -Consumer $consumer }
    }

    It 'Fails when the sibling is missing, even with the switch' {
        $consumer = New-ProvenanceConsumer
        try {
            $roots = @(Get-OmpSharedSourceRoots -RepositoryRoot $consumer.Root)
            Remove-Item -LiteralPath $consumer.Sibling -Recurse -Force
            $threw = $false
            try {
                Assert-OmpSharedSourcesClean -SharedSources $roots -AllowDirtySource | Out-Null
            }
            catch {
                $threw = $true
                ("$_" -match 'was not found') | Should -Be $true
            }

            $threw | Should -Be $true
        }
        finally { Remove-ProvenanceConsumer -Consumer $consumer }
    }

    It 'Returns nothing for a repository without sharedDependencies' {
        $repo = New-ProvenanceRepo
        try {
            @(Get-OmpSharedSourceRoots -RepositoryRoot $repo.Root).Count | Should -Be 0
        }
        finally { Remove-ProvenanceRepo -Repo $repo }
    }

    It 'Refuses to build a consuming component while its sibling is dirty' {
        $consumer = New-ProvenanceConsumer -DirtySibling
        try {
            $result = Invoke-ObjectBuilder -Consumer $consumer -ComponentKey 'provenance-app'
            ($result.ExitCode -ne 0) | Should -Be $true
            ($result.Output -match 'dirty source tree') | Should -Be $true
            ($result.Output -match 'SharedSibling') | Should -Be $true
        }
        finally { Remove-ProvenanceConsumer -Consumer $consumer }
    }

    It 'Neither gates nor stamps a dirty sibling that a built component does not consume' {
        $consumer = New-ProvenanceConsumer -DirtySibling
        $report = Join-Path $consumer.Parent 'shared-sources.json'
        try {
            $result = Invoke-ObjectBuilder -Consumer $consumer -ComponentKey 'other-app' -ReportPath $report
            $result.ExitCode | Should -Be 0
            ($result.Output -match 'dirty source tree') | Should -Be $false
            @(Get-Content -LiteralPath $report -Raw | ConvertFrom-Json | ForEach-Object { $_ }).Count | Should -Be 0
        }
        finally { Remove-ProvenanceConsumer -Consumer $consumer }
    }

    It 'Neither gates nor stamps a dirty sibling behind a reused package' {
        $consumer = New-ProvenanceConsumer -DirtySibling -ReusedPackage
        $report = Join-Path $consumer.Parent 'shared-sources.json'
        try {
            $result = Invoke-ObjectBuilder -Consumer $consumer -ComponentKey 'provenance-app' -ReportPath $report
            $result.ExitCode | Should -Be 0
            ($result.Output -match 'dirty source tree') | Should -Be $false
            @(Get-Content -LiteralPath $report -Raw | ConvertFrom-Json | ForEach-Object { $_ }).Count | Should -Be 0
            (Test-Path -LiteralPath (Join-Path $consumer.Parent 'objects/artifacts/provmod__provapp__web-app__prov-target__1.0.0.zip')) | Should -Be $true
        }
        finally { Remove-ProvenanceConsumer -Consumer $consumer }
    }

    It 'Judges only the consumed projectPath, like Check 14, not the whole sibling root' {
        $consumer = New-ProvenanceConsumer -UntrackedSiblingElsewhere
        $report = Join-Path $consumer.Parent 'shared-sources.json'
        try {
            $roots = @(Get-OmpSharedSourceRoots -RepositoryRoot $consumer.Root)
            @(Get-OmpSharedSourceProjectPaths -SharedSource $roots[0] -ComponentKey 'provenance-app') | Should -Be @('SharedProject')

            $result = Invoke-ObjectBuilder -Consumer $consumer -ComponentKey 'provenance-app' -ReportPath $report
            $result.ExitCode | Should -Be 0
            ($result.Output -match 'dirty source tree') | Should -Be $false
            $entries = @(Get-Content -LiteralPath $report -Raw | ConvertFrom-Json | ForEach-Object { $_ })
            $entries.Count | Should -Be 1
            [string]$entries[0].commitSha | Should -Be $consumer.SiblingHead
            [bool]$entries[0].dirty | Should -Be $false
        }
        finally { Remove-ProvenanceConsumer -Consumer $consumer }
    }

    It 'Reports the consumed dirty sibling with -AllowDirtySource' {
        $consumer = New-ProvenanceConsumer -DirtySibling
        $report = Join-Path $consumer.Parent 'shared-sources.json'
        try {
            $result = Invoke-ObjectBuilder -Consumer $consumer -ComponentKey 'provenance-app' -ReportPath $report -AllowDirtySource
            $result.ExitCode | Should -Be 0
            $entries = @(Get-Content -LiteralPath $report -Raw | ConvertFrom-Json | ForEach-Object { $_ })
            $entries.Count | Should -Be 1
            [string]$entries[0].repositoryKey | Should -Be 'provenance-sibling'
            [string]$entries[0].commitSha | Should -Be $consumer.SiblingHead
            [bool]$entries[0].dirty | Should -Be $true
        }
        finally { Remove-ProvenanceConsumer -Consumer $consumer }
    }
}

Describe 'package build provenance gate' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Universal-Package-Provenance.TestHelpers.ps1')
    }

    It 'Fails a build from a dirty tree without -AllowDirtySource' {
        $repo = New-ProvenanceRepo -DirtyEdit
        $outputPath = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-prov-dirty-' + [Guid]::NewGuid().ToString('N') + '.zip')
        try {
            $result = Invoke-ChildScript -ScriptPath $script:ExportScript -Arguments @(
                '-RepositoryRoot', $repo.Root,
                '-OutputPath', $outputPath
            )
            ($result.ExitCode -ne 0) | Should -Be $true
            ($result.Output -match 'dirty source tree') | Should -Be $true
            ($result.Output -match 'AllowDirtySource') | Should -Be $true
        }
        finally {
            Remove-ProvenanceRepo -Repo $repo
            try { Remove-Item -LiteralPath $outputPath -Force -ErrorAction Stop } catch { }
        }
    }

    It 'Passes the gate with -AllowDirtySource and then fails for the missing manifest' {
        $repo = New-ProvenanceRepo -DirtyEdit
        $outputPath = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-prov-allow-' + [Guid]::NewGuid().ToString('N') + '.zip')
        try {
            $result = Invoke-ChildScript -ScriptPath $script:ExportScript -Arguments @(
                '-RepositoryRoot', $repo.Root,
                '-OutputPath', $outputPath,
                '-AllowDirtySource'
            )
            ($result.ExitCode -ne 0) | Should -Be $true
            ($result.Output -match 'dirty source tree') | Should -Be $false
            ($result.Output -match 'Component manifest not found') | Should -Be $true
        }
        finally {
            Remove-ProvenanceRepo -Repo $repo
            try { Remove-Item -LiteralPath $outputPath -Force -ErrorAction Stop } catch { }
        }
    }

    It 'Stamps the commit SHA into the universal package manifest' {
        $outputPath = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-prov-stamp-' + [Guid]::NewGuid().ToString('N') + '.zip')
        try {
            $expectedSha = (git -C $script:RepoRoot rev-parse HEAD | Out-String).Trim()
            $result = Invoke-ChildScript -ScriptPath $script:ExportScript -Arguments @(
                '-RepositoryRoot', $script:RepoRoot,
                '-OutputPath', $outputPath
            )
            ($result.ExitCode -eq 0) | Should -Be $true
            $manifest = Read-UniversalManifest -PackagePath $outputPath
            [string]$manifest.sourceCommitSha | Should -Be $expectedSha
            [bool]$manifest.sourceDirty | Should -Be $false
            [string]$manifest.sourceRepositoryKey | Should -Be 'openmoduleplatform'
        }
        finally {
            try { Remove-Item -LiteralPath $outputPath -Force -ErrorAction Stop } catch { }
        }
    }

    It 'Refuses to replace an existing package file and leaves it untouched' {
        # The Bootstrapper refuses through NoOverwriteFile; the script path
        # used to delete the existing file and write a new one under the
        # same, versioned name.
        $outputPath = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-prov-exists-' + [Guid]::NewGuid().ToString('N') + '.zip')
        try {
            [System.IO.File]::WriteAllText($outputPath, 'existing package', (New-Object System.Text.UTF8Encoding($false)))
            $result = Invoke-ChildScript -ScriptPath $script:ExportScript -Arguments @(
                '-RepositoryRoot', $script:RepoRoot,
                '-OutputPath', $outputPath
            )
            ($result.ExitCode -ne 0) | Should -Be $true
            ($result.Output -match 'already exists and was not replaced') | Should -Be $true
            [System.IO.File]::ReadAllText($outputPath) | Should -Be 'existing package'
        }
        finally {
            try { Remove-Item -LiteralPath $outputPath -Force -ErrorAction Stop } catch { }
        }
    }

    It 'Stamps provenance into artifact manifests and omits it when unknown' {
        $workRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-prov-artifact-' + [Guid]::NewGuid().ToString('N'))
        $payload = Join-Path $workRoot 'payload'
        New-Item -ItemType Directory -Path $payload -Force | Out-Null
        try {
            [System.IO.File]::WriteAllText((Join-Path $payload 'app.txt'), 'payload', (New-Object System.Text.UTF8Encoding($false)))

            $stamped = Join-Path $workRoot 'stamped.zip'
            $result = Invoke-ChildScript -ScriptPath $script:ArtifactPackageScript -Arguments @(
                '-ModuleKey', 'provmod',
                '-AppKey', 'provapp',
                '-PackageType', 'web-app',
                '-TargetName', 'prov-target',
                '-Version', '9.9.9',
                '-PayloadPath', $payload,
                '-OutputPath', $stamped,
                '-SourceRepositoryKey', 'openmoduleplatform',
                '-SourceCommitSha', 'abc123def456',
                '-SourceDirty'
            )
            ($result.ExitCode -eq 0) | Should -Be $true
            $manifest = Read-ArtifactManifest -PackagePath $stamped
            [string]$manifest.sourceCommitSha | Should -Be 'abc123def456'
            [string]$manifest.sourceRepositoryKey | Should -Be 'openmoduleplatform'
            [bool]$manifest.sourceDirty | Should -Be $true

            $unstamped = Join-Path $workRoot 'unstamped.zip'
            $plain = Invoke-ChildScript -ScriptPath $script:ArtifactPackageScript -Arguments @(
                '-ModuleKey', 'provmod',
                '-AppKey', 'provapp',
                '-PackageType', 'web-app',
                '-TargetName', 'prov-target',
                '-Version', '9.9.9',
                '-PayloadPath', $payload,
                '-OutputPath', $unstamped
            )
            ($plain.ExitCode -eq 0) | Should -Be $true
            $plainManifest = Read-ArtifactManifest -PackagePath $unstamped
            # Missing JSON properties must be probed via PSObject: reading them
            # directly throws under StrictMode instead of returning $null.
            ($null -eq $plainManifest.PSObject.Properties['sourceCommitSha']) | Should -Be $true
            ($null -eq $plainManifest.PSObject.Properties['sourceDirty']) | Should -Be $true
        }
        finally {
            try { Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction Stop } catch { }
        }
    }
}

#Requires -Version 5.1
# Pester 5's 'Should -Be/-Not -Be/-Match' parameters are provided by the pinned
# Pester module (5.9.1), not by the inbox Pester 3.4.0 profile the compatibility
# rule measures against; suppress for the whole file, not per assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 5 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
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

    It 'Ignores pure line-ending noise the way content comparisons do' {
        $repo = New-ProvenanceRepo -CrlfOnlyChange
        try {
            $porcelain = (git -C $repo.Root status --porcelain | Out-String).Trim()
            ($porcelain -ne '') | Should -Be $true
            $provenance = Get-OmpSourceProvenance -RepositoryRoot $repo.Root
            $provenance.Dirty | Should -Be $false
        }
        finally { Remove-ProvenanceRepo -Repo $repo }
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

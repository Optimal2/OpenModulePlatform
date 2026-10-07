# Pester 6's 'Should -Be/-Not -Be/-Match' parameters are provided by the pinned
# Pester module (6.1.0), not by the inbox Pester 3.4.0 profile the compatibility
# rule measures against; suppress for the whole file, not per assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()
<#
.SYNOPSIS
Pester tests for deterministic embedded-SQL line endings.

.DESCRIPTION
Covers the two halves of the line-ending contract:

- scripts/dev/embed-module-definition-sql.ps1 normalizes every SQL file to the
  line-ending form .gitattributes declares for it (git check-attr text eol)
  before base64-embedding, instead of embedding the bytes exactly as they lie
  on disk.
- Check 20 in scripts/omp/validate-component-versions.ps1 fails when embedded
  content carries line endings that contradict that declaration.

The failing fixture reproduces the measured incident shape: a SQL file
rewritten to LF by a Git Bash text tool (sed -i) in a repository whose
.gitattributes declares eol=crlf, embedded as-is -- green on the machine that
produced it, failing validate-module-definitions on every normal checkout.

Every test runs in an isolated temporary git repository through the shared
harness in Validate-ComponentVersions.TestHelpers.ps1.
#>

Describe 'Check 20: embedded SQL line endings vs .gitattributes' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Validate-ComponentVersions.TestHelpers.ps1')
        $script:embedToolPath = Resolve-Path (Join-Path $PSScriptRoot '..\scripts\dev\embed-module-definition-sql.ps1')

        function Write-EmbeddedSqlContentAsOnDisk {
            <#
            .SYNOPSIS
            Mimics the PRE-FIX embed behavior: embeds the SQL bytes exactly as
            they lie on disk, with no .gitattributes normalization.
            #>
            param(
                [Parameter(Mandatory = $true)][string]$RepoRoot,
                [Parameter(Mandatory = $true)][string]$DefinitionRelativePath,
                [Parameter(Mandatory = $true)][string]$SqlRelativePath
            )

            $definitionPath = Join-Path $RepoRoot $DefinitionRelativePath
            $sqlText = Get-Content -LiteralPath (Join-Path $RepoRoot $SqlRelativePath) -Raw -Encoding UTF8
            $content = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($sqlText))
            $sha256 = Get-Sha256Hex -Text $sqlText

            $definition = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $definition.sqlScripts[0] | Add-Member -NotePropertyName contentEncoding -NotePropertyValue 'base64-utf8' -Force
            $definition.sqlScripts[0] | Add-Member -NotePropertyName content -NotePropertyValue $content -Force
            $definition.sqlScripts[0] | Add-Member -NotePropertyName sha256 -NotePropertyValue $sha256 -Force
            [System.IO.File]::WriteAllText($definitionPath, ($definition | ConvertTo-Json -Depth 10), [System.Text.UTF8Encoding]::new($false))
        }

        function Save-TextFile {
            param(
                [Parameter(Mandatory = $true)][string]$Path,
                [Parameter(Mandatory = $true)][string]$Content
            )

            [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
        }

        function Invoke-GitCommitAll {
            param(
                [Parameter(Mandatory = $true)][string]$RepoRoot,
                [Parameter(Mandatory = $true)][string]$Message
            )

            & git -C $RepoRoot add -A
            if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }
            & git -C $RepoRoot commit -m $Message --quiet
            if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }
        }

        function Get-EmbeddedSqlText {
            param(
                [Parameter(Mandatory = $true)][string]$RepoRoot,
                [Parameter(Mandatory = $true)][string]$DefinitionRelativePath
            )

            $definition = Get-Content -LiteralPath (Join-Path $RepoRoot $DefinitionRelativePath) -Raw -Encoding UTF8 | ConvertFrom-Json
            return [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([string]$definition.sqlScripts[0].content))
        }
    }

    It 'Fails on the incident shape: LF embedding where .gitattributes declares eol=crlf' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            # The SQL file as 'sed -i' in Git Bash leaves it: LF-only.
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -SqlContent "SELECT 1;`nSELECT 2;`n"
            Save-TextFile -Path (Join-Path $repoRoot '.gitattributes') -Content "*.sql text eol=crlf`n"
            Write-EmbeddedSqlContentAsOnDisk -RepoRoot $repoRoot -DefinitionRelativePath 'TestModule/test.module-definition.json' -SqlRelativePath 'TestModule/sql/init.sql'
            Invoke-GitCommitAll -RepoRoot $repoRoot -Message 'Embed SQL with LF endings under an eol=crlf declaration'

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Not -Be 0
            $result.Output | Should -Match 'wrong line endings'
            $result.Output | Should -Match 'eol=crlf'
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Passes after the embed tool re-embeds the same file with the declared CRLF form' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -SqlContent "SELECT 1;`nSELECT 2;`n"
            Save-TextFile -Path (Join-Path $repoRoot '.gitattributes') -Content "*.sql text eol=crlf`n"
            Write-EmbeddedSqlContentAsOnDisk -RepoRoot $repoRoot -DefinitionRelativePath 'TestModule/test.module-definition.json' -SqlRelativePath 'TestModule/sql/init.sql'
            Invoke-GitCommitAll -RepoRoot $repoRoot -Message 'Embed SQL with LF endings under an eol=crlf declaration'

            & $script:embedToolPath -RepositoryRoot $repoRoot
            if ($LASTEXITCODE -ne 0) { throw "embed tool exited $LASTEXITCODE" }

            # The re-embedded content carries CRLF, matching the declaration...
            $embeddedText = Get-EmbeddedSqlText -RepoRoot $repoRoot -DefinitionRelativePath 'TestModule/test.module-definition.json'
            ([regex]::Matches($embeddedText, "(?<!`r)`n")).Count | Should -Be 0
            ([regex]::Matches($embeddedText, "`r`n")).Count | Should -Be 2

            # ...and once committed, the validator accepts it.
            Invoke-GitCommitAll -RepoRoot $repoRoot -Message 'Re-embed with declared line endings'
            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Be 0
            $result.Output | Should -Match 'carry the line endings'
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Fails when CRLF is embedded where .gitattributes declares eol=lf' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -SqlContent "SELECT 1;`r`nSELECT 2;`r`n"
            Save-TextFile -Path (Join-Path $repoRoot '.gitattributes') -Content "*.sql text eol=lf`n"
            Write-EmbeddedSqlContentAsOnDisk -RepoRoot $repoRoot -DefinitionRelativePath 'TestModule/test.module-definition.json' -SqlRelativePath 'TestModule/sql/init.sql'
            Invoke-GitCommitAll -RepoRoot $repoRoot -Message 'Embed SQL with CRLF endings under an eol=lf declaration'

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Not -Be 0
            $result.Output | Should -Match 'wrong line endings'
            $result.Output | Should -Match 'eol=lf'
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Accepts any line endings when the SQL path is -text (git stores bytes as-is)' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -SqlContent "SELECT 1;`r`nSELECT 2;`r`n"
            Save-TextFile -Path (Join-Path $repoRoot '.gitattributes') -Content "*.sql -text`n"
            Write-EmbeddedSqlContentAsOnDisk -RepoRoot $repoRoot -DefinitionRelativePath 'TestModule/test.module-definition.json' -SqlRelativePath 'TestModule/sql/init.sql'
            Invoke-GitCommitAll -RepoRoot $repoRoot -Message 'Embed SQL under a -text declaration'

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Be 0
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Get-GitDeclaredLineEnding reads crlf/lf/unset from .gitattributes and leaves -text alone' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $null = New-TemporaryTestRepository -RootPath $repoRoot
            Save-TextFile -Path (Join-Path $repoRoot '.gitattributes') -Content "crlf-dir/*.sql text eol=crlf`nlf-dir/*.sql text eol=lf`nasis-dir/*.sql -text`n"

            Get-GitDeclaredLineEnding -RepositoryRoot $repoRoot -RelativePath 'crlf-dir/init.sql' | Should -Be 'CRLF'
            Get-GitDeclaredLineEnding -RepositoryRoot $repoRoot -RelativePath 'lf-dir/init.sql' | Should -Be 'LF'
            Get-GitDeclaredLineEnding -RepositoryRoot $repoRoot -RelativePath 'asis-dir/init.sql' | Should -Be ''
            Get-GitDeclaredLineEnding -RepositoryRoot $repoRoot -RelativePath 'undeclared/init.sql' | Should -Be ''

            ConvertTo-DeclaredLineEndings -Text "a`nb`r`nc" -Declared 'CRLF' | Should -Be "a`r`nb`r`nc"
            ConvertTo-DeclaredLineEndings -Text "a`r`nb`nc" -Declared 'LF' | Should -Be "a`nb`nc"
            ConvertTo-DeclaredLineEndings -Text "a`r`nb" -Declared '' | Should -Be "a`r`nb"
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }
}

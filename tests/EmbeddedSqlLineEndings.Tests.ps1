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

            # git writes advisory noise ('LF will be replaced by CRLF ...') to
            # stderr, and a Windows PowerShell 5.1 native-command stderr line
            # becomes a RemoteException that $ErrorActionPreference = 'Stop'
            # (the harness setting) escalates to a terminating error -- even
            # through 2>&1, where Out-String re-emits the wrapped ErrorRecord.
            # Drop the preference for the two native calls and judge them by
            # $LASTEXITCODE alone, keeping the output for the throw message.
            $previousPreference = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                $addOutput = & git -C $RepoRoot add -A 2>&1 | Out-String
                $addExitCode = $LASTEXITCODE
                $commitOutput = & git -C $RepoRoot commit -m $Message --quiet 2>&1 | Out-String
                $commitExitCode = $LASTEXITCODE
            }
            finally {
                $ErrorActionPreference = $previousPreference
            }
            if ($addExitCode -ne 0) { throw "git add failed: $addOutput" }
            if ($commitExitCode -ne 0) { throw "git commit failed: $commitOutput" }
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

    It 'ConvertTo-DeclaredLineEndings rejects a $null declaration instead of silently reading it as empty' {
        # $null is the unreadable-git-answer sentinel; letting it bind and
        # silently become '' ("git leaves the bytes alone") would turn NO
        # answer into a declaration. The function must refuse it.
        { ConvertTo-DeclaredLineEndings -Text 'a' -Declared $null } | Should -Throw
        # '' stays legal: it IS the declaration "git leaves the bytes alone".
        ConvertTo-DeclaredLineEndings -Text "a`r`nb" -Declared '' | Should -Be "a`r`nb"
    }

    It 'Get-GitDeclaredLineEnding returns $null (not "") when git cannot answer, and the validator fails loudly' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            # No git init: 'git check-attr' cannot answer here. The function
            # must say so with $null, and the validator must turn that into an
            # error -- the old '' fallback silently read as "bytes untouched".
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -NoGit
            # Give the sqlScripts entry embedded content so Checks 16/20 engage.
            Write-EmbeddedSqlContentAsOnDisk -RepoRoot $repoRoot -DefinitionRelativePath 'TestModule/test.module-definition.json' -SqlRelativePath 'TestModule/sql/init.sql'

            $declared = Get-GitDeclaredLineEnding -RepositoryRoot $repoRoot -RelativePath 'TestModule/sql/init.sql'
            ($null -eq $declared) | Should -BeTrue

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Not -Be 0
            $result.Output | Should -Match 'did not answer'
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }
}

Describe 'Check 22: embed tool helper copies are byte-identical to the shared core' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Validate-ComponentVersions.TestHelpers.ps1')
        $script:embedToolSourcePath = Resolve-Path (Join-Path $PSScriptRoot '..\scripts\dev\embed-module-definition-sql.ps1')
    }

    It 'Passes when the embed tool carries the exact shared-core functions' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot
            $embedCopyPath = Join-Path $repoRoot 'scripts\dev\embed-module-definition-sql.ps1'
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $embedCopyPath) -Force
            Copy-Item -LiteralPath $script:embedToolSourcePath -Destination $embedCopyPath -Force

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Be 0
            $result.Output | Should -Match 'byte-identical to the shared core'
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Fails when the embed tool copy drifts from the shared core, even in a comment' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot
            $embedCopyPath = Join-Path $repoRoot 'scripts\dev\embed-module-definition-sql.ps1'
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $embedCopyPath) -Force
            Copy-Item -LiteralPath $script:embedToolSourcePath -Destination $embedCopyPath -Force

            # Drift one byte inside Get-GitDeclaredLineEnding's comment help.
            $drifted = [System.IO.File]::ReadAllText($embedCopyPath).Replace('GIT COULD NOT ANSWER', 'GIT COULD NOT ANSWERX')
            [System.IO.File]::WriteAllText($embedCopyPath, $drifted, [System.Text.UTF8Encoding]::new($false))

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Not -Be 0
            $result.Output | Should -Match 'Check 22'
            $result.Output | Should -Match 'byte-identical'
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Does not print the byte-identical summary line when a copy has drifted' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot
            $embedCopyPath = Join-Path $repoRoot 'scripts\dev\embed-module-definition-sql.ps1'
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $embedCopyPath) -Force
            Copy-Item -LiteralPath $script:embedToolSourcePath -Destination $embedCopyPath -Force

            $drifted = [System.IO.File]::ReadAllText($embedCopyPath).Replace('GIT COULD NOT ANSWER', 'GIT COULD NOT ANSWERX')
            [System.IO.File]::WriteAllText($embedCopyPath, $drifted, [System.Text.UTF8Encoding]::new($false))

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            # The old summary wrote '... are byte-identical to the shared core'
            # on the same run that listed the drift as an error -- a green-
            # looking line next to a red verdict.
            $result.ExitCode | Should -Not -Be 0
            $result.Output | Should -Not -Match 'are byte-identical to the shared core'
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Names the side that is missing the function instead of saying from-both' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot
            $embedCopyPath = Join-Path $repoRoot 'scripts\dev\embed-module-definition-sql.ps1'
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $embedCopyPath) -Force
            Copy-Item -LiteralPath $script:embedToolSourcePath -Destination $embedCopyPath -Force

            # Remove exactly one copied function from the EMBED TOOL: the error
            # must name the embed tool as the side the function could not be
            # extracted from, not blame "both" files.
            $embedText = [System.IO.File]::ReadAllText($embedCopyPath)
            $functionStart = $embedText.IndexOf('function ConvertTo-DeclaredLineEndings')
            $functionEnd = $embedText.IndexOf('function ConvertFrom-JsonDocument')
            if ($functionStart -lt 0 -or $functionEnd -le $functionStart) { throw 'fixture setup failed: function block not found.' }
            [System.IO.File]::WriteAllText($embedCopyPath, $embedText.Remove($functionStart, $functionEnd - $functionStart), [System.Text.UTF8Encoding]::new($false))

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Not -Be 0
            $result.Output | Should -Match ([regex]::Escape("could not extract function 'ConvertTo-DeclaredLineEndings' from scripts/dev/embed-module-definition-sql.ps1"))
            $result.Output | Should -Not -Match 'from both'
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }
}

Describe 'Check 16: embedded sqlScripts freshness summary' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Validate-ComponentVersions.TestHelpers.ps1')

        function Set-EmbeddedSqlContent {
            <#
            .SYNOPSIS
            Gives the fixture's sqlScripts entry base64-utf8 embedded bytes.
            #>
            param(
                [Parameter(Mandatory = $true)][string]$RepoRoot,
                [Parameter(Mandatory = $true)][string]$EmbeddedText,
                [Parameter(Mandatory = $true)][string]$Sha256
            )

            $definitionPath = Join-Path $RepoRoot 'TestModule/test.module-definition.json'
            $definition = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $definition.sqlScripts[0] | Add-Member -NotePropertyName contentEncoding -NotePropertyValue 'base64-utf8' -Force
            $definition.sqlScripts[0] | Add-Member -NotePropertyName content -NotePropertyValue ([Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($EmbeddedText))) -Force
            $definition.sqlScripts[0] | Add-Member -NotePropertyName sha256 -NotePropertyValue $Sha256 -Force
            [System.IO.File]::WriteAllText($definitionPath, ($definition | ConvertTo-Json -Depth 10), [System.Text.UTF8Encoding]::new($false))
        }
    }

    # 2026-10-07: the consumer port of the declared-form Check 16 left the OLD
    # Check 16 block in place after the new one. The old block reset the
    # counters, so the summary printed the check-mark line on a run whose
    # Check 16 had failed, and it read $definition.sqlScripts directly, which
    # throws under Set-StrictMode for a definition without sqlScripts. The
    # platform validator never carried the duplicate, but it shared the
    # unconditional check-mark line; these pins hold the fixed contract.

    It 'Carries exactly one Check 16 freshness block (the counter is initialized once)' {
        $validatorSource = Get-Content -LiteralPath $scriptPath -Raw -Encoding UTF8
        @([regex]::Matches($validatorSource, '(?m)^\$embeddedSqlChecked = 0\r?$')).Count | Should -Be 1
    }

    It 'Prints no check-mark freshness line when the embedded SQL is stale' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot
            Set-EmbeddedSqlContent -RepoRoot $repoRoot -EmbeddedText 'SELECT 2;' -Sha256 '0'

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Not -Be 0
            $result.Output | Should -Match '0 of 1 embedded SQL script\(s\) passed freshness validation \(1 error\(s\)\)'
            $result.Output | Should -Not -Match '1 of 1 embedded SQL script\(s\) passed freshness validation'
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Prints the check-mark freshness line when the embedded SQL is fresh' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot
            # Nothing is declared for the fixture's SQL path, so the historical
            # LF normalization applies on both sides; embedding the on-disk
            # text as-is is fresh under that rule.
            $diskText = Get-Content -LiteralPath (Join-Path $repoRoot 'TestModule\sql\init.sql') -Raw -Encoding UTF8
            Set-EmbeddedSqlContent -RepoRoot $repoRoot -EmbeddedText $diskText -Sha256 (Get-Sha256Hex -Text $diskText)

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Be 0 -Because $result.Output
            $result.Output | Should -Match '1 of 1 embedded SQL script\(s\) passed freshness validation'
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Does not crash under StrictMode when a module definition has no sqlScripts' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot

            $definitionPath = Join-Path $repoRoot 'TestModule/test.module-definition.json'
            $definition = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $definition.PSObject.Properties.Remove('sqlScripts')
            [System.IO.File]::WriteAllText($definitionPath, ($definition | ConvertTo-Json -Depth 10), [System.Text.UTF8Encoding]::new($false))

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Be 0 -Because $result.Output
            $result.Output | Should -Not -Match 'cannot be found on this object'
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }
}

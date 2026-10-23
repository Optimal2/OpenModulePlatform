# Pester 6's 'Should -Be/-Not -Be/-Match' parameters are provided by the pinned
# Pester module (6.1.0), not by the inbox Pester 3.4.0 profile the compatibility
# rule measures against; suppress for the whole file, not per assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()
<#
.SYNOPSIS
Pester tests for the encoding guard in scripts/omp/bump-version.ps1.

.DESCRIPTION
On 2026-10-23 a version bump in a consumer repository turned the U+2019
apostrophe in a module definition's integrity.source into mojibake (bytes
CE 93 C3 87 C3 96: the UTF-8 bytes E2 80 99 decoded as OEM-437 and re-encoded).
The script itself was proven innocent; the corruption came from a shell round
trip around it. The canonical script now reads and writes every JSON file as
UTF-8 without BOM, named explicitly, and re-reads the written bytes to prove
every non-ASCII character is unchanged.

These tests bump a definition holding U+2019, a/a/o with diacritics, e acute
and an em dash -- once under the default console encoding (the shape of the
consumer guard this replaces) and once with the console forced to OEM-437 --
and assert byte-identical non-ASCII content and no BOM. A third test proves
the guard fails loudly when the non-ASCII text would change.
#>

Describe 'Bump-Version: non-ASCII text survives a bump byte-identically' {
    BeforeAll {
        $script:bumpVersion = Resolve-Path (Join-Path $PSScriptRoot '..\scripts\omp\bump-version.ps1')
        $script:utf8NoBom = [Text.UTF8Encoding]::new($false)
        # U+2019, A ring, a/o diaeresis, e acute, em dash.
        $script:probe = "path$([char]0x2019)s $([char]0x00C5)$([char]0x00E4)$([char]0x00F6) $([char]0x00E9) $([char]0x2014)"

        function Get-NonAsciiBytes {
            param([Parameter(Mandatory = $true)][byte[]]$Bytes)

            return (@($Bytes | Where-Object { $_ -gt 127 }) -join ',')
        }

        function New-ProbeRepository {
            param(
                [Parameter(Mandatory = $true)][string]$Root,
                [Parameter(Mandatory = $true)][string]$DefinitionJson
            )

            $null = New-Item -ItemType Directory -Path $Root -Force
            [IO.File]::WriteAllText((Join-Path $Root 'probe-def.json'), $DefinitionJson, $script:utf8NoBom)
            $manifest = (@{
                repositoryKey = 'probe'
                repositoryVersion = '0.0.1'
                components = @()
                moduleDefinitions = @(
                    @{ moduleKey = 'probe'; definitionVersion = '0.0.1'; path = 'probe-def.json' })
            } | ConvertTo-Json -Depth 8) + "`n"
            [IO.File]::WriteAllText((Join-Path $Root 'omp-components.json'), $manifest, $script:utf8NoBom)
        }

        function Get-ProbeDefinitionJson {
            return (@{
                formatVersion = 1
                moduleKey = 'probe'
                definitionVersion = '0.0.1'
                displayName = "Probe $([char]0x2014) $([char]0x00E5)$([char]0x00E4)$([char]0x00F6)"
                integrity = @{ source = $script:probe }
            } | ConvertTo-Json -Depth 8) + "`n"
        }

        function Assert-ProbeBumpPreservesNonAscii {
            param([Parameter(Mandatory = $true)][string]$Root)

            $defPath = Join-Path $Root 'probe-def.json'
            $bytesBefore = [IO.File]::ReadAllBytes($defPath)
            $before = [IO.File]::ReadAllText($defPath, [Text.Encoding]::UTF8)

            $global:LASTEXITCODE = 0
            $output = & $script:bumpVersion -RepositoryRoot $Root -ModuleKey probe -SkipRepositoryVersion 2>&1 | Out-String
            $LASTEXITCODE | Should -Be 0 -Because $output

            $bytesAfter = [IO.File]::ReadAllBytes($defPath)
            $after = [IO.File]::ReadAllText($defPath, [Text.Encoding]::UTF8)
            $after | Should -Match '"definitionVersion":\s*"0\.0\.2"'
            $after | Should -BeLikeExactly "*$($script:probe)*"
            (Get-NonAsciiBytes -Bytes $bytesAfter) | Should -BeExactly (Get-NonAsciiBytes -Bytes $bytesBefore)
            $nonAsciiBefore = -join @($before.ToCharArray() | Where-Object { [int]$_ -gt 127 })
            $nonAsciiAfter = -join @($after.ToCharArray() | Where-Object { [int]$_ -gt 127 })
            $nonAsciiAfter | Should -BeExactly $nonAsciiBefore
            (($bytesAfter[0..2]) -join ',') | Should -Not -Be '239,187,191'
        }
    }

    It 'leaves non-ASCII text byte-identical when bumping a module definition' {
        $root = Join-Path $TestDrive 'default-console'
        New-ProbeRepository -Root $root -DefinitionJson (Get-ProbeDefinitionJson)

        Assert-ProbeBumpPreservesNonAscii -Root $root
    }

    It 'leaves non-ASCII text byte-identical when the console code page is forced to OEM-437' {
        $root = Join-Path $TestDrive 'oem437-console'
        New-ProbeRepository -Root $root -DefinitionJson (Get-ProbeDefinitionJson)

        $oem437 = [Text.Encoding]::GetEncoding(437)
        $savedOutput = [Console]::OutputEncoding
        $savedInput = [Console]::InputEncoding
        $savedPipeline = $global:OutputEncoding
        try {
            [Console]::OutputEncoding = $oem437
            [Console]::InputEncoding = $oem437
            $global:OutputEncoding = $oem437
            # Prove the console really is on 437, so this test cannot pass vacuously.
            [Console]::OutputEncoding.CodePage | Should -Be 437

            Assert-ProbeBumpPreservesNonAscii -Root $root
        }
        finally {
            [Console]::OutputEncoding = $savedOutput
            [Console]::InputEncoding = $savedInput
            $global:OutputEncoding = $savedPipeline
        }
    }

    It 'fails loudly and names the code points when the non-ASCII text would change' {
        # A definition holding U+2019 as a JSON escape decodes to the literal
        # character, so the rewrite would add a non-ASCII character the file did
        # not have. The guard must refuse that rather than write it silently.
        $root = Join-Path $TestDrive 'guard'
        $escaped = (Get-ProbeDefinitionJson).Replace([string][char]0x2019, ([string][char]92 + 'u2019'))
        New-ProbeRepository -Root $root -DefinitionJson $escaped
        $defPath = Join-Path $root 'probe-def.json'
        $bytesBefore = [IO.File]::ReadAllBytes($defPath)

        $global:LASTEXITCODE = 0
        $records = & $script:bumpVersion -RepositoryRoot $root -ModuleKey probe -SkipRepositoryVersion 2>&1
        # Join the record messages, not Out-String: Windows PowerShell 5.1 wraps
        # the rendered error at the console width, splitting the words matched below.
        $output = (@($records | ForEach-Object {
                    if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.Exception.Message } else { [string]$_ }
                })) -join "`n"

        $LASTEXITCODE | Should -Be 1
        $output | Should -Match 'Encoding guard: non-ASCII text'
        $output | Should -Match 'U\+2019'
        # Refused before the write: the definition keeps its original bytes.
        ([IO.File]::ReadAllBytes($defPath) -join ',') | Should -BeExactly ($bytesBefore -join ',')
    }
}

Describe 'Embed-module-definition-sql: non-ASCII text survives an embed byte-identically' {
    BeforeAll {
        $script:embedTool = Resolve-Path (Join-Path $PSScriptRoot '..\scripts\dev\embed-module-definition-sql.ps1')
        $script:utf8NoBom = [Text.UTF8Encoding]::new($false)

        function New-EmbedProbeRepository {
            param([Parameter(Mandatory = $true)][string]$Root)

            $null = New-Item -ItemType Directory -Path $Root -Force
            $previousPreference = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                $initOutput = & git -C $Root init --quiet 2>&1 | Out-String
                $initExitCode = $LASTEXITCODE
            }
            finally {
                $ErrorActionPreference = $previousPreference
            }
            if ($initExitCode -ne 0) { throw "git init failed: $initOutput" }

            [IO.File]::WriteAllText((Join-Path $Root '.gitattributes'), "*.sql text eol=lf`n", $script:utf8NoBom)
            [IO.File]::WriteAllText((Join-Path $Root 'init.sql'), "SELECT 1;`n", $script:utf8NoBom)
            $definition = (@{
                moduleKey = 'probe'
                definitionVersion = '0.0.1'
                displayName = "Probe $([char]0x2014) $([char]0x00C5)$([char]0x00E4)$([char]0x00F6)"
                integrity = @{ source = "path$([char]0x2019)s $([char]0x00E9)" }
                sqlScripts = @(@{ path = 'init.sql' })
            } | ConvertTo-Json -Depth 8) + "`n"
            [IO.File]::WriteAllText((Join-Path $Root 'probe-def.json'), $definition, $script:utf8NoBom)
            $manifest = (@{
                repositoryKey = 'probe'
                repositoryVersion = '0.0.1'
                components = @()
                moduleDefinitions = @(@{ moduleKey = 'probe'; definitionVersion = '0.0.1'; path = 'probe-def.json' })
            } | ConvertTo-Json -Depth 8) + "`n"
            [IO.File]::WriteAllText((Join-Path $Root 'omp-components.json'), $manifest, $script:utf8NoBom)
        }
    }

    It 'embeds SQL without changing any non-ASCII byte when the console code page is forced to OEM-437' {
        $root = Join-Path $TestDrive 'embed-oem437'
        New-EmbedProbeRepository -Root $root
        $defPath = Join-Path $root 'probe-def.json'
        $bytesBefore = [IO.File]::ReadAllBytes($defPath)

        $oem437 = [Text.Encoding]::GetEncoding(437)
        $savedOutput = [Console]::OutputEncoding
        $savedInput = [Console]::InputEncoding
        $savedPipeline = $global:OutputEncoding
        try {
            [Console]::OutputEncoding = $oem437
            [Console]::InputEncoding = $oem437
            $global:OutputEncoding = $oem437
            [Console]::OutputEncoding.CodePage | Should -Be 437

            $null = & $script:embedTool -RepositoryRoot $root
        }
        finally {
            [Console]::OutputEncoding = $savedOutput
            [Console]::InputEncoding = $savedInput
            $global:OutputEncoding = $savedPipeline
        }

        $bytesAfter = [IO.File]::ReadAllBytes($defPath)
        $after = [IO.File]::ReadAllText($defPath, [Text.Encoding]::UTF8)
        $after | Should -Match '"sha256"'
        (@($bytesAfter | Where-Object { $_ -gt 127 }) -join ',') | Should -BeExactly (@($bytesBefore | Where-Object { $_ -gt 127 }) -join ',')
        (($bytesAfter[0..2]) -join ',') | Should -Not -Be '239,187,191'
    }
}

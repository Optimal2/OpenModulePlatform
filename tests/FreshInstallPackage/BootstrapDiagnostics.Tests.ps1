[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Assertions use the repository-pinned Pester 6.1.0 module.')]
param()

Describe 'Bootstrap input diagnostics do not disclose secrets' {
    BeforeAll {
        $script:repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
        . (Join-Path $script:repoRoot 'scripts/deployment/fresh-install-package-helpers.ps1')
        $script:protector = Join-Path $script:repoRoot 'scripts/protect-bootstrap-config-secrets.ps1'
    }

    It '<EntryPoint> rejects invalid JSON without echoing file content' -ForEach @(
        @{ EntryPoint = 'builder' }
        @{ EntryPoint = 'protector' }
    ) {
        $profile = Join-Path $TestDrive $EntryPoint
        New-Item -ItemType Directory -Path $profile -Force | Out-Null
        $path = Join-Path $profile 'bootstrap.json'
        $canary = [Guid]::NewGuid().ToString('N')
        $json = '{"hostAgent":{"serviceAccountPassword":"' + $canary + '"} "invalid":0}'
        Set-Content -LiteralPath $path -Value $json -Encoding UTF8
        $script:diagnosticFailure = $null
        $output = & {
            try {
                if ($EntryPoint -eq 'builder') { Read-ProfileConfig -ProfileFolder $profile }
                else { & $script:protector -Path $path }
            }
            catch {
                $script:diagnosticFailure = $_
                $_ | Format-List * -Force | Out-String -Width 4096
                $_.Exception.ToString()
            }
        } *>&1 | Out-String -Width 4096
        $caught = $script:diagnosticFailure
        # Assert booleans so a failing regression never prints the canary.
        $output.Contains($canary) | Should -BeFalse
        ($null -ne $caught) | Should -BeTrue
        $caught.Exception.Message.Contains($path) | Should -BeTrue
        $caught.Exception.Message.Contains('not valid JSON') | Should -BeTrue
        ((Get-Content -LiteralPath $path -Raw).Trim() -eq $json) | Should -BeTrue
    }

    It '<EntryPoint> rejects encrypted SQL passwords before changing the profile' -ForEach @(
        @{ EntryPoint = 'builder' }
        @{ EntryPoint = 'protector' }
    ) {
        $profile = Join-Path $TestDrive ('sql-' + $EntryPoint)
        New-Item -ItemType Directory -Path $profile -Force | Out-Null
        $path = Join-Path $profile 'bootstrap.json'
        $envelope = 'enc:aesgcm:v1:' + [Convert]::ToBase64String([byte[]]::new(12)) + ':' +
            [Convert]::ToBase64String([Guid]::NewGuid().ToByteArray()) + ':' +
            [Convert]::ToBase64String([byte[]]::new(16))
        $json = '{"hostAgent":null,"sql":{"password":"' + $envelope + '"}}'
        Set-Content -LiteralPath $path -Value $json -Encoding UTF8
        $failure = ''
        try {
            if ($EntryPoint -eq 'builder') { Read-ProfileConfig -ProfileFolder $profile | Out-Null }
            else { & $script:protector -Path $path | Out-Null }
        }
        catch { $failure = $_.Exception.Message }
        $failure.Contains($envelope) | Should -BeFalse
        $failure | Should -Match 'sql.password.*encrypted.*[Ii]ntegrated [Ss]ecurity'
        ((Get-Content -LiteralPath $path -Raw).Trim() -eq $json) | Should -BeTrue
    }
}

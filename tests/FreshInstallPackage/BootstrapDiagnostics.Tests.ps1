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
        # Keep an unrelated parser failure to prove cleanup is scoped to this read.
        try { 'invalid prior input' | ConvertFrom-Json -ErrorAction Stop | Out-Null }
        catch { $priorError = $Error[0] }
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
        $history = @($Error)
        $historyText = & {
            foreach ($record in $history) {
                $record | Format-List * -Force | Out-String -Width 4096
                $record.Exception.ToString()
            }
        } | Out-String -Width 4096
        $caught = $script:diagnosticFailure
        # Assert booleans so a failing regression never prints the canary.
        $historyText.Contains($canary) | Should -BeFalse
        ($history -contains $priorError) | Should -BeTrue
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

Describe 'Bootstrap protection accepts absent optional sections' {
    BeforeAll {
        Set-StrictMode -Version Latest
        $script:repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
        . (Join-Path $script:repoRoot 'scripts/bootstrap-secret-fields.ps1')
        $tokens = $null
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $script:repoRoot 'scripts/protect-bootstrap-config-secrets.ps1'),
            [ref]$tokens, [ref]$parseErrors)
        foreach ($definition in $ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst]
        }, $false)) {
            . ([scriptblock]::Create($definition.Extent.Text))
        }
        $script:samplePath = Join-Path $script:repoRoot 'installer/hosts/sample/bootstrap.json'
    }

    It 'protects the sample with <Omitted> without changing the repository sample' -ForEach @(
        @{ Omitted = 'original sections' }
        @{ Omitted = 'iisAppPoolOverrides' }
        @{ Omitted = 'appSettings' }
        @{ Omitted = 'hostAgent' }
    ) {
        # Only key generation needs a modern .NET API for this empty-password
        # sample. Keep parsing, StrictMode, traversal and serialization real on 5.1.
        Mock New-PortableEncryptionKey { 'base64:' + [Convert]::ToBase64String([byte[]]::new(32)) }
        $original = [System.IO.File]::ReadAllText($script:samplePath)
        $path = Join-Path $TestDrive 'sample-copy.json'
        Copy-Item -LiteralPath $script:samplePath -Destination $path
        if ($Omitted -ne 'original sections') {
            $config = $original | ConvertFrom-Json
            if ($Omitted -eq 'hostAgent') { $config.PSObject.Properties.Remove($Omitted) }
            else { $config.hostAgent.PSObject.Properties.Remove($Omitted) }
            $config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding UTF8
        }
        { Protect-ConfigFile -FilePath $path } | Should -Not -Throw
        $protected = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        ($protected.security.portableEncryptionKey.StartsWith('base64:')) | Should -BeTrue
        ([System.IO.File]::ReadAllText($script:samplePath) -ceq $original) | Should -BeTrue
    }

    It 'runs the complete protection script on the unchanged sample content' -Skip:($PSVersionTable.PSVersion -lt [version]'7.4') {
        $path = Join-Path $TestDrive 'complete-sample-copy.json'
        Copy-Item -LiteralPath $script:samplePath -Destination $path
        { & (Join-Path $script:repoRoot 'scripts/protect-bootstrap-config-secrets.ps1') -Path $path } | Should -Not -Throw
        $protected = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        ($protected.security.portableEncryptionKey.StartsWith('base64:')) | Should -BeTrue
    }
}

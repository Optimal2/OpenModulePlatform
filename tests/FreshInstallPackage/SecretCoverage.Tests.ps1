[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Assertions use the repository-pinned Pester 6.1.0 module.')]
param()

Describe 'Fresh install secret coverage and Windows PowerShell text encoding' {
    BeforeAll {
        Set-StrictMode -Version Latest
        $script:repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
        $script:helpers = Join-Path $script:repoRoot 'scripts/deployment/fresh-install-package-helpers.ps1'
        . $script:helpers
        $script:encrypted = 'enc:aesgcm:v1:AAAAAAAAAAAAAAAA:c2VjcmV0:AAAAAAAAAAAAAAAAAAAAAA=='
    }

    It 'rejects <Field> without disclosing its value' -ForEach @(
        @{ Field = 'hostAgent.iisAppPoolOverrides.web.password'; Json = '{"hostAgent":{"iisAppPoolOverrides":{"web":{"password":"Secret123!"}}}}' }
        @{ Field = 'hostAgent.serviceAppIdentityOverrides.worker.PASSWORD'; Json = '{"hostAgent":{"serviceAppIdentityOverrides":{"worker":{"PASSWORD":"Secret123!"}}}}' }
        @{ Field = 'hostAgent.appSettings.Mail.smtpPASSWORD'; Json = '{"hostAgent":{"appSettings":{"Mail":{"smtpPASSWORD":"Secret123!"}}}}' }
        @{ Field = 'hostAgent.appSettings.Accounts[0].Password'; Json = '{"hostAgent":{"appSettings":{"Accounts":[{"Password":"Secret123!"}]}}}' }
        @{ Field = 'hostAgent.appSettings.ConnectionStrings.Main'; Json = '{"hostAgent":{"appSettings":{"ConnectionStrings":{"Main":"Server=localhost;Password=Secret123!"}}}}' }
        @{ Field = 'sql.connectionString'; Json = '{"sql":{"connectionString":"Server=localhost; pWd = \"Secret123!;more\""}}' }
        @{ Field = 'connectionStrings[0]'; Json = '{"connectionStrings":["Pwd=Secret123!;Server=localhost"]}' }
        @{ Field = 'sql.connectionString'; Json = '{"sql":{"connectionString":"Password=Secret123!;Password="}}' }
        @{ Field = 'sql.connectionString'; Json = '{"sql":{"connectionString":"\"Password\"=Secret123!;Server=localhost"}}' }
        @{ Field = 'sql.connectionString'; Json = '{"sql":{"connectionString":"Password=\"Secret123!"}}' }
        @{ Field = 'hostAgent.serviceAccountPassword'; Json = '{"hostAgent":{"serviceAccountPassword":"enc:aesgcm:v1:Secret123!"}}' }
    ) {
        $profile = Join-Path $TestDrive 'profile'
        New-Item -ItemType Directory -Path $profile -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $profile 'bootstrap.json') -Value $Json -Encoding UTF8
        $failure = $null
        try { Read-ProfileConfig -ProfileFolder $profile | Out-Null }
        catch { $failure = $_.Exception.Message }
        $failure | Should -Not -BeNullOrEmpty
        $failure.Contains($Field) | Should -BeTrue
        $failure | Should -Not -Match 'Secret123'
    }

    It 'rejects malformed encrypted envelopes: <Value>' -ForEach @(
        @{ Value = 'enc:aesgcm:v1:' }
        @{ Value = 'enc:aesgcm:v1:clear text' }
        @{ Value = 'enc:aesgcm:v1:a:b:c' }
        @{ Value = 'enc:aesgcm:v1:AAAAAAAAAAAAAAAA:%%%%:AAAAAAAAAAAAAAAAAAAAAA==' }
        @{ Value = 'enc:aesgcm:v1:AA==:c2VjcmV0:AAAAAAAAAAAAAAAAAAAAAA==' }
        @{ Value = 'enc:aesgcm:v1:AAAAAAAAAAAAAAAA:c2VjcmV0:AA==' }
        @{ Value = 'enc:aesgcm:v1:AAAAAAAAAAAAAAAA:c2VjcmV0:AAAAAAAAAAAAAAAAAAAAAA==:extra' }
    ) {
        Test-ClearTextSecret -Value $Value | Should -BeTrue
    }

    It 'allows empty or correctly encoded secrets throughout the profile' {
        $config = @'
{"sql":{"password":""},"hostAgent":{"iisAppPoolOverrides":{"web":{"password":"ENCRYPTED"}},"serviceAppIdentityOverrides":{"worker":{"password":"ENCRYPTED"}},"appSettings":{"Mail":{"Password":"ENCRYPTED","EmptyPassword":""},"ConnectionStrings":{"Main":"Server=localhost;Pwd=ENCRYPTED","Empty":"Server=localhost;Password=","Integrated":"Server=localhost;Integrated Security=true"}}}}
'@.Replace('ENCRYPTED', $script:encrypted) | ConvertFrom-Json
        (Find-ClearTextPasswordFields -Config $config -ProfileFolder $TestDrive).Count | Should -Be 0
    }

    It 'rejects a malformed encrypted password in package.psd1' {
        Set-Content -LiteralPath (Join-Path $TestDrive 'package.psd1') -Value "@{ SqlPassword = 'enc:aesgcm:v1:Secret123!' }" -Encoding UTF8
        $fields = Find-ClearTextPasswordFields -Config ([pscustomobject]@{}) -ProfileFolder $TestDrive
        $fields | Should -Contain 'package.psd1.SqlPassword'
    }

    It 'shares exactly the bootstrapper portable password fields as writable properties' {
        $config = '{"hostAgent":{"serviceAccountPassword":"one","iisAppPoolPassword":"two","serviceAppPassword":"three","iisAppPoolOverrides":{"web.pool":{"PASSWORD":"four"}},"serviceAppIdentityOverrides":{"worker":{"password":"five"}}}}' | ConvertFrom-Json
        $fields = @(Get-BootstrapPortableSecretFields -Config $config)
        $fields.Count | Should -Be 5
        foreach ($field in $fields) { $field.Property.Value = $script:encrypted }
        (Find-ClearTextPasswordFields -Config $config -ProfileFolder (Join-Path $TestDrive 'empty-profile')).Count | Should -Be 0
        $config.hostAgent.iisAppPoolOverrides.'web.pool'.PASSWORD | Should -Be $script:encrypted
    }

    It 'accepts a real AES-GCM envelope emitted by the protection script' -Skip:($PSVersionTable.PSVersion -lt [version]'7.4') {
        # Encryption requires the modern .NET AES-GCM API used by the existing
        # protection script; the package validator itself also runs on 5.1.
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $script:repoRoot 'scripts/protect-bootstrap-config-secrets.ps1'), [ref]$tokens, [ref]$errors)
        $definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Protect-PortableSecret' }, $true)
        . ([scriptblock]::Create($definition.Extent.Text))
        $key = [byte[]]::new(32)
        $protected = Protect-PortableSecret -Value 'Fixture password' -Key $key
        Test-ClearTextSecret -Value $protected | Should -BeFalse
        Protect-PortableSecret -Value $protected -Key $key | Should -Be $protected
    }

    It 'preserves Swedish characters when both script and README are read with 5.1 BOM-or-ANSI semantics' {
        # Windows PowerShell 5.1 defaults BOM-less source/text to the ANSI code
        # page. Emulate that explicitly so this regression also runs on pwsh.
        $reader = [System.IO.StreamReader]::new($script:helpers, [System.Text.Encoding]::GetEncoding(1252), $true)
        try { $source = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
        $function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Write-FreshInstallReadme' }, $true)
        $readmePath = Join-Path $TestDrive 'README.txt'
        & {
            . ([scriptblock]::Create($function.Extent.Text))
            Write-FreshInstallReadme -Path $readmePath
        }
        $reader = [System.IO.StreamReader]::new($readmePath, [System.Text.Encoding]::GetEncoding(1252), $true)
        try { $readme = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $readme | Should -Match ('p' + [char]0x00e5 + ' en NY server')
        $readme | Should -Match ('Det h' + [char]0x00e4 + 'r paketet')
        $readme | Should -Match ('H' + [char]0x00f6 + 'gerklicka')
        [BitConverter]::ToString([System.IO.File]::ReadAllBytes($readmePath), 0, 3) | Should -Be 'EF-BB-BF'
    }
}

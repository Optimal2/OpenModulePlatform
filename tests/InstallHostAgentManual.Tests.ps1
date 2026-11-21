# Pester assertions are supplied by the repository's pinned module.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 assertions are provided by the pinned module.')]
param()

Describe 'Manual HostAgent installation service and path guards' {
    BeforeAll {
        $script:sourcePath = Join-Path $PSScriptRoot '..\scripts\deployment\install-hostagent-manual.ps1'
        $script:sourceText = [System.IO.File]::ReadAllText($script:sourcePath)
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:sourcePath, [ref]$tokens, [ref]$errors)
        $errors.Count | Should -Be 0
        $script:functionDefinitions = foreach ($name in @(
            'Get-ExistingHostAgentServices',
            'Resolve-ReferenceServiceName',
            'Stop-ServiceIfRunning',
            'Invoke-ScChecked',
            'Disable-OtherServices',
            'Test-BuiltInServiceAccount')) {
            $function = $ast.Find({ param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
            }, $true)
            $function.Extent.Text
        }
    }

    BeforeEach {
        $fixtureScript = Join-Path $TestDrive 'manual-hostagent-functions.ps1'
        [System.IO.File]::WriteAllText($fixtureScript, ($script:functionDefinitions -join [Environment]::NewLine))
        . $fixtureScript
    }

    It 'excludes Sentinel from HostAgent service enumeration' {
        Mock Get-CimInstance {
            @(
                [pscustomobject]@{ Name = 'OMP.HostAgent.1.2.3'; State = 'Running' },
                [pscustomobject]@{ Name = 'OMP.HostAgent.Sentinel'; State = 'Running' }
            )
        }

        $services = @(Get-ExistingHostAgentServices -Prefix 'OMP.HostAgent')

        $services.Name | Should -Be @('OMP.HostAgent.1.2.3')
    }

    It 'does not stop or disable Sentinel with the other HostAgent services' {
        Mock Get-CimInstance {
            @(
                [pscustomobject]@{ Name = 'OMP.HostAgent.1.2.2'; State = 'Running' },
                [pscustomobject]@{ Name = 'OMP.HostAgent.Sentinel'; State = 'Running' }
            )
        }
        Mock Stop-ServiceIfRunning {}
        Mock Invoke-ScChecked {}
        Mock Write-Host {}

        Disable-OtherServices -Prefix 'OMP.HostAgent' -TargetName 'OMP.HostAgent.1.2.3'

        Should -Invoke Stop-ServiceIfRunning -Times 1 -Exactly -ParameterFilter { $Name -eq 'OMP.HostAgent.1.2.2' }
        Should -Invoke Stop-ServiceIfRunning -Times 0 -Exactly -ParameterFilter { $Name -eq 'OMP.HostAgent.Sentinel' }
    }

    It 'rejects Sentinel as an explicit account reference service' {
        { Resolve-ReferenceServiceName -ExplicitName 'OMP.HostAgent.Sentinel' -Prefix 'OMP.HostAgent' -TargetName 'OMP.HostAgent.1.2.3' } |
            Should -Throw '*Sentinel*reference*'
    }

    It 'recognizes built-in service account alias <AccountName> without a password' -ForEach @(
        @{ AccountName = 'LocalSystem' },
        @{ AccountName = '.\LocalSystem' },
        @{ AccountName = 'NT AUTHORITY\SYSTEM' },
        @{ AccountName = 'NT AUTHORITY\LocalService' },
        @{ AccountName = '.\LocalService' },
        @{ AccountName = 'NT AUTHORITY\NetworkService' },
        @{ AccountName = '.\NetworkService' },
        @{ AccountName = 'nt authority\system' }
    ) {
        Test-BuiltInServiceAccount -AccountName $AccountName | Should -BeTrue
    }

    It 'still requires credentials for a non-built-in service account' {
        Test-BuiltInServiceAccount -AccountName 'EXAMPLE\ServiceAccount' | Should -BeFalse
    }

    It 'writes the credential-store setting for the final path rather than staging' {
        $script:sourceText | Should -Match '-TargetInstallPath\s+\$targetInstallPath\s+`'
        $script:sourceText | Should -Not -Match '-TargetInstallPath\s+\$stagingPath\s+`'
    }
}

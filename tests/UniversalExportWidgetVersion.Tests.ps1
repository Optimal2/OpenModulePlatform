# Pester 6's 'Should -Be' parameters are provided by the pinned Pester module
# (6.1.0), not by the inbox Pester 3.4.0 profile the compatibility rule
# measures against; suppress for the whole file, not per assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()

# A dashboard-widget package without packageVersion takes its version from the
# highest widgetVersion it carries. Both universal exporters chose that value
# with a plain string sort, which ranks 0.9.0 above 0.10.0.

Describe 'Universal exporters: dashboard-widget fallback version' -ForEach @(
    @{ ScriptName = 'export-universal-package.ps1' }
    @{ ScriptName = 'export-universal-object-root.ps1' }
) {
    BeforeAll {
        $scriptPath = Join-Path (Split-Path -Parent $PSScriptRoot) (Join-Path 'scripts\omp' $ScriptName)
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$null, [ref]$null)
        foreach ($functionName in @('Get-JsonPropertyValue', 'Get-PortableObjectVersion')) {
            $definition = $ast.Find({ param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -eq $functionName
            }, $true)
            $definition | Should -Not -BeNullOrEmpty -Because "$ScriptName must define $functionName"
            . ([scriptblock]::Create($definition.Extent.Text))
        }

        function Get-WidgetFallbackVersion {
            param([Parameter(Mandatory = $true)][string[]]$WidgetVersions)

            $path = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N') + '.json')
            $document = [ordered]@{
                widgets = @($WidgetVersions | ForEach-Object { [ordered]@{ widgetVersion = $_ } })
            }
            [System.IO.File]::WriteAllText($path, ($document | ConvertTo-Json -Depth 4))
            return Get-PortableObjectVersion -Kind 'dashboard-widget' -Path $path
        }
    }

    It '<ScriptName> ranks 0.10.0 above 0.9.0' {
        Get-WidgetFallbackVersion -WidgetVersions @('0.9.0', '0.10.0') | Should -Be '0.10.0'
    }

    It '<ScriptName> ranks 1.2.10 above 1.2.9 whatever the input order' {
        Get-WidgetFallbackVersion -WidgetVersions @('1.2.10', '1.2.9') | Should -Be '1.2.10'
        Get-WidgetFallbackVersion -WidgetVersions @('1.2.9', '1.2.10') | Should -Be '1.2.10'
    }

    It '<ScriptName> ranks an unparsable text below every version' {
        Get-WidgetFallbackVersion -WidgetVersions @('not-a-version', '0.0.1') | Should -Be '0.0.1'
    }
}

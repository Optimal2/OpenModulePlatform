# Pester 5's 'Should -Be/-Not -Be/-Match' parameters are provided by the pinned
# Pester module (5.9.1), not by the inbox Pester 3.4.0 profile the compatibility
# rule measures against; suppress for the whole file, not per assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 5 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()
<#
.SYNOPSIS
Pester tests for publish-all.ps1's project list against omp-components.json.

.DESCRIPTION
package-hostagent-first.ps1 zips the publish folder of every component in
omp-components.json that declares packageFileTemplate, and it gets those
folders from publish-all.ps1, whose project list is hard-coded. A component
added to the manifest but not to that list breaks every later package build,
and nothing in CI ran the packaging, so the gap surfaced only when a package
was built (the HostAgent Sentinel component, 2026-09). These tests read the
list from the script's AST, so the checked list is the shipping one, and cross
it with the manifest in both directions: each payload component's project
must be published, and each published project must exist.
#>

Describe 'publish-all: project list matches the component manifest' {
    BeforeAll {
        $repositoryRoot = Split-Path -Parent $PSScriptRoot
        $scriptPath = Join-Path $repositoryRoot 'publish-all.ps1'
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
        if ($errors.Count -gt 0) { throw "publish-all.ps1 does not parse: $($errors[0].Message)" }
        $assignment = $ast.Find({
            param($node)
            $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left.Extent.Text -eq '$projects'
        }, $true)
        if ($null -eq $assignment) { throw 'publish-all.ps1 no longer assigns $projects.' }
        $script:publishedProjects = @($assignment.Right.Find({
            param($node)
            $node -is [System.Management.Automation.Language.ArrayExpressionAst]
        }, $true).SubExpression.Statements.PipelineElements.Expression.Elements |
            ForEach-Object { [string]$_.Value })
        $manifest = Get-Content -LiteralPath (Join-Path $repositoryRoot 'omp-components.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $script:payloadComponents = @($manifest.components | Where-Object {
            -not [string]::IsNullOrWhiteSpace([string]$_.packageFileTemplate) -and
            -not [string]::IsNullOrWhiteSpace([string]$_.projectPath)
        })
        $script:repositoryRoot = $repositoryRoot
    }

    It 'Reads a non-empty project list from the script' {
        $script:publishedProjects.Count | Should -BeGreaterThan 0
    }

    It 'Publishes every component that package-hostagent-first.ps1 will zip' {
        $missing = foreach ($component in $script:payloadComponents) {
            $projectPath = [string]$component.projectPath
            $published = $script:publishedProjects | Where-Object {
                $_ -eq $projectPath -or $_.StartsWith($projectPath.TrimEnd('/') + '/', [StringComparison]::OrdinalIgnoreCase)
            }
            if (-not $published) { [string]$component.componentKey }
        }
        @($missing) | Should -BeNullOrEmpty
    }

    It 'Lists only project files that exist' {
        $absent = foreach ($project in $script:publishedProjects) {
            if (-not (Test-Path -LiteralPath (Join-Path $script:repositoryRoot $project) -PathType Leaf)) { $project }
        }
        @($absent) | Should -BeNullOrEmpty
    }
}

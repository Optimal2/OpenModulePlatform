# Shared fixture harness for Validate-ModuleDefinitions.Tests.ps1.
$validatorPath = Join-Path $PSScriptRoot '..\scripts\omp\validate-module-definitions.ps1'

function New-ModuleDefinitionFixture {
    param(
        [Parameter(Mandatory = $true)][string]$RootPath,
        [string[]]$SqlTexts = @(
            'CREATE TABLE [sample].[First] (Id int);',
            'CREATE TABLE [sample].[Second] (Id int);'
        ),
        [string[]]$RequiredTables = @('First', 'Second'),
        [string[]]$ScriptKeys = @('setup-first', 'setup-second')
    )

    $null = New-Item -ItemType Directory -Path $RootPath -Force
    $sqlScripts = @(for ($index = 0; $index -lt $SqlTexts.Count; $index++) {
        $sqlText = $SqlTexts[$index]
        $sqlFile = "setup-$($index + 1).sql"
        [System.IO.File]::WriteAllText((Join-Path $RootPath $sqlFile), $sqlText)
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($sqlText)
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hash = ([BitConverter]::ToString($sha256.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
        }
        finally {
            $sha256.Dispose()
        }
        @{
            key = $ScriptKeys[$index]
            path = $sqlFile
            contentEncoding = 'base64-utf8'
            content = [Convert]::ToBase64String($bytes)
            sha256 = $hash
        }
    })

    $definition = @{
        moduleKey = 'sample'
        definitionVersion = '1.0.0'
        sqlScripts = $sqlScripts
        integrity = @{
            requiredTables = @($RequiredTables | ForEach-Object { @{ schema = 'sample'; name = $_ } })
        }
    }
    $manifest = @{
        moduleDefinitions = @(@{
            moduleKey = 'sample'
            definitionVersion = '1.0.0'
            path = 'sample.module-definition.json'
        })
    }
    [System.IO.File]::WriteAllText((Join-Path $RootPath 'sample.module-definition.json'), ($definition | ConvertTo-Json -Depth 8))
    [System.IO.File]::WriteAllText((Join-Path $RootPath 'omp-components.json'), ($manifest | ConvertTo-Json -Depth 8))
}

function Invoke-ModuleDefinitionValidator {
    param([Parameter(Mandatory = $true)][string]$RootPath)

    # Invoke in this engine so the same tests run on Windows PowerShell 5.1 and pwsh.
    # A successful validator does not set LASTEXITCODE; clear any previous failure.
    $global:LASTEXITCODE = 0
    $output = & $validatorPath -RepositoryRoot $RootPath 6>&1 | Out-String
    $exitCode = $LASTEXITCODE
    Write-Host $output.TrimEnd()
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

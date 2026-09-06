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
        [string[]]$ScriptKeys = @('setup-first', 'setup-second'),
        # Manifest-side knobs: the definition file always says 'sample' / '1.0.0' /
        # 'sample.module-definition.json', so overriding these makes the manifest disagree.
        [string]$ManifestModuleKey = 'sample',
        [string]$ManifestDefinitionVersion = '1.0.0',
        [string]$ManifestDefinitionPath = 'sample.module-definition.json',
        # Embedding-side knobs. -OmitSqlFiles keeps the .sql files off disk;
        # -EmbeddedSqlText embeds (and hashes) other text than the file holds;
        # -Sha256Override leaves the content correct but stamps a stale hash.
        [switch]$OmitSqlFiles,
        [string]$ContentEncoding = 'base64-utf8',
        [string]$EmbeddedSqlText = '',
        [string]$Sha256Override = ''
    )

    $null = New-Item -ItemType Directory -Path $RootPath -Force
    $sqlScripts = @(for ($index = 0; $index -lt $SqlTexts.Count; $index++) {
        $sqlText = $SqlTexts[$index]
        $sqlFile = "setup-$($index + 1).sql"
        if (-not $OmitSqlFiles) {
            [System.IO.File]::WriteAllText((Join-Path $RootPath $sqlFile), $sqlText)
        }
        $embeddedText = $sqlText
        if (-not [string]::IsNullOrEmpty($EmbeddedSqlText)) {
            $embeddedText = $EmbeddedSqlText
        }
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($embeddedText)
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hash = ([BitConverter]::ToString($sha256.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
        }
        finally {
            $sha256.Dispose()
        }
        if (-not [string]::IsNullOrEmpty($Sha256Override)) {
            $hash = $Sha256Override
        }
        @{
            key = $ScriptKeys[$index]
            path = $sqlFile
            contentEncoding = $ContentEncoding
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
            moduleKey = $ManifestModuleKey
            definitionVersion = $ManifestDefinitionVersion
            path = $ManifestDefinitionPath
        })
    }
    [System.IO.File]::WriteAllText((Join-Path $RootPath 'sample.module-definition.json'), ($definition | ConvertTo-Json -Depth 8))
    [System.IO.File]::WriteAllText((Join-Path $RootPath 'omp-components.json'), ($manifest | ConvertTo-Json -Depth 8))
}

function Invoke-ModuleDefinitionValidator {
    param([Parameter(Mandatory = $true)][string]$RootPath)

    # Invoke in this engine so the same tests run on Windows PowerShell 5.1 and pwsh.
    # A successful validator does not set LASTEXITCODE; clear any previous failure.
    # -Width keeps Out-String from wrapping long messages at the host width, which
    # would split the text the assertions match on.
    $global:LASTEXITCODE = 0
    $output = & $validatorPath -RepositoryRoot $RootPath 6>&1 | Out-String -Width 4096
    $exitCode = $LASTEXITCODE
    Write-Host $output.TrimEnd()
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

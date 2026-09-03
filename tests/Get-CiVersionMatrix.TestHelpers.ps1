# Shared setup for Get-CiVersionMatrix.Tests.ps1.
# Dot-sourced from each Describe block's BeforeAll: Pester 5 runs every
# container in a separate session state, so functions and variables defined
# at file scope are not visible inside It blocks.

$ErrorActionPreference = 'Stop'

$script:MatrixScriptPath = Resolve-Path (Join-Path $PSScriptRoot '..\scripts\omp\get-ci-version-matrix.ps1')

function New-TemporaryMatrixRepository {
    <#
    .SYNOPSIS
    Creates a temporary git repository with get-ci-version-matrix.ps1, a
    global.json, and one project per supplied target framework. Returns the
    path to the copied script inside the temp repo.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,

        [Parameter(Mandatory = $false)]
        [string]$SdkVersion = '10.0.200',

        [Parameter(Mandatory = $false)]
        [string]$RollForward = 'latestFeature',

        [Parameter(Mandatory = $false)]
        [string[]]$TargetFrameworks = @('net10.0')
    )

    if (Test-Path -LiteralPath $RootPath -PathType Container) {
        Remove-Item -LiteralPath $RootPath -Recurse -Force
    }

    # Copy the matrix script so its $repoRoot resolves to the temp repo.
    $ompScriptsDir = Join-Path $RootPath 'scripts\omp'
    $null = New-Item -ItemType Directory -Path $ompScriptsDir -Force
    $copiedScript = Join-Path $ompScriptsDir 'get-ci-version-matrix.ps1'
    Copy-Item -LiteralPath $script:MatrixScriptPath -Destination $copiedScript -Force

    $rollForwardJson = ''
    if (-not [string]::IsNullOrWhiteSpace($RollForward)) {
        $rollForwardJson = ",`r`n    `"rollForward`": `"$RollForward`""
    }
    $globalJsonContent = "{`r`n  `"sdk`": {`r`n    `"version`": `"$SdkVersion`"$rollForwardJson`r`n  }`r`n}`r`n"
    [System.IO.File]::WriteAllText((Join-Path $RootPath 'global.json'), $globalJsonContent, [System.Text.Encoding]::UTF8)

    $index = 0
    foreach ($tfm in $TargetFrameworks) {
        $index++
        $projectDir = Join-Path $RootPath "App$index"
        $null = New-Item -ItemType Directory -Path $projectDir -Force
        $csprojContent = "<Project Sdk=`"Microsoft.NET.Sdk`">`r`n  <PropertyGroup>`r`n    <TargetFramework>$tfm</TargetFramework>`r`n  </PropertyGroup>`r`n</Project>`r`n"
        [System.IO.File]::WriteAllText((Join-Path $projectDir "App$index.csproj"), $csprojContent, [System.Text.Encoding]::UTF8)
    }

    # The script enumerates committed projects via git ls-files.
    & git -C $RootPath init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'git init failed.' }
    & git -C $RootPath config user.email 'test@example.com'
    if ($LASTEXITCODE -ne 0) { throw 'git config user.email failed.' }
    & git -C $RootPath config user.name 'Test User'
    if ($LASTEXITCODE -ne 0) { throw 'git config user.name failed.' }
    & git -C $RootPath add -A
    if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }
    & git -C $RootPath commit -m 'Initial commit' --quiet
    if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }

    return $copiedScript
}

function Remove-TemporaryMatrixRepository {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath
    )

    if (Test-Path -LiteralPath $RootPath -PathType Container) {
        Remove-Item -LiteralPath $RootPath -Recurse -Force
    }
}

function Invoke-MatrixScript {
    <#
    .SYNOPSIS
    Runs the matrix script in-process. Returns a hashtable with Threw, the
    caught error message, and the parsed matrix object ($null on failure).
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$ScriptPath,

        [Parameter(Mandatory = $false)]
        [switch]$IncludeScheduled
    )

    $savedGitHubOutput = $env:GITHUB_OUTPUT
    $env:GITHUB_OUTPUT = ''
    try {
        $threw = $false
        $errorMessage = ''
        $matrix = $null
        try {
            if ($IncludeScheduled) {
                $output = & $ScriptPath -IncludeScheduled
            }
            else {
                $output = & $ScriptPath
            }
            $jsonLine = @($output | Where-Object { $_ -is [string] -and $_.StartsWith('{') } | Select-Object -Last 1)
            if ($jsonLine.Count -gt 0) {
                $matrix = $jsonLine[0] | ConvertFrom-Json
            }
        }
        catch {
            $threw = $true
            $errorMessage = $_.Exception.Message
        }
    }
    finally {
        $env:GITHUB_OUTPUT = $savedGitHubOutput
    }

    return @{
        Threw        = $threw
        ErrorMessage = $errorMessage
        Matrix       = $matrix
    }
}

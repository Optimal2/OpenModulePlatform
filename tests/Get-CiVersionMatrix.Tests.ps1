<#
.SYNOPSIS
Pester tests for scripts/omp/get-ci-version-matrix.ps1.

.DESCRIPTION
The CI version matrix is derived from the repository's own version files
(global.json SDK pin + committed target frameworks), and the derivation script
is also the unsupported-version gate: a matrix pointed at a .NET major the
code does not target must fail before any build starts. These tests build
temporary repositories with controlled global.json/.csproj content and verify
both the emitted legs and the gate.

The script is invoked in-process with &, so a thrown gate failure is caught by
the test instead of terminating the Pester host. GITHUB_OUTPUT is cleared
around each invocation: in GitHub Actions that variable exists in every step
and the script would otherwise append its matrix output to the step's output
file.
#>

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

Describe 'get-ci-version-matrix: derivation from repository version files' {
    It 'Emits pinned and latest-band push legs plus the scheduled runtime-floor leg' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $scriptPath = New-TemporaryMatrixRepository -RootPath $repoRoot
            $result = Invoke-MatrixScript -ScriptPath $scriptPath -IncludeScheduled

            $result.Threw | Should Be $false
            $names = @($result.Matrix.include | ForEach-Object { $_.name })
            $names -contains 'sdk-pinned' | Should Be $true
            $names -contains 'sdk-latest-band' | Should Be $true
            $names -contains 'runtime-floor' | Should Be $true

            $pinned = @($result.Matrix.include | Where-Object { $_.name -eq 'sdk-pinned' })[0]
            $pinned.sdk | Should Be '10.0.200'
            $pinned.cadence | Should Be 'push'

            $latest = @($result.Matrix.include | Where-Object { $_.name -eq 'sdk-latest-band' })[0]
            $latest.sdk | Should Be '10.0.x'

            $floor = @($result.Matrix.include | Where-Object { $_.name -eq 'runtime-floor' })[0]
            $floor.runtimeFloor | Should Be '10.0.0'
            $floor.cadence | Should Be 'scheduled'
        }
        finally {
            Remove-TemporaryMatrixRepository -RootPath $repoRoot
        }
    }

    It 'Excludes scheduled legs unless -IncludeScheduled is passed' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $scriptPath = New-TemporaryMatrixRepository -RootPath $repoRoot
            $result = Invoke-MatrixScript -ScriptPath $scriptPath

            $result.Threw | Should Be $false
            $names = @($result.Matrix.include | ForEach-Object { $_.name })
            $names.Count | Should Be 2
            $names -contains 'runtime-floor' | Should Be $false
        }
        finally {
            Remove-TemporaryMatrixRepository -RootPath $repoRoot
        }
    }

    It 'Derives the SDK major and runtime floor from global.json, not a hardcoded list' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $scriptPath = New-TemporaryMatrixRepository -RootPath $repoRoot -SdkVersion '8.0.100' -RollForward 'latestFeature' -TargetFrameworks @('net8.0')
            $result = Invoke-MatrixScript -ScriptPath $scriptPath -IncludeScheduled

            $result.Threw | Should Be $false
            $pinned = @($result.Matrix.include | Where-Object { $_.name -eq 'sdk-pinned' })[0]
            $pinned.sdk | Should Be '8.0.100'
            $pinned.expectedMajor | Should Be '8'
            $floor = @($result.Matrix.include | Where-Object { $_.name -eq 'runtime-floor' })[0]
            $floor.runtimeFloor | Should Be '8.0.0'
        }
        finally {
            Remove-TemporaryMatrixRepository -RootPath $repoRoot
        }
    }
}

Describe 'get-ci-version-matrix: unsupported-version gate' {
    It 'Fails when a project targets a major the pinned SDK does not support' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $scriptPath = New-TemporaryMatrixRepository -RootPath $repoRoot -TargetFrameworks @('net10.0', 'net9.0')
            $result = Invoke-MatrixScript -ScriptPath $scriptPath

            $result.Threw | Should Be $true
            ($result.ErrorMessage -match 'does not support') | Should Be $true
        }
        finally {
            Remove-TemporaryMatrixRepository -RootPath $repoRoot
        }
    }

    It 'Fails when global.json pins an SDK major below the target frameworks' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $scriptPath = New-TemporaryMatrixRepository -RootPath $repoRoot -SdkVersion '9.0.100' -RollForward 'latestFeature' -TargetFrameworks @('net10.0')
            $result = Invoke-MatrixScript -ScriptPath $scriptPath

            $result.Threw | Should Be $true
            ($result.ErrorMessage -match 'does not support') | Should Be $true
        }
        finally {
            Remove-TemporaryMatrixRepository -RootPath $repoRoot
        }
    }

    It 'Fails loudly on a cross-major rollForward policy instead of deriving a wrong matrix' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $scriptPath = New-TemporaryMatrixRepository -RootPath $repoRoot -RollForward 'latestMajor'
            $result = Invoke-MatrixScript -ScriptPath $scriptPath

            $result.Threw | Should Be $true
            ($result.ErrorMessage -match 'not mapped') | Should Be $true
        }
        finally {
            Remove-TemporaryMatrixRepository -RootPath $repoRoot
        }
    }

    It 'Allows netstandard analyzer targets as compiler-loaded exemptions' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $scriptPath = New-TemporaryMatrixRepository -RootPath $repoRoot -TargetFrameworks @('net10.0', 'netstandard2.0')
            $result = Invoke-MatrixScript -ScriptPath $scriptPath

            $result.Threw | Should Be $false
        }
        finally {
            Remove-TemporaryMatrixRepository -RootPath $repoRoot
        }
    }
}

Describe 'get-ci-version-matrix: rollForward mapping' {
    It 'Emits only the pinned leg when rollForward forbids band drift' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $scriptPath = New-TemporaryMatrixRepository -RootPath $repoRoot -RollForward 'disable'
            $result = Invoke-MatrixScript -ScriptPath $scriptPath

            $result.Threw | Should Be $false
            $names = @($result.Matrix.include | ForEach-Object { $_.name })
            $names.Count | Should Be 1
            $names[0] | Should Be 'sdk-pinned'
        }
        finally {
            Remove-TemporaryMatrixRepository -RootPath $repoRoot
        }
    }
}

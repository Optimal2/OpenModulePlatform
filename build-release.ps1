param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Wait-ForKeyPress {
    Write-Host ""
    Write-Host "Press any key to close..." -ForegroundColor DarkGray
    if ($Host.UI.RawUI) {
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    }
    else {
        [void](Read-Host)
    }
}

try {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    Set-Location $scriptRoot

    $solution = Get-ChildItem -Path $scriptRoot -Filter *.slnx -File | Select-Object -First 1
    if (-not $solution) {
        $solution = Get-ChildItem -Path $scriptRoot -Filter *.sln -File | Select-Object -First 1
    }

    if (-not $solution) {
        throw "Could not find a solution file (*.slnx or *.sln) in $scriptRoot"
    }

    Write-Host "Using solution: $($solution.Name)" -ForegroundColor Cyan

    Write-Host "Removing stale build folders..." -ForegroundColor Cyan
    Get-ChildItem -Path $scriptRoot -Directory -Recurse -Force |
        Where-Object { $_.Name -in @("bin", "obj", "i") } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    if (-not $NoRestore) {
        Write-Host "Restoring..." -ForegroundColor Cyan
        dotnet restore $solution.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet restore failed with exit code $LASTEXITCODE"
        }
    }

    Write-Host "Cleaning ($Configuration)..." -ForegroundColor Cyan
    dotnet clean $solution.FullName -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet clean failed with exit code $LASTEXITCODE"
    }

    Write-Host "Building ($Configuration)..." -ForegroundColor Cyan
    dotnet build $solution.FullName -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }

    Write-Host "Done." -ForegroundColor Green
}
catch {
    Write-Host "Build failed: $($_.Exception.Message)" -ForegroundColor Red
    throw
}
finally {
    Wait-ForKeyPress
}

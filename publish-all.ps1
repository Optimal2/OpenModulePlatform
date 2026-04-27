# File: publish-all.ps1
[CmdletBinding()]
param(
    [string]$Root = (Get-Location).Path,
    [string]$Configuration = 'Release',
    [string]$Framework = 'net10.0',
    [string]$OutputRoot = '',
    [string]$Runtime = '',
    [bool]$SelfContained = $false,
    [switch]$Parallel,
    [int]$MaxParallel = 4,
    [switch]$Restore,
    [switch]$CleanOutput
)

$ErrorActionPreference = 'Stop'

function Get-AbsolutePath {
    param([string]$BasePath, [string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

function Get-SolutionPath {
    param([string]$RootPath)

    $preferred = @(
        (Join-Path $RootPath 'OpenModulePlatform.slnx'),
        (Join-Path $RootPath 'OpenModulePlatform.sln')
    )

    foreach ($candidate in $preferred) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    $anySolution = Get-ChildItem -LiteralPath $RootPath -File | Where-Object { $_.Extension -in @('.slnx', '.sln') } | Select-Object -First 1
    if ($null -ne $anySolution) {
        return $anySolution.FullName
    }

    throw 'No solution file (.slnx or .sln) was found in the repository root.'
}

function Invoke-DotnetCommand {
    param(
        [string]$DotnetPath,
        [string[]]$Arguments,
        [string]$WorkingDirectory,
        [string]$LogPath = ''
    )

    $lines = @()
    $exitCode = 0

    & {
        $PSNativeCommandUseErrorActionPreference = $false
        Set-Location $WorkingDirectory
        $lines = & $DotnetPath @Arguments 2>&1 | ForEach-Object { $_.ToString() }
        $exitCode = $LASTEXITCODE
    }

    $text = if ($lines.Count -gt 0) {
        [string]::Join([Environment]::NewLine, $lines)
    }
    else {
        ''
    }

    if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
        $logDirectory = Split-Path -Parent $LogPath
        if (-not [string]::IsNullOrWhiteSpace($logDirectory)) {
            New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
        }

        Set-Content -LiteralPath $LogPath -Value $text -Encoding UTF8
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $text
    }
}

function New-PublishCommand {
    param(
        [string]$ProjectPath,
        [string]$Configuration,
        [string]$Framework,
        [string]$PublishDir,
        [string]$Runtime,
        [bool]$SelfContained,
        [bool]$Restore
    )

    $arguments = @('publish', $ProjectPath, '-c', $Configuration, '-f', $Framework, '-o', $PublishDir, '--nologo', '--verbosity', 'minimal')

    if (-not $Restore) {
        $arguments += '--no-restore'
    }

    if (-not [string]::IsNullOrWhiteSpace($Runtime)) {
        $arguments += '-r'
        $arguments += $Runtime
        $arguments += '--self-contained'
        $arguments += ($SelfContained.ToString().ToLowerInvariant())
    }

    return ,$arguments
}

function Wait-ForJobSlot {
    param([int]$Limit)

    while (($script:jobs | Where-Object { $_.State -eq 'Running' }).Count -ge $Limit) {
        $finished = Wait-Job -Job $script:jobs -Any -Timeout 2
        if ($null -ne $finished) {
            Receive-CompletedJobs
        }
    }
}

function Receive-CompletedJobs {
    $completed = @($script:jobs | Where-Object { $_.State -in @('Completed', 'Failed', 'Stopped') })
    foreach ($job in $completed) {
        try {
            $result = Receive-Job -Job $job -ErrorAction Stop
            $script:results += $result
        }
        catch {
            $script:results += [pscustomobject]@{
                Name = $job.Name
                Project = $job.Name
                ExitCode = 1
                OutputPath = ''
                LogPath = ''
                Success = $false
                Error = $_.Exception.Message
            }
        }
        finally {
            Remove-Job -Job $job -Force | Out-Null
            $script:jobs = @($script:jobs | Where-Object { $_.Id -ne $job.Id })
        }
    }
}

$dotnetCommand = Get-Command dotnet -ErrorAction Stop
$rootPath = [System.IO.Path]::GetFullPath($Root)
$solutionPath = Get-SolutionPath -RootPath $rootPath

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $outputRootPath = Join-Path $rootPath 'artifacts\publish'
}
else {
    $outputRootPath = Get-AbsolutePath -BasePath $rootPath -Path $OutputRoot
}

$projects = @(
    'OpenModulePlatform.Portal/OpenModulePlatform.Portal.csproj',
    'examples/ServiceAppModule/ServiceApp/OpenModulePlatform.Service.ExampleServiceAppModule.csproj',
    'examples/ServiceAppModule/WebApp/OpenModulePlatform.Web.ExampleServiceAppModule.csproj',
    'examples/WebAppBlazorModule/OpenModulePlatform.Web.ExampleWebAppBlazorModule.csproj',
    'examples/WebAppModule/OpenModulePlatform.Web.ExampleWebAppModule.csproj',
    'OpenModulePlatform.Web.iFrameWebAppModule/OpenModulePlatform.Web.iFrameWebAppModule.csproj',
    'examples/WorkerAppModule/WebApp/OpenModulePlatform.Web.ExampleWorkerAppModule.csproj',
    'examples/WorkerAppModule/WorkerApp/OpenModulePlatform.Worker.ExampleWorkerAppModule.csproj',
    'OpenModulePlatform.WorkerManager.WindowsService/OpenModulePlatform.WorkerManager.WindowsService.csproj',
    'OpenModulePlatform.WorkerProcessHost/OpenModulePlatform.WorkerProcessHost.csproj'
)

$publishItems = foreach ($project in $projects) {
    $fullProjectPath = Get-AbsolutePath -BasePath $rootPath -Path $project
    if (-not (Test-Path -LiteralPath $fullProjectPath)) {
        throw "Project file not found: $project"
    }

    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($fullProjectPath)
    $projectOutputPath = Join-Path $outputRootPath $projectName
    $logPath = Join-Path $outputRootPath ($projectName + '.publish.log')

    [pscustomobject]@{
        Name = $projectName
        Project = $fullProjectPath
        OutputPath = $projectOutputPath
        LogPath = $logPath
    }
}

if ($CleanOutput -and (Test-Path -LiteralPath $outputRootPath)) {
    Remove-Item -LiteralPath $outputRootPath -Recurse -Force
}

New-Item -ItemType Directory -Path $outputRootPath -Force | Out-Null

if ($Restore) {
    $restoreLogPath = Join-Path $outputRootPath 'restore.log'
    Write-Host "Restoring solution: $solutionPath"
    $restoreResult = Invoke-DotnetCommand -DotnetPath $dotnetCommand.Source -Arguments @('restore', $solutionPath, '--nologo', '--verbosity', 'minimal') -WorkingDirectory $rootPath -LogPath $restoreLogPath
    if ($restoreResult.ExitCode -ne 0) {
        throw "dotnet restore failed. See log: $restoreLogPath"
    }
}

$script:jobs = @()
$script:results = @()

if ($Parallel) {
    if ($MaxParallel -lt 1) {
        throw 'MaxParallel must be at least 1.'
    }

    foreach ($item in $publishItems) {
        Wait-ForJobSlot -Limit $MaxParallel

        $args = New-PublishCommand -ProjectPath $item.Project -Configuration $Configuration -Framework $Framework -PublishDir $item.OutputPath -Runtime $Runtime -SelfContained $SelfContained -Restore $Restore.IsPresent
        $job = Start-Job -Name $item.Name -ArgumentList $dotnetCommand.Source, $args, $item.Name, $item.Project, $item.OutputPath, $item.LogPath, $rootPath -ScriptBlock {
            param($dotnetPath, $publishArgs, $name, $projectPath, $outputPath, $logPath, $workingRoot)

            $lines = @()
            $exitCode = 0
            Set-Location $workingRoot
            New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

            & {
                $PSNativeCommandUseErrorActionPreference = $false
                $lines = & $dotnetPath @publishArgs 2>&1 | ForEach-Object { $_.ToString() }
                $exitCode = $LASTEXITCODE
            }

            $text = if ($lines.Count -gt 0) {
                [string]::Join([Environment]::NewLine, $lines)
            }
            else {
                ''
            }

            Set-Content -LiteralPath $logPath -Value $text -Encoding UTF8

            [pscustomobject]@{
                Name = $name
                Project = $projectPath
                ExitCode = $exitCode
                OutputPath = $outputPath
                LogPath = $logPath
                Success = ($exitCode -eq 0)
                Error = ''
            }
        }

        $script:jobs += $job
    }

    while ($script:jobs.Count -gt 0) {
        $null = Wait-Job -Job $script:jobs -Any
        Receive-CompletedJobs
    }
}
else {
    foreach ($item in $publishItems) {
        Write-Host "Publishing $($item.Name)..."
        New-Item -ItemType Directory -Path $item.OutputPath -Force | Out-Null
        $args = New-PublishCommand -ProjectPath $item.Project -Configuration $Configuration -Framework $Framework -PublishDir $item.OutputPath -Runtime $Runtime -SelfContained $SelfContained -Restore $Restore.IsPresent
        $publishResult = Invoke-DotnetCommand -DotnetPath $dotnetCommand.Source -Arguments $args -WorkingDirectory $rootPath -LogPath $item.LogPath

        $script:results += [pscustomobject]@{
            Name = $item.Name
            Project = $item.Project
            ExitCode = $publishResult.ExitCode
            OutputPath = $item.OutputPath
            LogPath = $item.LogPath
            Success = ($publishResult.ExitCode -eq 0)
            Error = ''
        }
    }
}

$failed = @($script:results | Where-Object { -not $_.Success })
$ordered = @($script:results | Sort-Object Name)

Write-Host ''
Write-Host 'Publish summary:'
$ordered | Select-Object Name, Success, ExitCode, OutputPath | Format-Table -AutoSize

if ($failed.Count -gt 0) {
    Write-Host ''
    Write-Host 'Failed projects:' -ForegroundColor Red
    $failed | Select-Object Name, ExitCode, LogPath | Format-Table -AutoSize
    throw 'One or more publish operations failed. See the per-project log files under artifacts/publish.'
}

Write-Host ''
Write-Host "All publish operations completed successfully. Output root: $outputRootPath" -ForegroundColor Green

# Shared setup for Universal-Package-Provenance.Tests.ps1.
# Dot-sourced from each Describe block's BeforeAll: Pester 5 runs every
# container in a separate session state, so functions and variables defined
# at file scope are not visible inside It blocks.

$ErrorActionPreference = 'Stop'

$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$script:SourceProvenanceScript = Join-Path $script:RepoRoot 'scripts\omp\source-provenance.ps1'
$script:ExportScript = Join-Path $script:RepoRoot 'scripts\omp\export-universal-package.ps1'
$script:ArtifactPackageScript = Join-Path $script:RepoRoot 'scripts\deployment\new-omp-artifact-package.ps1'

function New-ProvenanceRepo {
    <#
    .SYNOPSIS
        Creates a small throwaway git repository with one committed text file.
        Switches add each kind of working-tree state the clean-tree verdict
        must judge. The fixture sets core.autocrlf=false explicitly so the
        CRLF-only case really exercises the normalization path instead of
        passing trivially through git's own conversion.
    #>
    param(
        [switch]$DirtyEdit,
        [switch]$UntrackedFile,
        [switch]$CrlfOnlyChange
    )

    $root = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-provenance-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root -Force | Out-Null

    & git -C $root init -q
    if ($LASTEXITCODE -ne 0) { throw 'git init failed for the provenance fixture.' }
    & git -C $root config user.email 'provenance-fixture@example.local'
    & git -C $root config user.name 'Provenance Fixture'
    & git -C $root config core.autocrlf false
    & git -C $root config commit.gpgsign false

    $tracked = Join-Path $root 'notes.txt'
    [System.IO.File]::WriteAllText($tracked, "line one`nline two`n", (New-Object System.Text.UTF8Encoding($false)))
    & git -C $root add notes.txt
    & git -C $root commit -q -m 'fixture baseline'
    if ($LASTEXITCODE -ne 0) { throw 'git commit failed for the provenance fixture.' }

    $head = (git -C $root rev-parse HEAD | Out-String).Trim()

    if ($DirtyEdit) {
        [System.IO.File]::AppendAllText($tracked, "uncommitted edit`n", (New-Object System.Text.UTF8Encoding($false)))
    }

    if ($UntrackedFile) {
        [System.IO.File]::WriteAllText((Join-Path $root 'scratch.txt'), "untracked`n", (New-Object System.Text.UTF8Encoding($false)))
    }

    if ($CrlfOnlyChange) {
        $bytes = [System.IO.File]::ReadAllBytes($tracked)
        $text = [System.Text.Encoding]::UTF8.GetString($bytes)
        $crlf = $text.Replace("`r`n", "`n").Replace("`n", "`r`n")
        [System.IO.File]::WriteAllBytes($tracked, [System.Text.Encoding]::UTF8.GetBytes($crlf))
    }

    return @{ Root = $root; Head = $head }
}

function Remove-ProvenanceRepo {
    param([hashtable]$Repo)
    try { Remove-Item -LiteralPath $Repo.Root -Recurse -Force -ErrorAction Stop } catch { }
}

function Invoke-ChildScript {
    <#
    .SYNOPSIS
        Runs a packaging script as its own process and measures the exit code.
        Packaging scripts (and the validators they call) terminate with exit,
        which would kill the test session in-process, so the contract under
        test is the process exit code plus the console output.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Arguments
    )

    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $ScriptPath) + $Arguments
    # A failing child writes its throw to stderr, which arrives here as an
    # ErrorRecord. Under the harness Stop preference that would terminate the
    # measurement instead of letting the exit code speak, so capture it as
    # plain output text (the same reason Invoke-GitCapture restores Continue).
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & powershell.exe @argList 2>&1 | Out-String
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    return @{ ExitCode = $code; Output = "$output" }
}

function Read-UniversalManifest {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entry = $archive.Entries |
            Where-Object { $_.FullName -eq 'omp-universal-package.json' } |
            Select-Object -First 1
        if ($null -eq $entry) { throw "omp-universal-package.json missing in $PackagePath" }

        $reader = [System.IO.StreamReader]::new($entry.Open(), [System.Text.Encoding]::UTF8)
        try {
            return $reader.ReadToEnd() | ConvertFrom-Json
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Read-ArtifactManifest {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entry = $archive.Entries |
            Where-Object { $_.FullName -eq 'omp-artifact-package.json' } |
            Select-Object -First 1
        if ($null -eq $entry) { throw "omp-artifact-package.json missing in $PackagePath" }

        $reader = [System.IO.StreamReader]::new($entry.Open(), [System.Text.Encoding]::UTF8)
        try {
            return $reader.ReadToEnd() | ConvertFrom-Json
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

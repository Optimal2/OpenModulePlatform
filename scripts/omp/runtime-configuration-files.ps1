<#
.SYNOPSIS
Shared runtime configuration file rules for OMP packaging scripts.

.DESCRIPTION
Runtime configuration files (appsettings.json, appsettings.*.json,
odv.site.config.js) must never ship inside an immutable artifact payload. They
belong in the artifact package configuration-files section or in config
overlays so changing configuration never changes the artifact hash.

The canonical rule is RuntimeConfigurationFiles.IsRuntimeConfigurationFileName
in OpenModulePlatform.Artifacts/RuntimeConfigurationFiles.cs. The import-time
validators (HostAgent ArtifactZipImportService, Portal package service, admin
artifact upload) enforce that rule when a package is imported. This helper
mirrors the same rule at package build time so an invalid payload fails the
build instead of the import. scripts/omp/test-runtime-configuration-guard.ps1
verifies that this mirror stays in parity with the canonical C# list.

Artifact configuration files may carry ConnectionStrings only as OMP
placeholders ({{Omp.Json.ConnectionStrings.OmpDb}} and the like). A literal
value is the database the artifact was packaged against, which is wrong on
every other host; Assert-OmpConfigurationFileHasNoLiteralConnectionStrings and
Assert-OmpArtifactPackageConfigurationHasNoLiteralConnectionStrings fail the
build instead.

Dot-source this file from packaging scripts:

    . (Join-Path $PSScriptRoot 'runtime-configuration-files.ps1')
#>

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Test-OmpRuntimeConfigurationFileName {
    [CmdletBinding()]
    [OutputType([bool])]
    param([AllowEmptyString()][string]$FileName)

    # Mirrors RuntimeConfigurationFiles.IsRuntimeConfigurationFileName in
    # OpenModulePlatform.Artifacts (the canonical list). Keep in sync; parity
    # is verified by scripts/omp/test-runtime-configuration-guard.ps1.
    if ([string]::IsNullOrWhiteSpace($FileName)) {
        return $false
    }

    if ([string]::Equals($FileName, 'appsettings.json', [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    if ($FileName.StartsWith('appsettings.', [StringComparison]::OrdinalIgnoreCase) `
            -and $FileName.EndsWith('.json', [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    return [string]::Equals($FileName, 'odv.site.config.js', [StringComparison]::OrdinalIgnoreCase)
}

function Remove-OmpRuntimeConfigurationFilesFromFolder {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)

    Get-ChildItem -LiteralPath $Path -File -Recurse |
        Where-Object { Test-OmpRuntimeConfigurationFileName -FileName $_.Name } |
        Remove-Item -Force
}

function Get-OmpNestedZipEntryNames {
    [CmdletBinding()]
    [OutputType([string[]])]
    param([Parameter(Mandatory = $true)][System.IO.Compression.ZipArchiveEntry]$Entry)

    $names = [System.Collections.Generic.List[string]]::new()
    $stream = $Entry.Open()
    # Spool the nested payload zip to a temp FILE, not a MemoryStream: buffering
    # the whole entry in RAM cost its full size per package and threw "Stream was
    # too long" once a nested payload passed ~2 GB, failing every build/refresh
    # (R4-G3). A FileStream is seekable, so ZipArchive still accepts it.
    $tempFile = [System.IO.Path]::GetTempFileName()
    try {
        $fileStream = [System.IO.FileStream]::new($tempFile, [System.IO.FileMode]::Create, [System.IO.FileAccess]::ReadWrite)
        try {
            $stream.CopyTo($fileStream)
            $fileStream.Position = 0
            $nested = [System.IO.Compression.ZipArchive]::new($fileStream, [System.IO.Compression.ZipArchiveMode]::Read)
            try {
                foreach ($nestedEntry in $nested.Entries) {
                    if (-not [string]::IsNullOrWhiteSpace($nestedEntry.Name)) {
                        $names.Add($nestedEntry.FullName)
                    }
                }
            }
            finally {
                $nested.Dispose()
            }
        }
        finally {
            $fileStream.Dispose()
        }
    }
    finally {
        $stream.Dispose()
        Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue
    }

    return $names.ToArray()
}

function Assert-OmpArtifactPackageHasNoRuntimeConfiguration {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ZipPath,
        [Parameter(Mandatory = $true)][string]$Description
    )

    # Mirrors the import-time rule in ArtifactZipImportService: no payload
    # entry may be a runtime configuration file. At build time the whole
    # artifact zip is scanned (covers legacy whole-zip payloads) plus one level
    # of the nested payload zip used by the current package format
    # (payload/artifact.zip). Configuration-section entries are stored with an
    # index prefix (configuration/000-name.ext) and never match the rule.
    $offenders = [System.Collections.Generic.List[string]]::new()

    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        foreach ($entry in $archive.Entries) {
            if ([string]::IsNullOrWhiteSpace($entry.Name)) {
                continue
            }

            if (Test-OmpRuntimeConfigurationFileName -FileName $entry.Name) {
                $offenders.Add($entry.FullName)
                continue
            }

            # Entry names may use backslashes when the zip was created by
            # Windows PowerShell 5.1 Compress-Archive; normalize before the
            # payload prefix check.
            $normalizedFullName = $entry.FullName.Replace('\', '/')
            if ($normalizedFullName.StartsWith('payload/', [StringComparison]::OrdinalIgnoreCase) `
                    -and $entry.Name.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
                foreach ($nestedName in (Get-OmpNestedZipEntryNames -Entry $entry)) {
                    $nestedFileName = ($nestedName.Split('/') | Select-Object -Last 1)
                    if (Test-OmpRuntimeConfigurationFileName -FileName $nestedFileName) {
                        $offenders.Add(('{0}!{1}' -f $entry.FullName, $nestedName))
                    }
                }
            }
        }
    }
    finally {
        $archive.Dispose()
    }

    if ($offenders.Count -gt 0) {
        throw ("{0} contains runtime configuration file(s): {1}. Put runtime configuration in the artifact package configuration-files section instead." -f $Description, ($offenders -join ', '))
    }
}

function Test-OmpConfigurationPlaceholderValue {
    [CmdletBinding()]
    [OutputType([bool])]
    param([AllowNull()][object]$Value)

    # The whole value must be one OMP placeholder that the HostAgent renders at
    # deployment, e.g. {{Omp.Json.ConnectionStrings.OmpDb}}.
    return ($Value -is [string]) -and [Regex]::IsMatch($Value, '^\{\{Omp\.[A-Za-z0-9_.]+\}\}$')
}

function Get-OmpLiteralConnectionStringName {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Content,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if ([string]::IsNullOrWhiteSpace($Content)) {
        return @()
    }

    try {
        $document = $Content | ConvertFrom-Json
    }
    catch {
        throw ("{0} is not valid JSON, so its ConnectionStrings cannot be verified: {1}" -f $Description, $_.Exception.Message)
    }

    if ($null -eq $document -or $document -isnot [System.Management.Automation.PSCustomObject]) {
        return @()
    }

    # Property lookup is case-insensitive here, matching .NET configuration binding.
    $names = [System.Collections.Generic.List[string]]::new()
    foreach ($section in @($document.PSObject.Properties | Where-Object { $_.Name -eq 'ConnectionStrings' })) {
        if ($null -eq $section.Value) {
            continue
        }

        if ($section.Value -isnot [System.Management.Automation.PSCustomObject]) {
            $names.Add($section.Name)
            continue
        }

        foreach ($entry in @($section.Value.PSObject.Properties)) {
            # An explicit null is not an omitted key: like an empty string it
            # overrides configuration and the HostAgent treats it as foreign.
            if (-not (Test-OmpConfigurationPlaceholderValue -Value $entry.Value)) {
                $names.Add(('{0}:{1}' -f $section.Name, $entry.Name))
            }
        }
    }

    return $names.ToArray()
}

function Assert-OmpConfigurationFileHasNoLiteralConnectionStrings {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ComponentKey,
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Content,
        [string]$Source = ''
    )

    if (-not $RelativePath.Trim().EndsWith('.json', [StringComparison]::OrdinalIgnoreCase)) {
        return
    }

    $description = "Artifact configuration file '$RelativePath' of component '$ComponentKey'"
    if (-not [string]::IsNullOrWhiteSpace($Source)) {
        $description += " (source: $Source)"
    }
    $literalNames = @(Get-OmpLiteralConnectionStringName -Content $Content -Description $description)
    if ($literalNames.Count -gt 0) {
        # Name the keys, never the values: a connection string may carry credentials.
        throw ("{0} sets a literal connection string ({1}). Artifact configuration may only use placeholders such as {{{{Omp.Json.ConnectionStrings.OmpDb}}}}; the HostAgent writes the live OMP connection at deployment, and any other database belongs in a config overlay." -f $description, ($literalNames -join ', '))
    }
}

function Assert-OmpArtifactPackageConfigurationHasNoLiteralConnectionStrings {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ZipPath,
        [Parameter(Mandatory = $true)][string]$ComponentKey
    )

    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        foreach ($entry in $archive.Entries) {
            $normalizedFullName = $entry.FullName.Replace('\', '/')
            if (-not $normalizedFullName.StartsWith('configuration/', [StringComparison]::OrdinalIgnoreCase) `
                    -or -not $entry.Name.EndsWith('.json', [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $reader = [System.IO.StreamReader]::new($entry.Open(), [System.Text.UTF8Encoding]::new($false), $true)
            try {
                $content = $reader.ReadToEnd()
            }
            finally {
                $reader.Dispose()
            }

            Assert-OmpConfigurationFileHasNoLiteralConnectionStrings `
                -ComponentKey $ComponentKey `
                -RelativePath $normalizedFullName `
                -Content $content `
                -Source $ZipPath
        }
    }
    finally {
        $archive.Dispose()
    }
}

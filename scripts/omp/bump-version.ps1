<#
.SYNOPSIS
Bumps OMP repository, component, module-definition, and widget versions.

.DESCRIPTION
This helper edits omp-components.json and, when module definitions are selected,
also updates the referenced module-definition JSON files. When widgets are
selected, it updates both the manifest widget entry and the referenced widget
package JSON. It is intentionally manifest-driven so OMP-compatible repositories
can expose the same command.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$RepositoryRoot = '',
    [string[]]$ComponentKey = @(),
    [switch]$AllComponents,
    [string[]]$ModuleKey = @(),
    [switch]$AllModuleDefinitions,
    [string[]]$WidgetFile = @(),
    [switch]$AllWidgets,
    [switch]$UpdateModuleMinimums,
    [switch]$SkipRepositoryVersion,
    [string]$Part = 'patch',
    [string]$Version = '',
    [switch]$Interactive,
    [switch]$Pause,
    [string]$CascadeFrom = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ValidVersionParts = @('patch', 'minor', 'major')

function Get-ScriptDirectory {
    if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        return $PSScriptRoot
    }

    $scriptPath = $PSCommandPath
    if ([string]::IsNullOrWhiteSpace($scriptPath)) {
        $scriptPath = $MyInvocation.MyCommand.Path
    }

    if ([string]::IsNullOrWhiteSpace($scriptPath)) {
        throw 'Could not resolve script directory. Pass -RepositoryRoot explicitly.'
    }

    return Split-Path -Parent $scriptPath
}

function Wait-ForUser {
    param([switch]$Enabled)

    if ($Enabled) {
        [void](Read-Host 'Press Enter to close')
    }
}

function Resolve-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Test-VersionText {
    param([Parameter(Mandatory = $true)][string]$Value)

    if ($Value -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
        throw "Version must use simplified SemVer-compatible text such as 1.2.3, 1.2.3-beta.1, or 1.2.3+build.5."
    }
}

function ConvertTo-VersionPart {
    param([Parameter(Mandatory = $true)][string]$Value)

    $normalized = $Value.Trim().ToLowerInvariant()
    if ($script:ValidVersionParts -notcontains $normalized) {
        throw "Version part must be one of: $($script:ValidVersionParts -join ', ')."
    }

    return $normalized
}

function Get-BumpedVersion {
    param(
        [Parameter(Mandatory = $true)][string]$CurrentVersion,
        [Parameter(Mandatory = $true)][string]$VersionPart
    )

    if ($CurrentVersion -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
        throw "Cannot bump '$CurrentVersion' automatically. Automatic bumps only support plain major.minor.patch versions such as 1.2.3. Use -Version for prerelease/build metadata or other non-standard text."
    }

    $major = [int]$Matches[1]
    $minor = [int]$Matches[2]
    $patch = [int]$Matches[3]

    switch ($VersionPart) {
        'major' {
            $major += 1
            $minor = 0
            $patch = 0
        }
        'minor' {
            $minor += 1
            $patch = 0
        }
        default {
            $patch += 1
        }
    }

    return "$major.$minor.$patch"
}

function Get-NextVersion {
    param([Parameter(Mandatory = $true)][string]$CurrentVersion)

    if ([string]::IsNullOrWhiteSpace($script:Version)) {
        return Get-BumpedVersion -CurrentVersion $CurrentVersion -VersionPart $script:Part
    }

    Test-VersionText -Value $script:Version
    return $script:Version.Trim()
}

function Get-JsonPropertyValue {
    param(
        [Parameter(Mandatory = $true)][object]$Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Set-JsonProperty {
    param(
        [Parameter(Mandatory = $true)][object]$Object,
        [Parameter(Mandatory = $true)][string]$Name,
        [object]$Value
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
        return
    }

    $property.Value = $Value
}

function Get-JsonIndent {
    param(
        [Parameter(Mandatory = $true)][int]$Level,
        [Parameter(Mandatory = $true)][int]$IndentSize
    )

    return ' ' * ($Level * $IndentSize)
}

function Get-NextJsonNonWhitespaceIndex {
    param(
        [Parameter(Mandatory = $true)][string]$Json,
        [Parameter(Mandatory = $true)][int]$StartIndex
    )

    for ($index = $StartIndex; $index -lt $Json.Length; $index++) {
        if (-not [char]::IsWhiteSpace($Json[$index])) {
            return $index
        }
    }

    return -1
}

function Format-JsonText {
    param(
        [Parameter(Mandatory = $true)][string]$Json,
        [int]$IndentSize = 2
    )

    $builder = [Text.StringBuilder]::new()
    $indent = 0
    $inString = $false
    $isEscaped = $false

    for ($index = 0; $index -lt $Json.Length; $index++) {
        $ch = $Json[$index]

        if ($inString) {
            [void]$builder.Append($ch)
            if ($isEscaped) {
                $isEscaped = $false
            }
            elseif ($ch -eq [char]92) {
                $isEscaped = $true
            }
            elseif ($ch -eq '"') {
                $inString = $false
            }

            continue
        }

        switch ($ch) {
            '"' {
                $inString = $true
                [void]$builder.Append($ch)
            }
            '{' {
                $nextIndex = Get-NextJsonNonWhitespaceIndex -Json $Json -StartIndex ($index + 1)
                if ($nextIndex -ge 0 -and $Json[$nextIndex] -eq '}') {
                    [void]$builder.Append($ch)
                    [void]$builder.Append($Json[$nextIndex])
                    $index = $nextIndex
                    continue
                }

                [void]$builder.Append($ch)
                $indent++
                [void]$builder.Append([Environment]::NewLine)
                [void]$builder.Append((Get-JsonIndent -Level $indent -IndentSize $IndentSize))
            }
            '[' {
                $nextIndex = Get-NextJsonNonWhitespaceIndex -Json $Json -StartIndex ($index + 1)
                if ($nextIndex -ge 0 -and $Json[$nextIndex] -eq ']') {
                    [void]$builder.Append($ch)
                    [void]$builder.Append($Json[$nextIndex])
                    $index = $nextIndex
                    continue
                }

                [void]$builder.Append($ch)
                $indent++
                [void]$builder.Append([Environment]::NewLine)
                [void]$builder.Append((Get-JsonIndent -Level $indent -IndentSize $IndentSize))
            }
            '}' {
                $indent = [Math]::Max(0, $indent - 1)
                [void]$builder.Append([Environment]::NewLine)
                [void]$builder.Append((Get-JsonIndent -Level $indent -IndentSize $IndentSize))
                [void]$builder.Append($ch)
            }
            ']' {
                $indent = [Math]::Max(0, $indent - 1)
                [void]$builder.Append([Environment]::NewLine)
                [void]$builder.Append((Get-JsonIndent -Level $indent -IndentSize $IndentSize))
                [void]$builder.Append($ch)
            }
            ':' {
                [void]$builder.Append(': ')
            }
            ',' {
                [void]$builder.Append($ch)
                [void]$builder.Append([Environment]::NewLine)
                [void]$builder.Append((Get-JsonIndent -Level $indent -IndentSize $IndentSize))
            }
            default {
                if (-not [char]::IsWhiteSpace($ch)) {
                    [void]$builder.Append($ch)
                }
            }
        }
    }

    return $builder.ToString()
}

function Save-JsonFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object]$Value
    )

    $json = $Value | ConvertTo-Json -Depth 50 -Compress
    $formattedJson = Format-JsonText -Json $json
    if ($PSCmdlet.ShouldProcess($Path, 'Write JSON file')) {
        [IO.File]::WriteAllText($Path, $formattedJson + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    }
}

function Get-FileSha256Hex {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "File not found for SHA-256 computation: $Path"
    }

    $stream = [System.IO.FileStream]::new($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        $hash = $sha256.ComputeHash($stream)
        return ([System.BitConverter]::ToString($hash)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

function Update-WebSharedHashManifest {
    <#
    .SYNOPSIS
    Builds OpenModulePlatform.Web.Shared with deterministic settings and writes
    the resulting DLL SHA-256 hash to .webshared-build-hash.txt.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$ProjectPath
    )

    $normalizedProjectPath = $ProjectPath.Replace('\', '/')
    $webSharedProjectPath = 'OpenModulePlatform.Web.Shared/OpenModulePlatform.Web.Shared.csproj'
    if (-not [string]::Equals($normalizedProjectPath, $webSharedProjectPath, [StringComparison]::OrdinalIgnoreCase)) {
        return
    }

    $projectFile = Join-Path $RepositoryRoot $normalizedProjectPath
    if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
        throw "Web.Shared project file was not found: $projectFile"
    }

    Write-Host 'Building OpenModulePlatform.Web.Shared with deterministic settings to refresh the hash manifest...'
    $buildOutputRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-webshared-hash-' + [Guid]::NewGuid().ToString('N'))
    $pathMap = '{0}={1}' -f $RepositoryRoot.TrimEnd('\', '/'), '/_/openmoduleplatform'
    try {
        & dotnet build $projectFile `
            -c Release `
            -o $buildOutputRoot `
            --verbosity minimal `
            -p:ContinuousIntegrationBuild=true `
            -p:Deterministic=true `
            "-p:PathMap=$pathMap" | ForEach-Object { Write-Host $_ }

        if ($LASTEXITCODE -ne 0) {
            throw "Failed to build OpenModulePlatform.Web.Shared. The hash manifest was not updated."
        }

        $dllPath = Join-Path $buildOutputRoot 'OpenModulePlatform.Web.Shared.dll'
        if (-not (Test-Path -LiteralPath $dllPath -PathType Leaf)) {
            throw "Build succeeded but OpenModulePlatform.Web.Shared.dll was not found at '$dllPath'."
        }

        $hash = Get-FileSha256Hex -Path $dllPath
        $manifestPath = Join-Path $RepositoryRoot '.webshared-build-hash.txt'
        if ($PSCmdlet.ShouldProcess($manifestPath, 'Update Web.Shared hash manifest')) {
            [System.IO.File]::WriteAllText($manifestPath, $hash + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
            Write-Host "Updated .webshared-build-hash.txt with Web.Shared hash $hash."
        }
    }
    finally {
        if (Test-Path -LiteralPath $buildOutputRoot) {
            try {
                Remove-Item -LiteralPath $buildOutputRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
            catch {
                # Best-effort cleanup of the temporary deterministic build output.
            }
        }
    }
}

function Convert-KeyInput {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return @()
    }

    return @(
        $Value.Split(',', [StringSplitOptions]::RemoveEmptyEntries) |
            ForEach-Object { $_.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
}

function Get-WidgetFileEntryPath {
    param([object]$Entry)

    if ($Entry -is [string]) {
        return $Entry
    }

    $path = [string](Get-JsonPropertyValue -Object $Entry -Name 'sourcePath')
    if ([string]::IsNullOrWhiteSpace($path)) {
        $path = [string](Get-JsonPropertyValue -Object $Entry -Name 'path')
    }

    return $path
}

function Get-WidgetFileEntryVersion {
    param([object]$Entry)

    if ($Entry -is [string]) {
        return ''
    }

    $version = [string](Get-JsonPropertyValue -Object $Entry -Name 'widgetVersion')
    if ([string]::IsNullOrWhiteSpace($version)) {
        $version = [string](Get-JsonPropertyValue -Object $Entry -Name 'packageVersion')
    }
    if ([string]::IsNullOrWhiteSpace($version)) {
        $version = [string](Get-JsonPropertyValue -Object $Entry -Name 'version')
    }

    return $version
}

function Get-ManifestWidgetFileEntries {
    param(
        [Parameter(Mandatory = $true)][object]$Manifest,
        [string]$RepositoryVersion
    )

    $entries = @((Get-JsonPropertyValue -Object $Manifest -Name 'widgetFiles'))
    $result = [System.Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $entries.Count; $index++) {
        $entry = $entries[$index]
        if ($null -eq $entry) {
            continue
        }

        $path = Get-WidgetFileEntryPath -Entry $entry
        if ([string]::IsNullOrWhiteSpace($path)) {
            continue
        }

        $version = Get-WidgetFileEntryVersion -Entry $entry
        if ([string]::IsNullOrWhiteSpace($version)) {
            $version = $RepositoryVersion
        }

        $result.Add([pscustomobject]@{
            Index = $index
            Entry = $entry
            Path = $path.Trim()
            WidgetVersion = if ([string]::IsNullOrWhiteSpace($version)) { '' } else { $version.Trim() }
        })
    }

    return $result.ToArray()
}

function Set-WidgetFileEntryVersion {
    param(
        [Parameter(Mandatory = $true)][object]$Manifest,
        [Parameter(Mandatory = $true)][object]$WidgetEntry,
        [Parameter(Mandatory = $true)][string]$Version
    )

    if ($WidgetEntry.Entry -is [string]) {
        $replacement = [pscustomobject]@{
            path = $WidgetEntry.Path
            widgetVersion = $Version
        }
        $Manifest.widgetFiles[$WidgetEntry.Index] = $replacement
        return
    }

    Set-JsonProperty -Object $WidgetEntry.Entry -Name 'widgetVersion' -Value $Version
}

function Update-WidgetPackageFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Version
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Widget file was not found: $Path"
    }

    $json = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    Set-JsonProperty -Object $json -Name 'packageVersion' -Value $Version
    foreach ($widget in @((Get-JsonPropertyValue -Object $json -Name 'widgets'))) {
        if ($null -ne $widget) {
            Set-JsonProperty -Object $widget -Name 'widgetVersion' -Value $Version
        }
    }

    if ($PSCmdlet.ShouldProcess($Path, "Set dashboard widget packageVersion/widgetVersion to $Version")) {
        Save-JsonFile -Path $Path -Value $json
    }
}

$exitCode = 0
try {
    $Part = ConvertTo-VersionPart -Value $Part
    $scriptDirectory = Get-ScriptDirectory
    if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
        $RepositoryRoot = (Resolve-Path (Join-Path $scriptDirectory '..\..')).Path
    }

    $repositoryRoot = Resolve-FullPath -Path $RepositoryRoot
    $manifestPath = Join-Path $repositoryRoot 'omp-components.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Component manifest not found: $manifestPath"
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $components = @($manifest.components)
    $moduleDefinitions = @($manifest.moduleDefinitions)
    $repositoryVersion = [string](Get-JsonPropertyValue -Object $manifest -Name 'repositoryVersion')
    $widgetEntries = @(Get-ManifestWidgetFileEntries -Manifest $manifest -RepositoryVersion $repositoryVersion)

    if ($Interactive) {
        Write-Host ''
        Write-Host "Repository: $repositoryRoot"
        Write-Host ''
        Write-Host 'Components:'
        $components | Select-Object componentKey, version, moduleKey | Format-Table -AutoSize

        $componentInput = Read-Host 'Component keys to bump (Enter=all, none=skip artifacts, comma-separated keys)'
        if ([string]::IsNullOrWhiteSpace($componentInput)) {
            $AllComponents = $true
        }
        elseif ($componentInput.Trim().Equals('none', [StringComparison]::OrdinalIgnoreCase)) {
            $ComponentKey = @()
            $AllComponents = $false
        }
        else {
            $ComponentKey = Convert-KeyInput -Value $componentInput
            $AllComponents = $false
        }

        if ($moduleDefinitions.Count -gt 0) {
            Write-Host ''
            Write-Host 'Module definitions:'
            $moduleDefinitions | Select-Object moduleKey, definitionVersion, path | Format-Table -AutoSize

            $moduleInput = Read-Host 'Module definition keys to bump (Enter=none, all=all, comma-separated module keys)'
            if ($moduleInput.Trim().Equals('all', [StringComparison]::OrdinalIgnoreCase)) {
                $AllModuleDefinitions = $true
            }
            elseif (-not [string]::IsNullOrWhiteSpace($moduleInput)) {
                $ModuleKey = Convert-KeyInput -Value $moduleInput
            }

            if ($AllModuleDefinitions -or $ModuleKey.Count -gt 0) {
                $minimumInput = Read-Host 'Update matching component minModuleDefinitionVersion values? (Y/n)'
                $UpdateModuleMinimums = -not $minimumInput.Trim().Equals('n', [StringComparison]::OrdinalIgnoreCase)
            }
        }

        if ($widgetEntries.Count -gt 0) {
            Write-Host ''
            Write-Host 'Dashboard widget files:'
            $widgetEntries | Select-Object Path, WidgetVersion | Format-Table -AutoSize

            $widgetInput = Read-Host 'Widget file paths to bump (Enter=none, all=all, comma-separated paths)'
            if ($widgetInput.Trim().Equals('all', [StringComparison]::OrdinalIgnoreCase)) {
                $AllWidgets = $true
            }
            elseif (-not [string]::IsNullOrWhiteSpace($widgetInput)) {
                $WidgetFile = Convert-KeyInput -Value $widgetInput
            }
        }

        $partInput = Read-Host 'Version part to bump (patch/minor/major, Enter=patch)'
        if (-not [string]::IsNullOrWhiteSpace($partInput)) {
            if (@('patch', 'minor', 'major') -notcontains $partInput.Trim().ToLowerInvariant()) {
                throw "Unsupported version part: $partInput"
            }

            $Part = $partInput.Trim().ToLowerInvariant()
        }
    }

    if ($AllComponents -and $ComponentKey.Count -gt 0) {
        throw 'Use either -AllComponents or -ComponentKey, not both.'
    }

    if ($AllModuleDefinitions -and $ModuleKey.Count -gt 0) {
        throw 'Use either -AllModuleDefinitions or -ModuleKey, not both.'
    }

    if ($AllWidgets -and $WidgetFile.Count -gt 0) {
        throw 'Use either -AllWidgets or -WidgetFile, not both.'
    }

    if (-not [string]::IsNullOrWhiteSpace($CascadeFrom)) {
        if ($AllComponents) {
            throw '-CascadeFrom cannot be used with -AllComponents.'
        }

        if ($AllModuleDefinitions) {
            throw '-CascadeFrom cannot be used with -AllModuleDefinitions.'
        }
    }

    if (-not $AllComponents -and $ComponentKey.Count -eq 0 -and -not $AllModuleDefinitions -and $ModuleKey.Count -eq 0 -and -not $AllWidgets -and $WidgetFile.Count -eq 0 -and -not $Interactive -and [string]::IsNullOrWhiteSpace($CascadeFrom)) {
        $AllComponents = $true
    }

    if ($AllComponents) {
        $selectedComponents = @($components)
    }
    else {
        $selectedComponents = @(foreach ($key in $ComponentKey) {
            $match = @($components | Where-Object { $_.componentKey -eq $key })
            if ($match.Count -ne 1) {
                throw "Component '$key' was not found exactly once in $manifestPath."
            }

            $match[0]
        })
    }

    if ($AllModuleDefinitions) {
        $selectedModuleDefinitions = @($moduleDefinitions)
    }
    else {
        $selectedModuleDefinitions = @(foreach ($key in $ModuleKey) {
            $match = @($moduleDefinitions | Where-Object { $_.moduleKey -eq $key })
            if ($match.Count -ne 1) {
                throw "Module definition '$key' was not found exactly once in $manifestPath."
            }

            $match[0]
        })
    }

    if ($AllWidgets) {
        $selectedWidgets = @($widgetEntries)
    }
    else {
        $selectedWidgets = @(foreach ($path in $WidgetFile) {
            $match = @($widgetEntries | Where-Object { $_.Path -eq $path })
            if ($match.Count -ne 1) {
                throw "Widget file '$path' was not found exactly once in $manifestPath."
            }

            $match[0]
        })
    }

    $selectedComponents = [System.Collections.Generic.List[object]]::new($selectedComponents)

    if (-not [string]::IsNullOrWhiteSpace($CascadeFrom)) {
        $sharedProjects = Get-JsonPropertyValue -Object $manifest -Name 'sharedProjects'
        if ($null -eq $sharedProjects) {
            throw "sharedProjects block not found in $manifestPath. -CascadeFrom requires shared project metadata."
        }

        $matchingSharedProject = @($sharedProjects | Where-Object { [string](Get-JsonPropertyValue -Object $_ -Name 'projectPath') -eq $CascadeFrom })
        if ($matchingSharedProject.Count -ne 1) {
            throw "Shared project '$CascadeFrom' was not found in $manifestPath."
        }

        $consumerKeys = @((Get-JsonPropertyValue -Object $matchingSharedProject[0] -Name 'consumers'))
        if ($consumerKeys.Count -eq 0) {
            Write-Host "Cascade from ${CascadeFrom}: no consumers declared."
        }
        else {
            $cascadeComponents = @(foreach ($key in $consumerKeys) {
                $match = @($components | Where-Object { $_.componentKey -eq $key })
                if ($match.Count -ne 1) {
                    throw "Cascade consumer '$key' was not found exactly once in $manifestPath."
                }

                $match[0]
            })

            $selectedKeys = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($component in $selectedComponents) {
                [void]$selectedKeys.Add([string]$component.componentKey)
            }

            $addedConsumers = [System.Collections.Generic.List[string]]::new()
            foreach ($component in $cascadeComponents) {
                $key = [string]$component.componentKey
                if (-not $selectedKeys.Contains($key)) {
                    [void]$selectedComponents.Add($component)
                    [void]$addedConsumers.Add($key)
                }
            }

            if ($addedConsumers.Count -gt 0) {
                Write-Host "Cascade from ${CascadeFrom}: bumping $($addedConsumers -join ', ')"
            }
            else {
                Write-Host "Cascade from ${CascadeFrom}: all consumers already selected, no additional components to bump."
            }
        }

        Update-WebSharedHashManifest -RepositoryRoot $repositoryRoot -ProjectPath $CascadeFrom
    }

    $updates = [System.Collections.Generic.List[object]]::new()

    $hasSelectedVersionTargets = $selectedComponents.Count -gt 0 -or $selectedModuleDefinitions.Count -gt 0 -or $selectedWidgets.Count -gt 0
    if (-not $SkipRepositoryVersion -and $hasSelectedVersionTargets) {
        $currentRepositoryVersion = [string]$manifest.repositoryVersion
        if ([string]::IsNullOrWhiteSpace($currentRepositoryVersion)) {
            throw 'repositoryVersion is missing. Add it manually or use -SkipRepositoryVersion.'
        }

        $nextRepositoryVersion = Get-NextVersion -CurrentVersion $currentRepositoryVersion
        Set-JsonProperty -Object $manifest -Name 'repositoryVersion' -Value $nextRepositoryVersion
        [void]$updates.Add([pscustomobject]@{
            Item = 'repository'
            Key = [string]$manifest.repositoryKey
            OldVersion = $currentRepositoryVersion
            NewVersion = $nextRepositoryVersion
        })
    }

    foreach ($component in $selectedComponents) {
        $currentVersion = [string]$component.version
        $nextVersion = Get-NextVersion -CurrentVersion $currentVersion
        Set-JsonProperty -Object $component -Name 'version' -Value $nextVersion
        [void]$updates.Add([pscustomobject]@{
            Item = 'component'
            Key = [string]$component.componentKey
            OldVersion = $currentVersion
            NewVersion = $nextVersion
        })
    }

    $moduleVersionByKey = @{}
    foreach ($moduleDefinition in $selectedModuleDefinitions) {
        $currentVersion = [string]$moduleDefinition.definitionVersion
        $nextVersion = Get-NextVersion -CurrentVersion $currentVersion
        Set-JsonProperty -Object $moduleDefinition -Name 'definitionVersion' -Value $nextVersion
        $moduleVersionByKey[[string]$moduleDefinition.moduleKey] = $nextVersion

        $definitionPath = Resolve-FullPath -Path (Join-Path $repositoryRoot ([string]$moduleDefinition.path))
        if (Test-Path -LiteralPath $definitionPath -PathType Leaf) {
            $definitionJson = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8 | ConvertFrom-Json
            Set-JsonProperty -Object $definitionJson -Name 'definitionVersion' -Value $nextVersion
            if ($PSCmdlet.ShouldProcess($definitionPath, "Set definitionVersion to $nextVersion")) {
                Save-JsonFile -Path $definitionPath -Value $definitionJson
            }
        }

        [void]$updates.Add([pscustomobject]@{
            Item = 'module-definition'
            Key = [string]$moduleDefinition.moduleKey
            OldVersion = $currentVersion
            NewVersion = $nextVersion
        })
    }

    if ($UpdateModuleMinimums -and $moduleVersionByKey.Count -gt 0) {
        foreach ($component in $components) {
            $moduleKey = [string]$component.moduleKey
            if (-not $moduleVersionByKey.ContainsKey($moduleKey)) {
                continue
            }

            $oldMinimum = [string]$component.minModuleDefinitionVersion
            $newMinimum = [string]$moduleVersionByKey[$moduleKey]
            Set-JsonProperty -Object $component -Name 'minModuleDefinitionVersion' -Value $newMinimum
            [void]$updates.Add([pscustomobject]@{
                Item = 'component-min-module-definition'
                Key = [string]$component.componentKey
                OldVersion = $oldMinimum
                NewVersion = $newMinimum
            })
        }
    }

    foreach ($widgetEntry in $selectedWidgets) {
        $currentVersion = [string]$widgetEntry.WidgetVersion
        if ([string]::IsNullOrWhiteSpace($currentVersion)) {
            throw "Widget file '$($widgetEntry.Path)' has no widgetVersion and repositoryVersion is missing. Set a version manually or add repositoryVersion."
        }

        $nextVersion = Get-NextVersion -CurrentVersion $currentVersion
        Set-WidgetFileEntryVersion -Manifest $manifest -WidgetEntry $widgetEntry -Version $nextVersion

        $widgetPath = Resolve-FullPath -Path (Join-Path $repositoryRoot ([string]$widgetEntry.Path))
        Update-WidgetPackageFile -Path $widgetPath -Version $nextVersion

        [void]$updates.Add([pscustomobject]@{
            Item = 'dashboard-widget'
            Key = [string]$widgetEntry.Path
            OldVersion = $currentVersion
            NewVersion = $nextVersion
        })
    }

    if ($updates.Count -gt 0 -and $PSCmdlet.ShouldProcess($manifestPath, 'Update OMP component manifest versions')) {
        Save-JsonFile -Path $manifestPath -Value $manifest
    }

    Write-Host ''
    if ($updates.Count -eq 0) {
        Write-Host 'No versions were changed.'
    }
    else {
        $updates | Format-Table -AutoSize
    }

    $generatorPath = Join-Path $repositoryRoot 'scripts\omp\update-module-definition.ps1'
    if ($selectedModuleDefinitions.Count -gt 0 -and (Test-Path -LiteralPath $generatorPath -PathType Leaf)) {
        Write-Warning "This repository has scripts/omp/update-module-definition.ps1. If it hardcodes definitionVersion, update that generator too before regenerating module definitions."
    }
}
catch {
    $exitCode = 1
    Write-Error $_
}
finally {
    Wait-ForUser -Enabled:$Pause
}

if ($exitCode -ne 0) {
    exit $exitCode
}

param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Sha256Hex {
    param([string]$Text)

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha256.ComputeHash($bytes)
        return ([System.BitConverter]::ToString($hash)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function ConvertTo-PortableModuleDefinitionSql {
    param([string]$SqlText)

    # Portal executes embedded module-definition SQL on an already-open
    # connection to the configured OMP database. Strip the historical local
    # development database switch so the same JSON works in every installation.
    return [System.Text.RegularExpressions.Regex]::Replace(
        $SqlText,
        '(?im)^\s*USE\s+\[OpenModulePlatform\]\s*;\s*\r?\n\s*GO\s*(?:--.*)?\s*(?:\r?\n)?',
        '')
}

# Get-GitDeclaredLineEnding and ConvertTo-DeclaredLineEndings are BYTE-IDENTICAL
# copies of the shared validator core
# (scripts/omp/validate-component-versions.helpers.ps1); this script stays
# standalone so it can run in a consumer repository without the validator next
# to it. Never edit one copy without the other: Check 22 in
# scripts/omp/validate-component-versions.ps1 compares the two function texts
# and fails on any difference.
function Get-GitDeclaredLineEnding {
    <#
    .SYNOPSIS
    Returns the line-ending form .gitattributes declares for a repository path:
    'CRLF', 'LF', '' when git leaves the bytes alone (the text attribute is
    unset or no eol attribute applies), or $null when GIT COULD NOT ANSWER
    (git missing, non-zero exit, no output -- for example a repository root
    that is not a git work tree).

    .DESCRIPTION
    The embed tool (scripts/dev/embed-module-definition-sql.ps1) used to embed
    whichever line-ending form the SQL file happened to have on disk, so the
    same module definition held LF bytes on one machine and CRLF bytes on
    another, and a file rewritten by a Git Bash text tool (sed -i writes LF)
    produced an embedding that failed validation on every normal checkout.
    Reading the declared form from git check-attr makes the embedded bytes a
    function of the repository contract, not of the local working tree.

    '' and $null are different answers. '' is a declaration: git stores the
    bytes exactly, so embed and freshness logic must keep them exactly as they
    are -- never a license to normalize. $null is NO answer: every caller must
    treat it as an error (fail validation / abort the embed), because silently
    falling back to "bytes untouched" re-embeds the local accident the
    declared-form read exists to prevent.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $gitPath = $RelativePath -replace '\\', '/'
    $output = $null
    $exitCode = 0
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & git -C $RepositoryRoot check-attr text eol -- $gitPath 2>$null
        $exitCode = $LASTEXITCODE
    }
    catch {
        return $null
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($exitCode -ne 0 -or $null -eq $output) {
        return $null
    }

    $textAttribute = ''
    $eolAttribute = ''
    foreach ($attributeLine in @($output)) {
        if ($attributeLine -match ': text: (\S+)\s*$') {
            $textAttribute = $Matches[1]
        }
        elseif ($attributeLine -match ': eol: (\S+)\s*$') {
            $eolAttribute = $Matches[1]
        }
    }

    # -text (text: unset) means git stores the file byte for byte; no eol applies.
    if ($textAttribute -eq 'unset') {
        return ''
    }

    switch ($eolAttribute) {
        'crlf' { return 'CRLF' }
        'lf' { return 'LF' }
        default { return '' }
    }
}

function ConvertTo-DeclaredLineEndings {
    <#
    .SYNOPSIS
    Normalizes every line ending in $Text to the declared form ('CRLF' or
    'LF'). Any other declaration -- including '' for "git leaves the bytes
    alone" -- returns the text unchanged. $null is NOT a declaration (it is
    the unreadable-git-answer sentinel) and is rejected by [ValidateNotNull()].
    The parameter is deliberately UNTYPED: a [string]-typed parameter coerces
    $null to '' BEFORE the validation attributes run (measured on Windows
    PowerShell 5.1), which silently turned NO answer into "bytes untouched".
    #>
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory = $true)][AllowEmptyString()][ValidateNotNull()]$Declared
    )

    if ($Declared -eq 'CRLF') {
        return ($Text -replace "`r`n", "`n") -replace "`n", "`r`n"
    }

    if ($Declared -eq 'LF') {
        return $Text -replace "`r`n", "`n"
    }

    return $Text
}

function ConvertFrom-JsonDocument {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '', Justification = 'The ConvertFrom-Json -Depth call is guarded at runtime by checking Get-Command for the Depth parameter; on Windows PowerShell 5.1 the fallback branch without -Depth runs.')]
    param(
        [Parameter(Mandatory = $true)][string]$Json,
        [Parameter(Mandatory = $true)][int]$Depth
    )

    $command = Get-Command ConvertFrom-Json
    if ($command.Parameters.ContainsKey('Depth')) {
        return $Json | ConvertFrom-Json -Depth $Depth
    }

    return $Json | ConvertFrom-Json
}

function Get-OptionalPropertyValue {
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

function Resolve-RepositoryPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$BasePath
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

$jsonDepth = 100
$manifestPath = Join-Path $RepositoryRoot 'omp-components.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Component manifest was not found: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$definitionFiles = @()
foreach ($definition in @($manifest.moduleDefinitions)) {
    if ($null -eq $definition) {
        continue
    }

    $relativePath = [string]$definition.path
    if ([string]::IsNullOrWhiteSpace($relativePath)) {
        throw "Module definition entry in '$manifestPath' is missing path."
    }

    $definitionPath = Resolve-RepositoryPath -Path $relativePath -BasePath $RepositoryRoot
    if (-not (Test-Path -LiteralPath $definitionPath -PathType Leaf)) {
        throw "Module definition file was not found: $definitionPath"
    }

    $definitionFiles += Get-Item -LiteralPath $definitionPath
}

$definitionFiles = $definitionFiles | Sort-Object FullName

foreach ($definitionFile in $definitionFiles) {
    $jsonText = Get-Content -LiteralPath $definitionFile.FullName -Raw -Encoding UTF8
    $document = ConvertFrom-JsonDocument -Json $jsonText -Depth $jsonDepth

    # runtimeMaintenance.steps carry SQL in the same fields as sqlScripts and are
    # embedded the same way (docs/MODULE_DEFINITIONS.md, "Runtime maintenance steps").
    $sqlEntries = @()
    $sqlScripts = Get-OptionalPropertyValue -Object $document -Name 'sqlScripts'
    if ($null -ne $sqlScripts) {
        $sqlEntries += @($sqlScripts)
    }

    $runtimeMaintenance = Get-OptionalPropertyValue -Object $document -Name 'runtimeMaintenance'
    if ($null -ne $runtimeMaintenance) {
        $runtimeSteps = Get-OptionalPropertyValue -Object $runtimeMaintenance -Name 'steps'
        if ($null -ne $runtimeSteps) {
            $sqlEntries += @($runtimeSteps)
        }
    }

    if ($sqlEntries.Count -eq 0) {
        continue
    }

    $changed = $false
    foreach ($script in $sqlEntries) {
        if ([string]::IsNullOrWhiteSpace([string]$script.path)) {
            continue
        }

        $sqlPath = Join-Path $RepositoryRoot ([string]$script.path)
        if (-not (Test-Path -LiteralPath $sqlPath)) {
            Write-Warning "Skipping missing SQL script referenced by $($definitionFile.Name): $($script.path)"
            continue
        }

        $sqlText = Get-Content -LiteralPath $sqlPath -Raw -Encoding UTF8
        # Normalize to the line-ending form .gitattributes declares for the
        # file before embedding. Embedding the bytes exactly as they lie on
        # disk made the result depend on the local checkout (core.autocrlf) or
        # on whatever tool last rewrote the file (sed -i writes LF), so the
        # same definition held different bytes on different machines.
        $declaredLineEnding = Get-GitDeclaredLineEnding -RepositoryRoot $RepositoryRoot -RelativePath ([string]$script.path)
        if ($null -eq $declaredLineEnding) {
            throw "Could not determine the line-ending form .gitattributes declares for '$($script.path)': 'git check-attr text eol' did not answer (is '$RepositoryRoot' a git work tree?). Refusing to embed the on-disk bytes as-is -- that is exactly the local-checkout dependence this tool exists to remove."
        }
        $sqlText = ConvertTo-DeclaredLineEndings -Text $sqlText -Declared $declaredLineEnding
        $sqlText = ConvertTo-PortableModuleDefinitionSql -SqlText $sqlText
        $contentEncoding = 'base64-utf8'
        $content = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($sqlText))
        $sha256 = Get-Sha256Hex -Text $sqlText

        if ([string](Get-OptionalPropertyValue -Object $script -Name 'contentEncoding') -eq $contentEncoding `
            -and [string](Get-OptionalPropertyValue -Object $script -Name 'content') -eq $content `
            -and [string](Get-OptionalPropertyValue -Object $script -Name 'sha256') -eq $sha256) {
            continue
        }

        $script | Add-Member -NotePropertyName contentEncoding -NotePropertyValue 'base64-utf8' -Force
        $script | Add-Member -NotePropertyName content -NotePropertyValue $content -Force
        $script | Add-Member -NotePropertyName sha256 -NotePropertyValue $sha256 -Force
        $changed = $true
    }

    if ($changed) {
        $updated = $document | ConvertTo-Json -Depth $jsonDepth
        [System.IO.File]::WriteAllText($definitionFile.FullName, $updated + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
        Write-Host "Embedded SQL in $($definitionFile.Name)"
    }
}

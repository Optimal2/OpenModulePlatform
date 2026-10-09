# One encoding implementation for both PowerShell packers and the C# artifact writer.
if (-not ('OpenModulePlatform.Artifacts.DeterministicArtifactEncoding' -as [type])) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $source = Join-Path $PSScriptRoot 'DeterministicArtifactEncoding.cs'
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        Add-Type -Path $source -ReferencedAssemblies System.IO.Compression, System.IO.Compression.FileSystem
    }
    else {
        Add-Type -Path $source
    }
}

function Write-OmpCanonicalJson {
    param([string]$Path, [object]$Value)
    [OpenModulePlatform.Artifacts.DeterministicArtifactEncoding]::WriteJson($Path, $Value)
}

function Compress-OmpDeterministicDirectory {
    param([string]$Source, [string]$Destination)
    [OpenModulePlatform.Artifacts.DeterministicArtifactEncoding]::WriteDirectory($Source, $Destination)
}

function ConvertTo-OmpDeterministicZip {
    param([string]$Source, [string]$Destination)
    [OpenModulePlatform.Artifacts.DeterministicArtifactEncoding]::NormalizeZip($Source, $Destination)
}

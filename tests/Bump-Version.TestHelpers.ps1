# Shared setup for Bump-Version.Tests.ps1.
# Dot-sourced from each Describe block's BeforeAll: Pester 6 runs every
# container in a separate session state, so functions and variables defined
# at file scope are not visible inside It blocks.

$ErrorActionPreference = 'Stop'

$scriptPath = Resolve-Path (Join-Path $PSScriptRoot '..\scripts\omp\bump-version.ps1')

function New-TemporaryBumpRepository {
    <#
    .SYNOPSIS
    Creates a temporary repository with bump-version.ps1, a minimal
    omp-components.json, a module definition, and a fake .csproj project.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,

        [Parameter(Mandatory = $false)]
        [string]$ComponentVersion = '1.0.0',

        [Parameter(Mandatory = $false)]
        [string]$CompatibleArtifactMaxVersion = ''
    )

    if (Test-Path -LiteralPath $RootPath -PathType Container) {
        Remove-Item -LiteralPath $RootPath -Recurse -Force
    }

    $null = New-Item -ItemType Directory -Path $RootPath -Force

    # Copy the bump script so its $repositoryRoot resolves to the temp repo.
    $ompScriptsDir = Join-Path $RootPath 'scripts\omp'
    $null = New-Item -ItemType Directory -Path $ompScriptsDir -Force
    Copy-Item -LiteralPath $scriptPath -Destination (Join-Path $ompScriptsDir 'bump-version.ps1') -Force

    # Create component project.
    $projectDir = Join-Path $RootPath 'TestApp'
    $null = New-Item -ItemType Directory -Path $projectDir -Force
    $csprojContent = "<Project Sdk=`"Microsoft.NET.Sdk`">`r`n  <PropertyGroup>`r`n    <TargetFramework>net8.0</TargetFramework>`r`n  </PropertyGroup>`r`n</Project>`r`n"
    [System.IO.File]::WriteAllText((Join-Path $projectDir 'TestApp.csproj'), $csprojContent, [System.Text.Encoding]::UTF8)

    # Create module definition.
    $moduleDir = Join-Path $RootPath 'TestModule'
    $null = New-Item -ItemType Directory -Path $moduleDir -Force

    $compatibleArtifact = @{
        appKey = 'test_app'
        packageType = 'web-app'
        targetName = 'test-app'
        relativePathTemplate = 'test-app/web/{version}'
        minVersion = '1.0.0'
    }
    if (-not [string]::IsNullOrWhiteSpace($CompatibleArtifactMaxVersion)) {
        $compatibleArtifact['maxVersion'] = $CompatibleArtifactMaxVersion
    }

    $moduleDefinition = @{
        moduleKey = 'test_module'
        definitionVersion = '1.0.0'
        compatibleArtifacts = @($compatibleArtifact)
        sqlScripts = @()
    }

    $moduleDefinitionJson = $moduleDefinition | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText((Join-Path $moduleDir 'test.module-definition.json'), $moduleDefinitionJson, [System.Text.Encoding]::UTF8)

    # Create the component manifest.
    $manifest = @{
        repositoryVersion = '1.0.0'
        repositoryKey = 'testrepo'
        moduleDefinitions = @(
            @{
                moduleKey = 'test_module'
                definitionVersion = '1.0.0'
                path = 'TestModule/test.module-definition.json'
            }
        )
        components = @(
            @{
                componentKey = 'test_app'
                appKey = 'test_app'
                moduleKey = 'test_module'
                version = $ComponentVersion
                projectPath = 'TestApp/TestApp.csproj'
            }
        )
    }
    $manifestJson = $manifest | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText((Join-Path $RootPath 'omp-components.json'), $manifestJson, [System.Text.Encoding]::UTF8)

    return (Join-Path $ompScriptsDir 'bump-version.ps1')
}

function Remove-TemporaryBumpRepository {
    <#
    .SYNOPSIS
    Removes a temporary repository created by New-TemporaryBumpRepository.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath
    )

    if (Test-Path -LiteralPath $RootPath -PathType Container) {
        Remove-Item -LiteralPath $RootPath -Recurse -Force
    }
}

function Invoke-BumpVersion {
    <#
    .SYNOPSIS
    Runs the helper in an isolated runspace and returns its real exit code.
    Use the current engine so a PowerShell 7 run does not silently test 5.1.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$BumpScriptPath,
        [Parameter(Mandatory = $false)][string]$ComponentKey = 'test_app'
    )

    $result = Invoke-BumpVersionWithFakeHost -BumpScriptPath $BumpScriptPath -Parameters @{ ComponentKey = @($ComponentKey) }
    return $result.ExitCode
}

function Invoke-BumpVersionScript {
    <#
    .SYNOPSIS
    Runs the helper with named parameters in the current engine's isolated
    runspace, capturing exit, host output and errors without a child shell.

    .DESCRIPTION
    The runspace owns the fake host, so no confirmation can ever reach a real
    console and no test can wait for a human. The exit code is read back from
    the runspace session state instead of from the ambient $LASTEXITCODE,
    which Pester 6's session-state isolation does not cover.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$BumpScriptPath,
        [Parameter(Mandatory = $false)][hashtable]$Parameters = @{}
    )

    $result = Invoke-BumpVersionWithFakeHost -BumpScriptPath $BumpScriptPath -Parameters $Parameters
    return [pscustomobject]@{
        ExitCode = $result.ExitCode
        Output = (@($result.Output) + @($result.HostOutput) + @($result.Errors)) -join "`n"
    }
}

function Get-BumpVersionFileHash {
    <#
    .SYNOPSIS
    SHA-256 of a file as base64. Hash via .NET rather than Get-FileHash: on a
    workstation whose PSModulePath also lists PowerShell 7 module folders,
    Windows PowerShell 5.1 loads a hybrid Utility module without Get-FileHash.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    return [Convert]::ToBase64String([System.Security.Cryptography.SHA256]::Create().ComputeHash([System.IO.File]::ReadAllBytes($Path)))
}

function Get-BumpVersionTreeHash {
    <#
    .SYNOPSIS
    SHA-256 of every file below a directory, keyed by the relative path. Used
    to prove that a run wrote NOTHING, not merely that the interesting file
    kept its version.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RootPath
    )

    $hashes = @{}
    if (-not (Test-Path -LiteralPath $RootPath -PathType Container)) {
        return $hashes
    }

    $root = (Resolve-Path -LiteralPath $RootPath).Path.TrimEnd('\', '/')
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File) {
        $relative = $file.FullName.Substring($root.Length).TrimStart('\', '/')
        $hashes[$relative] = Get-BumpVersionFileHash -Path $file.FullName
    }

    return $hashes
}

function Get-BumpVersionTreeStamp {
    <#
    .SYNOPSIS
    SHA-256 and last-write time of every file below a directory, keyed by the
    relative path.

    .DESCRIPTION
    A refused run has to leave the files alone completely. Identical bytes are
    not enough: a rewrite with the same content is still a write, so the
    timestamp has to be part of the proof.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RootPath
    )

    $stamps = @{}
    if (-not (Test-Path -LiteralPath $RootPath -PathType Container)) {
        return $stamps
    }

    $root = (Resolve-Path -LiteralPath $RootPath).Path.TrimEnd('\', '/')
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File) {
        $relative = $file.FullName.Substring($root.Length).TrimStart('\', '/')
        $written = [System.IO.File]::GetLastWriteTimeUtc($file.FullName).Ticks
        $stamps[$relative] = '{0}|{1}' -f (Get-BumpVersionFileHash -Path $file.FullName), $written
    }

    return $stamps
}

function New-BumpVersionCoreRepository {
    <#
    .SYNOPSIS
    Builds a temporary repository with a single component that carries no
    appKey and no moduleKey.

    .DESCRIPTION
    A component without an appKey has no compatibleArtifacts cap to be
    compared with, so the helper never compares its version with anything.
    The contract under test is the version text itself: the numeric core has
    to be one the platform can parse and order. A refusal here therefore comes
    from the version contract and from nothing else.

    .PARAMETER ComponentVersion
    The version the component carries in the manifest.

    .PARAMETER RepositoryVersion
    The version the repository carries in the manifest.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RootPath,
        [Parameter(Mandatory = $false)][string]$ComponentVersion = '1.0.0',
        [Parameter(Mandatory = $false)][string]$RepositoryVersion = '1.0.0'
    )

    if (Test-Path -LiteralPath $RootPath -PathType Container) {
        Remove-Item -LiteralPath $RootPath -Recurse -Force
    }

    $null = New-Item -ItemType Directory -Path $RootPath -Force

    # Copy the bump script so its $repositoryRoot resolves to the temp repo.
    $ompScriptsDir = Join-Path $RootPath 'scripts\omp'
    $null = New-Item -ItemType Directory -Path $ompScriptsDir -Force
    Copy-Item -LiteralPath $scriptPath -Destination (Join-Path $ompScriptsDir 'bump-version.ps1') -Force

    $projectDir = Join-Path $RootPath 'TestApp'
    $null = New-Item -ItemType Directory -Path $projectDir -Force
    $csprojContent = "<Project Sdk=`"Microsoft.NET.Sdk`">`r`n  <PropertyGroup>`r`n    <TargetFramework>net8.0</TargetFramework>`r`n  </PropertyGroup>`r`n</Project>`r`n"
    [System.IO.File]::WriteAllText((Join-Path $projectDir 'TestApp.csproj'), $csprojContent, [System.Text.Encoding]::UTF8)

    $manifest = @{
        repositoryVersion = $RepositoryVersion
        repositoryKey = 'testrepo'
        moduleDefinitions = @()
        components = @(
            @{
                componentKey = 'test_app'
                version = $ComponentVersion
                projectPath = 'TestApp/TestApp.csproj'
            }
        )
    }

    $manifestJson = $manifest | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText((Join-Path $RootPath 'omp-components.json'), $manifestJson, [System.Text.Encoding]::UTF8)

    return (Join-Path $ompScriptsDir 'bump-version.ps1')
}

function New-BumpVersionFindingsRepository {
    <#
    .SYNOPSIS
    Builds a temporary repository with two components, two module definitions
    and one dashboard widget package, so a failure in one selected target can
    be shown to leave EVERY other selected target untouched.

    .PARAMETER MissingModuleDefinition
    The second component references a module definition whose file does not
    exist. The component version would advance while the definition's
    compatibleArtifacts.maxVersion cap stayed behind it.

    .PARAMETER InvalidModuleDefinition
    The second component references a module definition whose file exists but
    does not hold valid JSON.

    .PARAMETER MissingWidgetFile
    The widget package file listed in the manifest does not exist.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RootPath,
        [Parameter(Mandatory = $false)][switch]$MissingModuleDefinition,
        [Parameter(Mandatory = $false)][switch]$InvalidModuleDefinition,
        [Parameter(Mandatory = $false)][switch]$MissingWidgetFile
    )

    $bumpScriptPath = New-TemporaryBumpRepository -RootPath $RootPath -ComponentVersion '1.0.0' -CompatibleArtifactMaxVersion '1.0.0'

    $manifestPath = Join-Path $RootPath 'omp-components.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json

    $widgetPath = Join-Path $RootPath 'widget.json'
    if (-not $MissingWidgetFile) {
        $widgetJson = '{"packageVersion":"1.0.0","widgets":[{"widgetKey":"test_widget","widgetVersion":"1.0.0"}]}'
        [System.IO.File]::WriteAllText($widgetPath, $widgetJson, [System.Text.UTF8Encoding]::new($false))
    }

    $manifest | Add-Member -NotePropertyName widgetFiles -NotePropertyValue @(
        [pscustomobject]@{ path = 'widget.json'; widgetVersion = '1.0.0' }
    ) -Force

    if ($MissingModuleDefinition -or $InvalidModuleDefinition) {
        $projectDir = Join-Path $RootPath 'BrokenApp'
        $null = New-Item -ItemType Directory -Path $projectDir -Force
        $csprojContent = "<Project Sdk=`"Microsoft.NET.Sdk`">`r`n  <PropertyGroup>`r`n    <TargetFramework>net8.0</TargetFramework>`r`n  </PropertyGroup>`r`n</Project>`r`n"
        [System.IO.File]::WriteAllText((Join-Path $projectDir 'BrokenApp.csproj'), $csprojContent, [System.Text.Encoding]::UTF8)

        $definitionRelativePath = 'BrokenModule/absent.module-definition.json'
        if ($InvalidModuleDefinition) {
            $definitionRelativePath = 'BrokenModule/invalid.module-definition.json'
            $null = New-Item -ItemType Directory -Path (Join-Path $RootPath 'BrokenModule') -Force
            [System.IO.File]::WriteAllText((Join-Path $RootPath $definitionRelativePath), '{ "moduleKey": "broken_module", ', [System.Text.UTF8Encoding]::new($false))
        }

        $manifest.components = @($manifest.components) + @(
            [pscustomobject]@{
                componentKey = 'broken_app'
                appKey       = 'broken_app'
                moduleKey    = 'broken_module'
                version      = '1.0.0'
                projectPath  = 'BrokenApp/BrokenApp.csproj'
            }
        )
        $manifest.moduleDefinitions = @($manifest.moduleDefinitions) + @(
            [pscustomobject]@{
                moduleKey         = 'broken_module'
                definitionVersion = '1.0.0'
                path              = $definitionRelativePath
            }
        )
    }

    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

    return $bumpScriptPath
}

if (-not ('OmpBumpVersionProbe.FakeConfirmHost' -as [type])) {
    <#
    A PSHost that answers every confirmation prompt from a scriptable rule, so
    the approval and the refusal path can be proven without a human and
    without a console window. The type is compiled in memory; nothing is
    installed and nothing outside the process is touched.

    The PSHost surface is identical in Windows PowerShell 5.1 and PowerShell 7:
      PSHost              : Name, Version, InstanceId, UI, CurrentCulture,
                           CurrentUICulture, EnterNestedPrompt, ExitNestedPrompt,
                           NotifyBeginApplication, NotifyEndApplication,
                           SetShouldExit(int)
      PSHostUserInterface : RawUI, ReadLine, ReadLineAsSecureString, Write x2,
                           WriteLine(string), WriteErrorLine, WriteDebugLine,
                           WriteProgress, WriteWarningLine, WriteVerboseLine,
                           Prompt, PromptForCredential x2, PromptForChoice
    #>
    $fakeHostSource = @'
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Security;

namespace OmpBumpVersionProbe
{
    public class FakeConfirmHost : PSHost
    {
        private readonly FakeUserInterface ui;

        public List<string> Prompts = new List<string>();
        public List<string> HostOutput = new List<string>();
        public List<string> DeclinePatterns = new List<string>();

        public FakeConfirmHost() { ui = new FakeUserInterface(this); }

        public override PSHostUserInterface UI { get { return ui; } }
        public override string Name { get { return "OmpFakeConfirmHost"; } }
        public override Version Version { get { return new Version(1, 0); } }
        public override Guid InstanceId { get { return Guid.NewGuid(); } }
        public override CultureInfo CurrentCulture { get { return CultureInfo.CurrentCulture; } }
        public override CultureInfo CurrentUICulture { get { return CultureInfo.CurrentUICulture; } }
        public override void EnterNestedPrompt() { }
        public override void ExitNestedPrompt() { }
        public override void NotifyBeginApplication() { }
        public override void NotifyEndApplication() { }
        public override void SetShouldExit(int exitCode) { }
    }

    public class FakeUserInterface : PSHostUserInterface
    {
        private readonly FakeConfirmHost host;

        public FakeUserInterface(FakeConfirmHost host) { this.host = host; }

        public override PSHostRawUserInterface RawUI { get { return null; } }
        public override string ReadLine() { return string.Empty; }
        public override SecureString ReadLineAsSecureString() { return new SecureString(); }
        public override void Write(string value) { host.HostOutput.Add(value ?? string.Empty); }
        public override void Write(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value) { host.HostOutput.Add(value ?? string.Empty); }
        public override void WriteLine(string value) { host.HostOutput.Add(value ?? string.Empty); }
        public override void WriteErrorLine(string value) { host.HostOutput.Add(value ?? string.Empty); }
        public override void WriteDebugLine(string message) { }
        public override void WriteProgress(long sourceId, ProgressRecord record) { }
        public override void WriteWarningLine(string message) { host.HostOutput.Add(message ?? string.Empty); }
        public override void WriteVerboseLine(string message) { host.HostOutput.Add(message ?? string.Empty); }
        public override Dictionary<string, PSObject> Prompt(string caption, string message, Collection<FieldDescription> descriptions) { return new Dictionary<string, PSObject>(); }
        public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName) { return null; }
        public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName, PSCredentialTypes credentialTypes, PSCredentialUIOptions uiOptions) { return null; }

        public override int PromptForChoice(string caption, string message, Collection<ChoiceDescription> choices, int defaultChoice)
        {
            string text = (caption ?? string.Empty) + " " + (message ?? string.Empty);
            host.Prompts.Add(text);

            bool decline = false;
            foreach (string pattern in host.DeclinePatterns)
            {
                if (text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0) { decline = true; }
            }

            for (int index = 0; index < choices.Count; index++)
            {
                string label = choices[index].Label ?? string.Empty;
                if (label.IndexOf("All", StringComparison.OrdinalIgnoreCase) >= 0) { continue; }
                if (!decline && label.IndexOf("Yes", StringComparison.OrdinalIgnoreCase) >= 0) { return index; }
                if (decline && label.IndexOf("No", StringComparison.OrdinalIgnoreCase) >= 0) { return index; }
            }

            return defaultChoice;
        }
    }
}
'@
    Add-Type -TypeDefinition $fakeHostSource -Language CSharp
}

function Invoke-BumpVersionWithFakeHost {
    <#
    .SYNOPSIS
    Runs bump-version.ps1 in a runspace whose host answers the confirmation
    prompts, and returns the prompts, the errors, the REAL exit code and the
    output of the run.

    .DESCRIPTION
    A script's 'exit N' is not observable through the PowerShell API when the
    script is added as a command, but it IS observable when the script is
    called with & : the exit code lands in $LASTEXITCODE. The script is
    therefore called with & inside the runspace, and the exit code is read
    back from the runspace session state.

    The runspace owns the fake host, so no confirmation can ever reach a real
    console and no test can wait for a human.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$BumpScriptPath,
        [Parameter(Mandatory = $true)][hashtable]$Parameters,
        [Parameter(Mandatory = $false)][string[]]$DeclinePatterns = @()
    )

    $fakeHost = New-Object OmpBumpVersionProbe.FakeConfirmHost
    foreach ($pattern in $DeclinePatterns) {
        $fakeHost.DeclinePatterns.Add($pattern)
    }

    $runspace = [runspacefactory]::CreateRunspace($fakeHost)
    $runspace.Open()
    $engine = [PowerShell]::Create()
    $engine.Runspace = $runspace
    try {
        $null = $engine.AddScript(@'
param($BumpScriptPath, $Parameters)
$global:LASTEXITCODE = 0
& $BumpScriptPath @Parameters
'@).AddParameters(@{
                BumpScriptPath = $BumpScriptPath
                Parameters     = $Parameters
            })

        $output = @($engine.Invoke())
        $exitCode = $runspace.SessionStateProxy.GetVariable('LASTEXITCODE')

        $promptTargets = @()
        foreach ($prompt in $fakeHost.Prompts) {
            $match = [regex]::Match($prompt, 'on target "(?<target>[^"]+)"')
            if ($match.Success) {
                $promptTargets += $match.Groups['target'].Value
            }
        }

        return [pscustomobject]@{
            ExitCode     = [int]$exitCode
            Failed       = $engine.HadErrors
            Errors       = ($engine.Streams.Error | ForEach-Object { $_.Exception.Message }) -join "`n"
            Prompts      = @($fakeHost.Prompts)
            PromptTargets = @($promptTargets)
            HostOutput   = @($fakeHost.HostOutput)
            Output       = $output
        }
    }
    finally {
        $engine.Dispose()
        $runspace.Dispose()
    }
}

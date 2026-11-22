# Pester assertions are supplied by the repository's pinned module.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 assertions are provided by the pinned module.')]
param()

Describe 'Module SQL guard isolated build folder cleanup' {
    BeforeAll {
        . (Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\omp\module-sql-validator-support.ps1')

        function New-GuardBuildRoot {
            param(
                [Parameter(Mandatory = $true)][string]$GuardRoot,
                [Parameter(Mandatory = $true)][double]$AgeHours,
                [string]$CallerRoot = '',
                [string]$Name = ''
            )

            if ([string]::IsNullOrEmpty($Name)) {
                $Name = ([Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N'))
            }
            $path = Join-Path $GuardRoot $Name
            $output = Join-Path $path 'bin\ModuleSqlGuard\Release\net10.0'
            $null = New-Item -ItemType Directory -Path $output -Force
            [IO.File]::WriteAllText((Join-Path $output 'ModuleSqlGuard.dll'), 'validator')
            if (-not [string]::IsNullOrEmpty($CallerRoot)) {
                Set-ModuleSqlGuardBuildRootInUse -BuildRoot $path -CallerRoot $CallerRoot
            }
            $usedUtc = [DateTime]::UtcNow.AddHours(-$AgeHours)
            foreach ($marker in @(Get-ChildItem -LiteralPath $path -Force -File)) { $marker.LastWriteTimeUtc = $usedUtc }
            [IO.Directory]::SetLastWriteTimeUtc($path, $usedUtc)
            return $path
        }

        function New-DirectoryLink {
            param([string]$LinkPath, [string]$TargetPath)

            # A real junction, made the way an operator or a tool would make one.
            $null = & cmd.exe /c mklink /J "$LinkPath" "$TargetPath" 2>&1
            if ($LASTEXITCODE -ne 0) { return $false }
            return (([IO.File]::GetAttributes($LinkPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0)
        }
    }

    BeforeEach {
        $script:GuardRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N') + '\omp-module-sql-guard')
        $null = New-Item -ItemType Directory -Path $script:GuardRoot -Force
        $script:LiveCaller = Join-Path $TestDrive ('caller-' + [Guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path $script:LiveCaller -Force
        $script:GoneCaller = Join-Path $TestDrive ('gone-' + [Guid]::NewGuid().ToString('N'))
    }

    It 'Removes a folder that has not been used for the maximum age' {
        $old = New-GuardBuildRoot -GuardRoot $script:GuardRoot -AgeHours (8 * 24) -CallerRoot $script:LiveCaller

        $removed = @(Remove-StaleModuleSqlGuardBuildRoots -GuardRoot $script:GuardRoot)

        $removed | Should -Be @($old)
        Test-Path -LiteralPath $old | Should -BeFalse
        @(Get-ChildItem -LiteralPath $script:GuardRoot -Force).Count | Should -Be 0
    }

    It 'Leaves a folder that was used recently, with or without markers' {
        $fresh = New-GuardBuildRoot -GuardRoot $script:GuardRoot -AgeHours 2 -CallerRoot $script:LiveCaller
        $freshLegacy = New-GuardBuildRoot -GuardRoot $script:GuardRoot -AgeHours (6 * 24)

        @(Remove-StaleModuleSqlGuardBuildRoots -GuardRoot $script:GuardRoot).Count | Should -Be 0

        Test-Path -LiteralPath (Join-Path $fresh 'bin\ModuleSqlGuard\Release\net10.0\ModuleSqlGuard.dll') | Should -BeTrue
        Test-Path -LiteralPath $freshLegacy | Should -BeTrue
    }

    It 'Removes a folder from before the markers existed once it is old enough' {
        $legacy = New-GuardBuildRoot -GuardRoot $script:GuardRoot -AgeHours (9 * 24)

        @(Remove-StaleModuleSqlGuardBuildRoots -GuardRoot $script:GuardRoot) | Should -Be @($legacy)
    }

    It 'Removes the folder of a caller that no longer exists after the grace period, and not before' {
        $abandoned = New-GuardBuildRoot -GuardRoot $script:GuardRoot -AgeHours 3 -CallerRoot $script:GoneCaller
        $justLeft = New-GuardBuildRoot -GuardRoot $script:GuardRoot -AgeHours 0.2 -CallerRoot $script:GoneCaller
        $alive = New-GuardBuildRoot -GuardRoot $script:GuardRoot -AgeHours 3 -CallerRoot $script:LiveCaller

        $removed = @(Remove-StaleModuleSqlGuardBuildRoots -GuardRoot $script:GuardRoot)

        $removed | Should -Be @($abandoned)
        Test-Path -LiteralPath $justLeft | Should -BeTrue
        Test-Path -LiteralPath $alive | Should -BeTrue
    }

    It 'Never removes the folder of the run that is cleaning up' {
        $current = New-GuardBuildRoot -GuardRoot $script:GuardRoot -AgeHours (30 * 24) -CallerRoot $script:GoneCaller

        @(Remove-StaleModuleSqlGuardBuildRoots -GuardRoot $script:GuardRoot -CurrentBuildRoot $current).Count | Should -Be 0
        Test-Path -LiteralPath $current | Should -BeTrue
    }

    It 'Leaves a folder in which a file is open' {
        $busy = New-GuardBuildRoot -GuardRoot $script:GuardRoot -AgeHours (30 * 24) -CallerRoot $script:LiveCaller
        $dll = Join-Path $busy 'bin\ModuleSqlGuard\Release\net10.0\ModuleSqlGuard.dll'
        $handle = [IO.File]::Open($dll, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            @(Remove-StaleModuleSqlGuardBuildRoots -GuardRoot $script:GuardRoot).Count | Should -Be 0
        }
        finally {
            $handle.Dispose()
        }

        Test-Path -LiteralPath $dll | Should -BeTrue
        @(Get-ChildItem -LiteralPath $script:GuardRoot -Force -Directory).Name | Should -Be @(Split-Path -Leaf $busy)
    }

    It 'Leaves everything that is not a build folder of its own' {
        $notAKey = New-GuardBuildRoot -GuardRoot $script:GuardRoot -AgeHours (30 * 24) -Name 'somebody-elses-folder'
        $looseFile = Join-Path $script:GuardRoot ('a' * 64)
        [IO.File]::WriteAllText($looseFile, 'a file, not a folder')
        (Get-Item -LiteralPath $looseFile).LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-30)

        @(Remove-StaleModuleSqlGuardBuildRoots -GuardRoot $script:GuardRoot).Count | Should -Be 0

        Test-Path -LiteralPath $notAKey | Should -BeTrue
        Test-Path -LiteralPath $looseFile | Should -BeTrue
    }

    It 'Does nothing below a root that is not the guard root: <Name>' -ForEach @(
        @{ Name = 'another folder name'; Kind = 'OtherName' }
        @{ Name = 'an empty path'; Kind = 'Empty' }
        @{ Name = 'a relative path'; Kind = 'Relative' }
        @{ Name = 'the parent of the guard root'; Kind = 'Parent' }
    ) {
        $outsideRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N') + '\some-other-root')
        $null = New-Item -ItemType Directory -Path $outsideRoot -Force
        $outside = New-GuardBuildRoot -GuardRoot $outsideRoot -AgeHours (30 * 24) -CallerRoot $script:GoneCaller
        $inside = New-GuardBuildRoot -GuardRoot $script:GuardRoot -AgeHours (30 * 24) -CallerRoot $script:GoneCaller

        $argument = switch ($Kind) {
            'OtherName' { $outsideRoot }
            'Empty' { '' }
            'Relative' { 'omp-module-sql-guard' }
            'Parent' { Split-Path -Parent $script:GuardRoot }
        }
        Push-Location (Split-Path -Parent $script:GuardRoot)
        try {
            @(Remove-StaleModuleSqlGuardBuildRoots -GuardRoot $argument).Count | Should -Be 0
        }
        finally {
            Pop-Location
        }

        Test-Path -LiteralPath $outside | Should -BeTrue
        Test-Path -LiteralPath $inside | Should -BeTrue
    }

    It 'Removes a link inside an old folder without touching what it points at' {
        $external = Join-Path $TestDrive ('external-' + [Guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path $external -Force
        $precious = Join-Path $external 'keep.txt'
        [IO.File]::WriteAllText($precious, 'outside the guard root')
        $old = New-GuardBuildRoot -GuardRoot $script:GuardRoot -AgeHours (30 * 24) -CallerRoot $script:LiveCaller
        if (-not (New-DirectoryLink -LinkPath (Join-Path $old 'linked') -TargetPath $external)) {
            Set-ItResult -Skipped -Because 'this process cannot create a directory link'
            return
        }
        [IO.Directory]::SetLastWriteTimeUtc($old, [DateTime]::UtcNow.AddDays(-30))

        @(Remove-StaleModuleSqlGuardBuildRoots -GuardRoot $script:GuardRoot) | Should -Be @($old)

        Test-Path -LiteralPath $old | Should -BeFalse
        [IO.File]::ReadAllText($precious) | Should -Be 'outside the guard root'
    }

    It 'Leaves a build folder name that is itself a link, and its target' {
        $external = Join-Path $TestDrive ('external-' + [Guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path $external -Force
        $precious = Join-Path $external 'keep.txt'
        [IO.File]::WriteAllText($precious, 'outside the guard root')
        $link = Join-Path $script:GuardRoot ('b' * 64)
        if (-not (New-DirectoryLink -LinkPath $link -TargetPath $external)) {
            Set-ItResult -Skipped -Because 'this process cannot create a directory link'
            return
        }
        [IO.Directory]::SetLastWriteTimeUtc($external, [DateTime]::UtcNow.AddDays(-30))

        @(Remove-StaleModuleSqlGuardBuildRoots -GuardRoot $script:GuardRoot).Count | Should -Be 0

        Test-Path -LiteralPath $link | Should -BeTrue
        Test-Path -LiteralPath $precious | Should -BeTrue
    }

    It 'Never removes a folder a running guard holds, however old it looks' {
        # The race this closes: a cleanup reads the age, the owner marks the folder and
        # starts building, the cleanup renames it. The owner holds the marker open
        # instead, and the rename cannot happen.
        $held = New-GuardBuildRoot -GuardRoot $script:GuardRoot -AgeHours 1 -CallerRoot $script:GoneCaller
        $hold = Enter-ModuleSqlGuardBuildRoot -BuildRoot $held -CallerRoot $script:GoneCaller
        try {
            # Old by every rule: far past the maximum age, and its caller is gone.
            $ancient = [DateTime]::UtcNow.AddDays(-400)
            foreach ($marker in @(Get-ChildItem -LiteralPath $held -Force -File)) { $marker.LastWriteTimeUtc = $ancient }
            [IO.Directory]::SetLastWriteTimeUtc($held, $ancient)

            @(Remove-StaleModuleSqlGuardBuildRoots -GuardRoot $script:GuardRoot).Count | Should -Be 0
            Test-Path -LiteralPath (Join-Path $held 'bin\ModuleSqlGuard\Release\net10.0\ModuleSqlGuard.dll') | Should -BeTrue
            @(Get-ChildItem -LiteralPath $script:GuardRoot -Force -Directory).Name | Should -Be @(Split-Path -Leaf $held)

            # A second run of the same caller can hold and refresh it at the same time.
            $second = Enter-ModuleSqlGuardBuildRoot -BuildRoot $held -CallerRoot $script:GoneCaller
            $second.Dispose()
        }
        finally {
            $hold.Dispose()
        }

        # Released and old again: now it goes.
        $ancient = [DateTime]::UtcNow.AddDays(-400)
        foreach ($marker in @(Get-ChildItem -LiteralPath $held -Force -File)) { $marker.LastWriteTimeUtc = $ancient }
        [IO.Directory]::SetLastWriteTimeUtc($held, $ancient)
        @(Remove-StaleModuleSqlGuardBuildRoots -GuardRoot $script:GuardRoot) | Should -Be @($held)
    }

    It 'Holds an existing folder for a -NoBuild run and creates none' {
        $missing = Join-Path $script:GuardRoot ('d' * 64)

        Enter-ModuleSqlGuardBuildRoot -BuildRoot $missing -CallerRoot $script:LiveCaller -ExistingOnly | Should -BeNullOrEmpty
        Test-Path -LiteralPath $missing | Should -BeFalse

        $existing = New-GuardBuildRoot -GuardRoot $script:GuardRoot -AgeHours 1 -CallerRoot $script:LiveCaller
        $hold = Enter-ModuleSqlGuardBuildRoot -BuildRoot $existing -CallerRoot $script:LiveCaller -ExistingOnly
        try {
            $hold | Should -Not -BeNullOrEmpty
        }
        finally {
            $hold.Dispose()
        }
    }

    It 'Leaves a folder whose hash name is not in lower case' {
        $upper = New-GuardBuildRoot -GuardRoot $script:GuardRoot -AgeHours (30 * 24) -Name ('ABCDEF0123456789' * 4)

        @(Remove-StaleModuleSqlGuardBuildRoots -GuardRoot $script:GuardRoot).Count | Should -Be 0
        Test-Path -LiteralPath $upper | Should -BeTrue
        { Remove-ModuleSqlGuardTree -Path $upper -GuardRoot $script:GuardRoot } | Should -Throw '*Refusing to delete*'
        Test-Path -LiteralPath $upper | Should -BeTrue
    }

    It 'Refuses, when called directly, to delete through a junction and leaves its target intact' {
        # The deletion checks its own argument: called on a junction it used to empty
        # whatever the junction pointed at.
        $external = Join-Path $TestDrive ('external-' + [Guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path (Join-Path $external 'sub') -Force
        [IO.File]::WriteAllText((Join-Path $external 'keep.txt'), 'outside the guard root')
        [IO.File]::WriteAllText((Join-Path $external 'sub\keep-too.txt'), 'outside the guard root')
        $buildRootLink = Join-Path $script:GuardRoot ('e' * 64)
        $leftoverLink = Join-Path $script:GuardRoot 'del-0123456789abcdef'
        if (-not ((New-DirectoryLink -LinkPath $buildRootLink -TargetPath $external) -and
                (New-DirectoryLink -LinkPath $leftoverLink -TargetPath $external))) {
            Set-ItResult -Skipped -Because 'this process cannot create a junction'
            return
        }

        { Remove-ModuleSqlGuardTree -Path $buildRootLink -GuardRoot $script:GuardRoot } | Should -Throw '*Refusing to delete*'
        { Remove-ModuleSqlGuardTree -Path $leftoverLink -GuardRoot $script:GuardRoot } | Should -Throw '*Refusing to delete*'
        { Remove-ModuleSqlGuardTreeContent -Path $buildRootLink } | Should -Throw '*junction or symbolic link*'

        [IO.File]::ReadAllText((Join-Path $external 'keep.txt')) | Should -Be 'outside the guard root'
        [IO.File]::ReadAllText((Join-Path $external 'sub\keep-too.txt')) | Should -Be 'outside the guard root'
        Test-Path -LiteralPath $buildRootLink | Should -BeTrue
        Test-Path -LiteralPath $leftoverLink | Should -BeTrue
    }

    It 'Refuses, when called directly, anything that is not a build folder of a guard root: <Name>' -ForEach @(
        @{ Name = 'a folder outside the guard root'; Kind = 'Outside' }
        @{ Name = 'a folder two levels below the guard root'; Kind = 'Nested' }
        @{ Name = 'the guard root itself'; Kind = 'Root' }
        @{ Name = 'a build folder below a root with another name'; Kind = 'OtherRoot' }
        @{ Name = 'a build folder below a root reached through a junction'; Kind = 'LinkedRoot' }
    ) {
        $inside = New-GuardBuildRoot -GuardRoot $script:GuardRoot -AgeHours (30 * 24) -CallerRoot $script:GoneCaller
        $guardRootArgument = $script:GuardRoot
        switch ($Kind) {
            'Outside' {
                $target = Join-Path $TestDrive ('f' * 64)
                $null = New-Item -ItemType Directory -Path $target -Force
            }
            'Nested' {
                $target = Join-Path $inside ('a' * 64)
                $null = New-Item -ItemType Directory -Path $target -Force
            }
            'Root' { $target = $script:GuardRoot }
            'OtherRoot' {
                $guardRootArgument = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N') + '\some-other-root')
                $null = New-Item -ItemType Directory -Path $guardRootArgument -Force
                $target = New-GuardBuildRoot -GuardRoot $guardRootArgument -AgeHours 1
            }
            'LinkedRoot' {
                $linkParent = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
                $null = New-Item -ItemType Directory -Path $linkParent -Force
                $guardRootArgument = Join-Path $linkParent 'omp-module-sql-guard'
                if (-not (New-DirectoryLink -LinkPath $guardRootArgument -TargetPath $script:GuardRoot)) {
                    Set-ItResult -Skipped -Because 'this process cannot create a junction'
                    return
                }
                $target = Join-Path $guardRootArgument (Split-Path -Leaf $inside)
            }
        }

        { Remove-ModuleSqlGuardTree -Path $target -GuardRoot $guardRootArgument } | Should -Throw '*Refusing to delete*'

        Test-Path -LiteralPath $target | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $inside 'bin\ModuleSqlGuard\Release\net10.0\ModuleSqlGuard.dll') | Should -BeTrue
    }

    It 'Leaves a folder with paths beyond the classic length limit to an engine that can delete them' {
        if ($PSVersionTable.PSVersion.Major -le 5) {
            Set-ItResult -Skipped -Because 'Windows PowerShell cannot create the over-long path this case needs'
            return
        }

        $long = New-GuardBuildRoot -GuardRoot $script:GuardRoot -AgeHours (30 * 24) -CallerRoot $script:GoneCaller
        $deep = $long
        while ($deep.Length -lt 300) { $deep = Join-Path $deep ('segment-' + ('x' * 40)) }
        $null = [IO.Directory]::CreateDirectory($deep)
        [IO.File]::WriteAllText((Join-Path $deep 'deep.txt'), 'deep')
        [IO.Directory]::SetLastWriteTimeUtc($long, [DateTime]::UtcNow.AddDays(-30))

        # As Windows PowerShell would see it: not renamed, not half-deleted, reported.
        $reported = @(Remove-StaleModuleSqlGuardBuildRoots -GuardRoot $script:GuardRoot -ClassicPathLimit $true 6>&1)
        ($reported | ForEach-Object { [string]$_ }) -join ' ' | Should -Match 'beyond the classic length limit'
        Test-Path -LiteralPath (Join-Path $deep 'deep.txt') | Should -BeTrue
        @(Get-ChildItem -LiteralPath $script:GuardRoot -Force -Directory).Name | Should -Be @(Split-Path -Leaf $long)

        # An engine with long path support removes it.
        @(Remove-StaleModuleSqlGuardBuildRoots -GuardRoot $script:GuardRoot -ClassicPathLimit $false) | Should -Be @($long)
        Test-Path -LiteralPath $long | Should -BeFalse
    }

    It 'Finishes a deletion an earlier run was interrupted in' {
        $leftover = New-GuardBuildRoot -GuardRoot $script:GuardRoot -AgeHours 0 -Name ('del-' + [Guid]::NewGuid().ToString('N').Substring(0, 16))

        @(Remove-StaleModuleSqlGuardBuildRoots -GuardRoot $script:GuardRoot) | Should -Be @($leftover)
        Test-Path -LiteralPath $leftover | Should -BeFalse
    }
}

Describe 'Module SQL validator staleness' {
    BeforeAll {
        . (Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\omp\module-sql-validator-support.ps1')
    }

    BeforeEach {
        $script:Platform = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $tool = Join-Path $script:Platform 'tools\ModuleSqlGuard'
        $runtime = Join-Path $script:Platform 'Runtime'
        $shared = Join-Path $script:Platform 'shared'
        $script:Output = Join-Path $script:Platform 'out'
        foreach ($folder in @($tool, $runtime, $shared, $script:Output)) { $null = New-Item -ItemType Directory -Path $folder -Force }
        [IO.File]::WriteAllText((Join-Path $tool 'ModuleSqlGuard.csproj'),
            '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../../Runtime/Runtime.csproj" /></ItemGroup></Project>')
        [IO.File]::WriteAllText((Join-Path $tool 'Program.cs'), '// program')
        [IO.File]::WriteAllText((Join-Path $runtime 'Runtime.csproj'),
            '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><Compile Include="..\shared\Rule.cs" /></ItemGroup></Project>')
        [IO.File]::WriteAllText((Join-Path $runtime 'notes.md'), 'not a build input')
        $script:Rule = Join-Path $shared 'Rule.cs'
        [IO.File]::WriteAllText($script:Rule, '// rule')
        $script:Validator = Join-Path $script:Output 'ModuleSqlGuard.dll'
        $script:RuntimeAssembly = Join-Path $script:Output 'OpenModulePlatform.Runtime.dll'
        [IO.File]::WriteAllText($script:Validator, 'dll')
        [IO.File]::WriteAllText($script:RuntimeAssembly, 'dll')

        $script:SourcesUtc = [DateTime]::UtcNow.AddHours(-10)
        foreach ($file in @(Get-ChildItem -LiteralPath $script:Platform -Recurse -File)) { $file.LastWriteTimeUtc = $script:SourcesUtc }
    }

    It 'Accepts a validator built after its sources' {
        (Get-Item -LiteralPath $script:Validator).LastWriteTimeUtc = $script:SourcesUtc.AddHours(1)

        Get-ModuleSqlValidatorStaleSource -ValidatorPath $script:Validator -PlatformRoot $script:Platform | Should -BeNullOrEmpty
    }

    It 'Names the rule source that is newer than the validator' {
        (Get-Item -LiteralPath $script:Rule).LastWriteTimeUtc = $script:SourcesUtc.AddHours(2)

        Get-ModuleSqlValidatorStaleSource -ValidatorPath $script:Validator -PlatformRoot $script:Platform | Should -Be $script:Rule
    }

    It 'Counts a referenced assembly rebuilt after the change as a current build' {
        (Get-Item -LiteralPath $script:Rule).LastWriteTimeUtc = $script:SourcesUtc.AddHours(2)
        (Get-Item -LiteralPath $script:RuntimeAssembly).LastWriteTimeUtc = $script:SourcesUtc.AddHours(3)

        Get-ModuleSqlValidatorStaleSource -ValidatorPath $script:Validator -PlatformRoot $script:Platform | Should -BeNullOrEmpty
    }

    It 'Ignores a newer C# file where MSBuild takes no sources from: <StrayFolder>' -ForEach @(
        @{ StrayFolder = 'Runtime\artifacts\staged' }
        @{ StrayFolder = 'Runtime\obj' }
        @{ StrayFolder = 'Runtime\bin\Release' }
        @{ StrayFolder = 'Runtime\.vs' }
    ) {
        # Such a file is never compiled, so no rebuild would ever make the validator
        # newer than it: counted, it would fail -NoBuild for good.
        $directory = Join-Path $script:Platform $StrayFolder
        $null = New-Item -ItemType Directory -Path $directory -Force
        $stray = Join-Path $directory 'Stray.cs'
        [IO.File]::WriteAllText($stray, '// not compiled')
        (Get-Item -LiteralPath $stray).LastWriteTimeUtc = $script:SourcesUtc.AddHours(5)

        Get-ModuleSqlValidatorStaleSource -ValidatorPath $script:Validator -PlatformRoot $script:Platform | Should -BeNullOrEmpty
    }

    It 'Still counts a C# file in a nested folder that merely shares one of those names' {
        # Only bin, obj and artifacts directly below the project folder are left out.
        $directory = Join-Path $script:Platform 'Runtime\Packaging\artifacts'
        $null = New-Item -ItemType Directory -Path $directory -Force
        $compiled = Join-Path $directory 'Compiled.cs'
        [IO.File]::WriteAllText($compiled, '// compiled')
        (Get-Item -LiteralPath $compiled).LastWriteTimeUtc = $script:SourcesUtc.AddHours(5)

        Get-ModuleSqlValidatorStaleSource -ValidatorPath $script:Validator -PlatformRoot $script:Platform | Should -Be $compiled
    }

    It 'Ignores a newer file that the build does not read' {
        (Get-Item -LiteralPath (Join-Path $script:Platform 'Runtime\notes.md')).LastWriteTimeUtc = $script:SourcesUtc.AddHours(5)

        Get-ModuleSqlValidatorStaleSource -ValidatorPath $script:Validator -PlatformRoot $script:Platform | Should -BeNullOrEmpty
    }
}

Describe 'Module SQL guard -NoBuild validator selection' {
    BeforeAll {
        $script:Guard = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\omp\Test-ModuleSqlGuards.ps1'
        $script:Consumer = Join-Path $TestDrive 'NoBuildConsumer'
        $null = New-Item -ItemType Directory -Path $script:Consumer -Force
        $script:Probe = Join-Path $TestDrive 'allowed.sql'
        [IO.File]::WriteAllText($script:Probe, 'SELECT 1;')

        function Invoke-Guard {
            param([switch]$NoBuild)

            $shell = (Get-Process -Id $PID).Path
            $arguments = @(
                '-NoProfile', '-NonInteractive', '-File', $script:Guard,
                '-RepositoryRoot', $script:Consumer, '-Path', $script:Probe, '-Verbose'
            )
            if ($NoBuild) { $arguments += '-NoBuild' }
            # The child's error stream is merged in on purpose; it must not stop this
            # script before the exit code and the text have been collected.
            $previousPreference = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                $output = @(& $shell @arguments 2>&1 | ForEach-Object { [string]$_ })
                $exitCode = $LASTEXITCODE
            }
            finally {
                $ErrorActionPreference = $previousPreference
            }
            $text = [regex]::Replace(($output -join "`n"), '\x1b\[[0-9;]*[A-Za-z]', '')
            # Each shell decorates and wraps redirected host and error output in its own
            # way (line breaks at the buffer width, a '|' gutter in front of a wrapped error
            # message). Compact drops all of that so a phrase can be recognised wherever
            # the shell chose to break it.
            return [pscustomobject]@{
                ExitCode = $exitCode
                Text = $text
                Flat = ($text -replace '[\r\n]', '')
                Compact = ($text -replace '[\s|]', '')
            }
        }

        # One real build for this caller; every case below runs -NoBuild against it.
        $script:Built = Invoke-Guard
        $match = [regex]::Match($script:Built.Flat, 'Module SQL validator isolated build root:\s*(.+?\\omp-module-sql-guard\\[0-9a-f]{64})')
        $script:BuildRoot = $match.Groups[1].Value.Trim()
    }

    AfterAll {
        $guardRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'omp-module-sql-guard'))
        if (-not [string]::IsNullOrWhiteSpace($script:BuildRoot)) {
            $fullRoot = [IO.Path]::GetFullPath($script:BuildRoot)
            if ($fullRoot.StartsWith($guardRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -and
                (Test-Path -LiteralPath $fullRoot -PathType Container)) {
                Remove-Item -LiteralPath $fullRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    It 'Builds an isolated validator for the caller first' {
        $script:Built.ExitCode | Should -Be 0 -Because $script:Built.Text
        $script:BuildRoot | Should -Not -BeNullOrEmpty -Because $script:Built.Text
        Test-Path -LiteralPath (Join-Path $script:BuildRoot 'bin\ModuleSqlGuard\Release\net10.0\ModuleSqlGuard.dll') | Should -BeTrue
    }

    It 'Uses the validator this caller built, not the one in the platform checkout, and says so' {
        $result = Invoke-Guard -NoBuild

        $result.ExitCode | Should -Be 0 -Because $result.Text
        $result.Compact.Contains('Checked1script(s)') | Should -BeTrue -Because $result.Text
        $result.Compact.Contains('ModuleSQLvalidator(-NoBuild):') | Should -BeTrue -Because $result.Text
        $result.Compact.Contains("thiscaller'sisolatedbuild") | Should -BeTrue -Because $result.Text
        $expectedValidator = (Join-Path $script:BuildRoot 'bin\ModuleSqlGuard\Release\net10.0\ModuleSqlGuard.dll') -replace '\s', ''
        $result.Compact.Contains($expectedValidator) | Should -BeTrue -Because $result.Text
    }

    It 'Refuses a validator that is older than its sources instead of measuring with it' {
        $output = Join-Path $script:BuildRoot 'bin\ModuleSqlGuard\Release\net10.0'
        $assemblies = @(Get-ChildItem -LiteralPath $output -Filter '*.dll' -File)
        $originalTimes = @{}
        foreach ($assembly in $assemblies) {
            $originalTimes[$assembly.FullName] = $assembly.LastWriteTimeUtc
            $assembly.LastWriteTimeUtc = [DateTime]::new(2001, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)
        }
        try {
            $result = Invoke-Guard -NoBuild
        }
        finally {
            foreach ($assembly in $assemblies) { $assembly.LastWriteTimeUtc = $originalTimes[$assembly.FullName] }
        }

        $result.ExitCode | Should -Not -Be 0 -Because $result.Text
        $result.Compact.Contains('olderthanitssourcesandwasnotused') | Should -BeTrue -Because $result.Text
        $result.Compact.Contains('Checked1script(s)') | Should -BeFalse -Because $result.Text
    }
}

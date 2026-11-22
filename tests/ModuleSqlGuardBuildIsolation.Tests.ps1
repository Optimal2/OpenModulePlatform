# Pester assertions are supplied by the repository's pinned module.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 assertions and process redirection are provided by the pinned module and supported PowerShell runtimes.')]
param()

Describe 'Module SQL guard isolated consumer builds' {
    BeforeEach {
        $script:Guard = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\omp\Test-ModuleSqlGuards.ps1'
        $script:Probe = Join-Path $TestDrive 'allowed.sql'
        [IO.File]::WriteAllText($script:Probe, 'SELECT 1;')
        $script:Consumers = @(
            (Join-Path $TestDrive 'ConsumerA'),
            (Join-Path $TestDrive 'ConsumerB')
        )
        foreach ($consumer in $script:Consumers) {
            $null = New-Item -ItemType Directory -Path $consumer -Force
        }
        $script:BuildRoots = @()
        $script:Processes = @()
    }

    AfterEach {
        foreach ($process in @($script:Processes)) {
            if ($null -ne $process -and -not $process.HasExited) {
                & taskkill.exe /PID $process.Id /T /F 2>$null | Out-Null
            }
            if ($null -ne $process) { $process.Dispose() }
        }
        $buildParent = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'omp-module-sql-guard'))
        foreach ($root in @($script:BuildRoots)) {
            if (-not [string]::IsNullOrWhiteSpace($root)) {
                $fullRoot = [IO.Path]::GetFullPath($root)
                if ($fullRoot.StartsWith($buildParent.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -and
                    (Test-Path -LiteralPath $fullRoot -PathType Container)) {
                    Remove-Item -LiteralPath $fullRoot -Recurse -Force -ErrorAction SilentlyContinue
                }
            }
        }
    }

    It 'Runs two consumers concurrently without sharing intermediate or output folders' {
        $shell = (Get-Process -Id $PID).Path
        $script:Processes = @()
        foreach ($index in 0..1) {
            $stdout = Join-Path $TestDrive "guard-$index.out"
            $stderr = Join-Path $TestDrive "guard-$index.err"
            $arguments = @(
                '-NoProfile', '-NonInteractive', '-WindowStyle', 'Hidden',
                '-File', ('"' + $script:Guard + '"'),
                '-RepositoryRoot', ('"' + $script:Consumers[$index] + '"'),
                '-Path', ('"' + $script:Probe + '"'),
                '-Verbose'
            )
            $script:Processes += Start-Process -FilePath $shell -ArgumentList $arguments `
                -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
        }

        foreach ($process in $script:Processes) {
            if (-not $process.WaitForExit(240000)) {
                throw "Concurrent module SQL guard process $($process.Id) did not finish within four minutes."
            }
            $process.WaitForExit()
        }

        for ($index = 0; $index -lt 2; $index++) {
            $stdout = Join-Path $TestDrive "guard-$index.out"
            $stderr = Join-Path $TestDrive "guard-$index.err"
            $output = [IO.File]::ReadAllText($stdout) + [IO.File]::ReadAllText($stderr)
            # The verbose line is decorated by the shell that writes it, and the decoration
            # depends on the host. PowerShell 7 can wrap it in ANSI colour sequences even when
            # the stream is redirected to a file, so the line no longer starts with the
            # prefix. Windows PowerShell wraps redirected host output at the buffer width, so
            # the path lands on the following line and a long one is cut in two. Strip the
            # colour sequences, undo the wrapping after the message, and read the root by its
            # known shape instead of by its position on a line.
            $output = [regex]::Replace($output, '\x1b\[[0-9;]*[A-Za-z]', '')
            $script:Processes[$index].ExitCode | Should -Be 0 -Because $output
            $output | Should -Match 'Checked 1 script\(s\)'
            $marker = 'Module SQL validator isolated build root:'
            $markerIndex = $output.IndexOf($marker, [StringComparison]::Ordinal)
            $markerIndex | Should -BeGreaterOrEqual 0 -Because $output
            $afterMarker = $output.Substring($markerIndex + $marker.Length) -replace '[\r\n]', ''
            $match = [regex]::Match($afterMarker, '^\s*(.+?\\omp-module-sql-guard\\[0-9a-f]{64})')
            $match.Success | Should -BeTrue -Because $output
            $script:BuildRoots += $match.Groups[1].Value.Trim()
        }

        $script:BuildRoots[0] | Should -Not -Be $script:BuildRoots[1]
        foreach ($root in $script:BuildRoots) {
            Test-Path -LiteralPath (Join-Path $root 'obj\ModuleSqlGuard') -PathType Container | Should -BeTrue
            Test-Path -LiteralPath (Join-Path $root 'bin\ModuleSqlGuard\Release\net10.0\ModuleSqlGuard.dll') -PathType Leaf | Should -BeTrue
        }
    }
}

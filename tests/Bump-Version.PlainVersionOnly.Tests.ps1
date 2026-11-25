[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Assertions use the repository-pinned Pester 6.1.0 module.')]
param()

Describe 'Bump-Version: plain versions in a non-interactive child process' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Bump-Version.TestHelpers.ps1')

        function Invoke-BumpVersionChild {
            param([string]$BumpScriptPath, [string]$Version)

            $engine = if ($PSVersionTable.PSEdition -eq 'Desktop') { 'powershell.exe' } else { 'pwsh.exe' }
            $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
            $startInfo.FileName = Join-Path $PSHOME $engine
            $startInfo.Arguments = "-NoProfile -NonInteractive -File ""$BumpScriptPath"" -RepositoryOnly -Version $Version"
            $startInfo.UseShellExecute = $false
            $startInfo.CreateNoWindow = $true
            $startInfo.RedirectStandardOutput = $true
            $startInfo.RedirectStandardError = $true
            $process = [System.Diagnostics.Process]::Start($startInfo)
            try {
                $stdout = $process.StandardOutput.ReadToEndAsync()
                $stderr = $process.StandardError.ReadToEndAsync()
                if (-not $process.WaitForExit(30000)) {
                    $process.Kill()
                    $process.WaitForExit()
                    throw 'The bump-version child did not exit within 30 seconds.'
                }
                return [pscustomobject]@{
                    ExitCode = $process.ExitCode
                    Output = $stdout.Result + $stderr.Result
                }
            }
            finally {
                $process.Dispose()
            }
        }
    }

    It 'Refuses <Scenario> in the child process without changing any fixture files' -ForEach @(
        @{ Scenario = 'an explicit dotted prerelease'; Version = '1.2.3-beta.1'; Existing = '1.0.0'; Offending = '1.2.3-beta.1' }
        @{ Scenario = 'explicit build metadata'; Version = '1.2.3+build5'; Existing = '1.0.0'; Offending = '1.2.3+build5' }
        @{ Scenario = 'a suffixed component version during a repository-only bump'; Version = '1.2.3'; Existing = '1.0.0-rc.1'; Offending = '1.0.0-rc.1' }
    ) {
        $repoRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $bump = New-TemporaryBumpRepository -RootPath $repoRoot -ComponentVersion $Existing
        $before = Get-BumpVersionTreeStamp -RootPath $repoRoot

        $result = Invoke-BumpVersionChild -BumpScriptPath $bump -Version $Version

        $result.ExitCode | Should -Not -Be 0
        $message = "Version '$Offending' is not allowed: OMP versions must be plain major.minor.patch (three numeric segments, X.Y.Z) with no prerelease or build suffix."
        # Windows PowerShell wraps error records when the process has no console.
        ($result.Output -replace '\s+', '') | Should -Match ([regex]::Escape(($message -replace '\s+', '')))
        $after = Get-BumpVersionTreeStamp -RootPath $repoRoot
        $before.ContainsKey('omp-components.json') | Should -BeTrue
        $after.Count | Should -Be $before.Count
        foreach ($path in $before.Keys) {
            $after[$path] | Should -Be $before[$path] -Because "$path should stay byte-identical and unwritten"
        }
    }

    It 'Accepts explicit 1.2.3 in the child process and updates the manifest' {
        $repoRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $bump = New-TemporaryBumpRepository -RootPath $repoRoot

        $result = Invoke-BumpVersionChild -BumpScriptPath $bump -Version '1.2.3'

        $result.ExitCode | Should -Be 0
        $manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'omp-components.json') -Raw | ConvertFrom-Json
        $manifest.repositoryVersion | Should -Be '1.2.3'
    }
}

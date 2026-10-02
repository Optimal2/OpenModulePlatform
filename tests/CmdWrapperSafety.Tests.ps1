# Pester assertions are supplied by the repository's pinned module.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 assertions are provided by the pinned module.')]
param()

Describe 'CMD wrapper process safety' {
    BeforeAll {
        $sourcePath = Join-Path $PSScriptRoot '..\scripts\omp\test-cmd-wrappers.ps1'
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($sourcePath, [ref]$tokens, [ref]$errors)
        $errors.Count | Should -Be 0
        # Import declarations only; never run repository discovery or packaging.
        foreach ($statement in $ast.EndBlock.Statements) {
            if ($statement -is [System.Management.Automation.Language.FunctionDefinitionAst] -or
                ($statement -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                 $statement.Left.Extent.Text -match '^\$(MaximumCmdCommandLineLength|MinimumValidProcessId|TaskKill.*)$')) {
                . ([scriptblock]::Create($statement.Extent.Text))
            }
        }
        # Exercise the real call site as well as the guard: the original bug was
        # in its argument, not in the comparison inside the assertion function.
        $guardCalls = @($ast.FindAll({ param($node)
            $node -is [System.Management.Automation.Language.CommandAst] -and
                $node.GetCommandName() -eq 'Assert-CmdCommandLineLength'
        }, $true))
        $guardCalls.Count | Should -Be 1
        $preflight = [scriptblock]::Create($guardCalls[0].Extent.Text)
    }

    It 'accepts the exact 8191-character launch line and rejects one character more' {
        foreach ($cmdExePath in @('C:\Windows\System32\cmd.exe', 'C:\System Root\System32\cmd.exe')) {
            # Windows PowerShell 5.1 appends a space after ArgumentList; PowerShell
            # 7 joins it without a trailing space. Both quote the executable.
            $trailingSpaceLength = 0
            if ($PSVersionTable.PSVersion.Major -le 5) { $trailingSpaceLength = 1 }
            $prefixLength = ('"' + $cmdExePath + '" /d /c call ').Length + $trailingSpaceLength
            $cmdArguments = @('/d', '/c', ('call ' + ('x' * (8191 - $prefixLength))))
            { . $preflight } | Should -Not -Throw
            $cmdArguments[2] += 'x'
            { . $preflight } | Should -Throw '*8192*8191*'
        }
    }

    It 'measures the actual command line received by cmd.exe' {
        $cmdExePath = Join-Path $env:SystemRoot 'System32\cmd.exe'
        $cmdArguments = @('/d', '/c', 'echo [%cmdcmdline%]')
        Mock Assert-CmdCommandLineLength { param($CommandLine) $script:measuredCommandLine = $CommandLine }
        . $preflight
        $stdout = Join-Path $TestDrive 'command-line.txt'
        $process = Start-Process -FilePath $cmdExePath -ArgumentList $cmdArguments -WindowStyle Hidden -RedirectStandardOutput $stdout -PassThru -Wait
        try { $process.ExitCode | Should -Be 0 }
        finally { $process.Dispose() }
        # Delimit the expansion: echo also prints the trailing space that 5.1
        # adds to its own invocation, outside the closing bracket. Preserve the
        # space inside the brackets because it belongs to lpCommandLine.
        $echoOutput = [System.IO.File]::ReadAllText($stdout).TrimEnd([char[]]" `r`n")
        $echoOutput | Should -BeLike '[[]*]'
        $received = $echoOutput.Substring(1, $echoOutput.Length - 2)
        $script:measuredCommandLine | Should -BeExactly $received
    }

    It 'returns the execution sentinel and availability warning when taskkill cannot be resolved' {
        $originalSystemRoot = $env:SystemRoot
        try {
            # No helper can start from this absent directory, even if PATH has
            # a taskkill executable. No actual process is terminated by this test.
            $env:SystemRoot = Join-Path $TestDrive 'missing-system-root'
            $result = Invoke-TaskKillTree -ProcessId 12345
        }
        finally { $env:SystemRoot = $originalSystemRoot }
        $result.ExitCode | Should -Be ([int]::MinValue)
        ($result.Output -join ' ') | Should -BeLike '*Could not resolve the system taskkill.exe*'
        $warnings = @(Write-TaskKillFailureWarning -RepositoryName probe -ExitCode $result.ExitCode -Output $result.Output 3>&1)
        $warnings.Count | Should -Be 1
        $warnings[0].Message | Should -BeLike '*taskkill could not be started*Could not resolve the system taskkill.exe*'
    }
}

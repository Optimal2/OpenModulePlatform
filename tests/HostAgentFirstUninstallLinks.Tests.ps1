# Pester assertions are supplied by the repository's pinned module.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 assertions are provided by the pinned module.')]
param()

# The uninstall script is generated as a here-string inside
# package-hostagent-first.ps1 and runs on the target host, where the repository
# does not exist. These tests load its functions from that exact text, so they
# test what ships in the package.
Describe 'HostAgent-first uninstall: runtime folder removal never follows links' {
    BeforeAll {
        $sourcePath = Join-Path $PSScriptRoot '..\scripts\deployment\package-hostagent-first.ps1'
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($sourcePath, [ref]$tokens, [ref]$errors)
        $errors.Count | Should -Be 0
        $assignment = $ast.Find({ param($node)
                $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
                $node.Left.VariablePath.UserPath -eq 'uninstallScript'
            }, $true)
        $assignment | Should -Not -BeNullOrEmpty
        $uninstallText = $assignment.Right.Find({ param($node)
                $node -is [System.Management.Automation.Language.StringConstantExpressionAst]
            }, $true).Value

        $uninstallAst = [System.Management.Automation.Language.Parser]::ParseInput($uninstallText, [ref]$tokens, [ref]$errors)
        $errors.Count | Should -Be 0
        $functionText = @($uninstallAst.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst]
                }, $false) | ForEach-Object { $_.Extent.Text }) -join [Environment]::NewLine
        . ([scriptblock]::Create($functionText))
    }

    BeforeEach {
        $script:root = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-uninstall-links-' + [Guid]::NewGuid().ToString('N'))
        $script:runtimeFolder = Join-Path $script:root 'runtime\ArtifactStore'
        $script:outside = Join-Path $script:root 'outside'
        $script:outsideFile = Join-Path $script:outside 'keep.txt'
        [void](New-Item -ItemType Directory -Path (Join-Path $script:runtimeFolder 'nested') -Force)
        [void](New-Item -ItemType Directory -Path $script:outside -Force)
        Set-Content -LiteralPath (Join-Path $script:runtimeFolder 'nested\runtime.txt') -Value 'runtime' -Encoding ASCII
        Set-Content -LiteralPath $script:outsideFile -Value 'outside' -Encoding ASCII
    }

    AfterEach {
        # Remove links first so the cleanup itself cannot follow one. .NET, not
        # Remove-Item: a test's Remove-Item mock is still active here.
        if (Test-Path -LiteralPath $script:root) {
            Get-ChildItem -LiteralPath $script:root -Recurse -Force -Attributes ReparsePoint -ErrorAction SilentlyContinue |
                Sort-Object { $_.FullName.Length } -Descending |
                ForEach-Object { [System.IO.Directory]::Delete($_.FullName, $false) }
            [System.IO.Directory]::Delete($script:root, $true)
        }
    }

    It 'removes an ordinary runtime folder as before' {
        $output = Remove-ConfiguredDirectory -Path $script:runtimeFolder 6>&1 | ForEach-Object { [string]$_ }

        Test-Path -LiteralPath $script:runtimeFolder | Should -BeFalse
        $output | Should -Contain "Removing $script:runtimeFolder"
        Test-Path -LiteralPath $script:outsideFile | Should -BeTrue
    }

    It 'removes a junction inside a runtime folder as a link and keeps the target content' {
        $link = Join-Path $script:runtimeFolder 'nested\linked'
        [void](New-Item -ItemType Junction -Path $link -Value $script:outside)

        Remove-ConfiguredDirectory -Path $script:runtimeFolder 6>$null

        Test-Path -LiteralPath $script:runtimeFolder | Should -BeFalse
        Test-Path -LiteralPath $script:outsideFile | Should -BeTrue
        Get-Content -LiteralPath $script:outsideFile | Should -Be 'outside'
    }

    It 'keeps the target content even when the recursive delete follows links (older Windows PowerShell 5.1)' {
        # Recent 5.1 builds no longer walk into links, so emulate the engines
        # that do: a recursive Remove-Item that meets a link empties its target.
        Mock Remove-Item -ParameterFilter { $Recurse -and $LiteralPath -eq $script:runtimeFolder } {
            $links = @(Get-ChildItem -LiteralPath $LiteralPath -Recurse -Force -Attributes ReparsePoint -ErrorAction SilentlyContinue)
            foreach ($link in $links) {
                Get-ChildItem -LiteralPath $link.Target -Force | ForEach-Object { [System.IO.File]::Delete($_.FullName) }
                [System.IO.Directory]::Delete($link.FullName, $false)
            }
            [System.IO.Directory]::Delete($LiteralPath, $true)
        }
        $link = Join-Path $script:runtimeFolder 'nested\linked'
        [void](New-Item -ItemType Junction -Path $link -Value $script:outside)

        Remove-ConfiguredDirectory -Path $script:runtimeFolder 6>$null

        # The emulation must actually run; otherwise the test passes on engines that never follow links.
        Should -Invoke Remove-Item -Times 1 -Exactly -ParameterFilter { $Recurse -and $LiteralPath -eq $script:runtimeFolder }
        Test-Path -LiteralPath $script:runtimeFolder | Should -BeFalse
        Test-Path -LiteralPath $script:outsideFile | Should -BeTrue
    }

    It 'refuses a runtime folder that is itself a junction and keeps both link and target' {
        $linkedFolder = Join-Path $script:root 'runtime\WebApps'
        [void](New-Item -ItemType Junction -Path $linkedFolder -Value $script:outside)

        { Remove-ConfiguredDirectory -Path $linkedFolder 6>$null } |
            Should -Throw -ExpectedMessage '*Refusing to remove*reparse point*'

        Test-Path -LiteralPath $linkedFolder | Should -BeTrue
        Test-Path -LiteralPath $script:outsideFile | Should -BeTrue
    }

    It 'refuses a drive root' {
        $driveRoot = [System.IO.Path]::GetPathRoot($script:root)
        Mock Remove-Item -ParameterFilter { $LiteralPath -eq $driveRoot } { throw 'Remove-Item must not be called for a drive root.' }

        { Remove-ConfiguredDirectory -Path $driveRoot 6>$null } |
            Should -Throw -ExpectedMessage '*Refusing to remove*drive root*'

        Should -Invoke Remove-Item -ParameterFilter { $LiteralPath -eq $driveRoot } -Times 0 -Exactly
    }
}

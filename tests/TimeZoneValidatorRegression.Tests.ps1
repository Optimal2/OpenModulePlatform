[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 parameters come from the pinned module.')]
param()

Describe 'Check 21: nested interpolation and nameof boundaries' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Validate-ComponentVersions.TestHelpers.ps1')
    }

    It 'Detects the call after adjacent nested closing braces' {
        $text = 'var s = $"{new { A = 1 }}"; TimeZoneInfo.FindSystemTimeZoneById(id);'
        Test-DirectTimeZonePlatformCall (Remove-CSharpCommentsAndStringLiterals $text) | Should -BeTrue
    }

    It 'Masks a mention in a string after adjacent nested closing braces' {
        $text = 'var s = $"{new { A = 1 }}"; var prose = "TimeZoneInfo.FindSystemTimeZoneById(id)";'
        Test-DirectTimeZonePlatformCall (Remove-CSharpCommentsAndStringLiterals $text) | Should -BeFalse
    }

    It 'Handles nested switch braces and nested string literals' {
        $text = 'var s = $"{x switch { 1 => "a", _ => "b" }}"; TimeZoneInfo.FindSystemTimeZoneById(id);'
        Test-DirectTimeZonePlatformCall (Remove-CSharpCommentsAndStringLiterals $text) | Should -BeTrue
    }

    It 'Preserves multiline verbatim layout around a nested hole' {
        $text = '$@"first' + "`r`n" + '{new { A = 1 }} last' + "`r`n" + '"; TimeZoneInfo.FindSystemTimeZoneById(id);'
        $masked = Remove-CSharpCommentsAndStringLiterals $text
        $masked.Length | Should -Be $text.Length
        ($masked -replace '[^\r\n]', '') | Should -Be ($text -replace '[^\r\n]', '')
        Test-DirectTimeZonePlatformCall $masked | Should -BeTrue
    }

    It 'Counts raw-string nested braces individually before its two-brace delimiter' {
        $text = 'var s = $$"""{{new { A = new { B = 1 }}}}}"""; TimeZoneInfo.FindSystemTimeZoneById(id);'
        Test-DirectTimeZonePlatformCall (Remove-CSharpCommentsAndStringLiterals $text) | Should -BeTrue
    }

    It 'Keeps a call inside a raw-string hole visible after a nested object' {
        $text = 'var s = $$"""{{new { A = 1 }.ToString() + TimeZoneInfo.FindSystemTimeZoneById(id)}}""";'
        Test-DirectTimeZonePlatformCall (Remove-CSharpCommentsAndStringLiterals $text) | Should -BeTrue
    }

    It 'Does not mistake xnameof for the nameof keyword' {
        Test-DirectTimeZonePlatformCall 'xnameof(TimeZoneInfo.FindSystemTimeZoneById(id));' | Should -BeTrue
    }

    It 'Still excludes the actual nameof keyword with whitespace' {
        Test-DirectTimeZonePlatformCall 'nameof ( TimeZoneInfo.FindSystemTimeZoneById)' | Should -BeFalse
    }
}

Describe 'Check 21: MSBuild global usings' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Validate-ComponentVersions.TestHelpers.ps1')
    }

    It 'Detects a <Kind> using from <BuildFile>' -ForEach @(
        @{ Kind = 'static'; BuildFile = 'TestApp/TestApp.csproj'; Metadata = 'Static="true"'; Call = 'FindSystemTimeZoneById(id)' }
        @{ Kind = 'alias'; BuildFile = 'TestApp/TestApp.csproj'; Metadata = 'Alias="X"'; Call = 'X.FindSystemTimeZoneById(id)' }
        @{ Kind = 'static'; BuildFile = 'Directory.Build.props'; Metadata = 'Static="true"'; Call = 'FindSystemTimeZoneById(id)' }
        @{ Kind = 'alias'; BuildFile = 'TestApp/Custom.targets'; Metadata = 'Alias="X"'; Call = 'X.FindSystemTimeZoneById(id)' }
    ) {
        $repoRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $validator = New-TemporaryTestRepository -RootPath $repoRoot
        [IO.File]::WriteAllText((Join-Path $repoRoot $BuildFile), ('<Project><ItemGroup><Using Include="System.TimeZoneInfo" ' + $Metadata + ' /></ItemGroup></Project>'))
        [IO.File]::WriteAllText((Join-Path $repoRoot 'TestApp/Calendar.cs'), ('class Calendar { void M() { _ = ' + $Call + '; } }'))
        $result = Invoke-ValidatorWithOutput -ValidatorPath $validator
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match "Production file 'TestApp[\\/]Calendar.cs' uses TimeZoneInfo"
    }

    It 'Ignores test-project usings and commented-out production usings' {
        $repoRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $validator = New-TemporaryTestRepository -RootPath $repoRoot
        $null = New-Item -ItemType Directory (Join-Path $repoRoot 'Verification')
        [IO.File]::WriteAllText((Join-Path $repoRoot 'Verification/Verification.csproj'), '<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><Using Include="System.TimeZoneInfo" Static="true" Alias="X" /></ItemGroup></Project>')
        [IO.File]::WriteAllText((Join-Path $repoRoot 'Verification/Custom.props'), '<Project><ItemGroup><Using Include="System.TimeZoneInfo" Static="true" /></ItemGroup></Project>')
        [IO.File]::WriteAllText((Join-Path $repoRoot 'Directory.Build.props'), '<Project><!-- <Using Include="System.TimeZoneInfo" Static="true" Alias="X" /> --></Project>')
        [IO.File]::WriteAllText((Join-Path $repoRoot 'TestApp/Calendar.cs'), 'class Calendar { void M() { FindSystemTimeZoneById(id); X.FindSystemTimeZoneById(id); } }')
        $result = Invoke-ValidatorWithOutput -ValidatorPath $validator
        $result.ExitCode | Should -Be 0 -Because $result.Output
    }
}

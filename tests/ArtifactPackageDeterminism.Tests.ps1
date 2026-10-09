[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 assertions come from the pinned module, not the inbox Pester 3 profile.')]
param()

Describe 'Artifact package deterministic bytes' {
    It 'Ignores creation order and timestamps, including generated worker compatibility metadata' {
        $packer = Join-Path $PSScriptRoot '../scripts/deployment/new-omp-artifact-package.ps1'
        $hashes = @()
        foreach ($leg in @('first', 'second')) {
            $root = Join-Path $TestDrive $leg
            $payload = Join-Path $root 'payload'
            [void](New-Item -ItemType Directory -Path $payload -Force)
            $names = if ($leg -eq 'first') { @('z.txt', 'a.txt') } else { @('a.txt', 'z.txt') }
            foreach ($name in $names) {
                $file = Join-Path $payload $name
                [IO.File]::WriteAllText($file, $name)
                (Get-Item -LiteralPath $file).LastWriteTimeUtc = if ($leg -eq 'first') {
                    [datetime]'2020-01-02T03:04:06Z'
                } else { [datetime]'2024-05-06T07:08:10Z' }
            }
            $zip = Join-Path $root 'package.zip'
            & $packer -ModuleKey test -AppKey worker -PackageType worker -TargetName worker `
                -Version 1.0.0 -MinWorkerHostVersion 1.0.0 -PayloadPath $payload -OutputPath $zip
            $hashes += (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
        }
        $hashes[0] | Should -Be $hashes[1]
        # The guard must still notice a real content change.
        [IO.File]::WriteAllText((Join-Path $payload 'a.txt'), 'changed')
        & $packer -ModuleKey test -AppKey worker -PackageType worker -TargetName worker `
            -Version 1.0.0 -MinWorkerHostVersion 1.0.0 -PayloadPath $payload -OutputPath $zip
        (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash | Should -Not -Be $hashes[0]
    }
}

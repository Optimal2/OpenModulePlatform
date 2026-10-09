"""Foreground cross-shell/producer contract test; never installs or deploys anything."""
import argparse
import json
import io
import os
from pathlib import Path
import shutil
import subprocess
import zipfile

repo = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument('--output', required=True, type=Path)
args = parser.parse_args()
output = args.output.resolve()
output.mkdir(parents=True, exist_ok=False)
env = os.environ.copy()
env['TEMP'] = env['TMP'] = str(output / 'temp')
Path(env['TEMP']).mkdir()

def run(command):
    result = subprocess.run(command, cwd=repo, env=env, capture_output=True, text=True, timeout=600)
    if result.returncode:
        raise RuntimeError(f'{command}\n{result.stdout}\n{result.stderr}')
    return result.stdout

project = repo / 'scripts/dev/ArtifactDeterminismProbe/ArtifactDeterminismProbe.csproj'
print(run(['dotnet', 'build', str(project), '-c', 'Release', '--nologo', '-v', 'q']), flush=True)
probe = project.parent / 'bin/Release/net10.0/ArtifactDeterminismProbe.dll'
results = []
for shell, producer in [('powershell.exe', 'installer'), ('powershell.exe', 'canonical'),
                        ('pwsh', 'installer'), ('pwsh', 'canonical'), ('dotnet', 'csharp')]:
    if not shutil.which(shell):
        raise RuntimeError(f'Required producer shell missing: {shell}')
    root = output / f'{shell}-{producer}'
    payload = root / 'payload'
    payload.mkdir(parents=True)
    names = ['z.txt', 'A.txt', 'nested/utf8.txt']
    if len(results) % 2:
        names.reverse()
    for name in names:
        path = payload / name
        path.parent.mkdir(exist_ok=True)
        path.write_bytes(('fixture ' + name + ' å漢\n').encode('utf-8'))
        os.utime(path, (1577934246 + 86400 * len(results),) * 2)
    (root / 'config.txt').write_bytes('config å漢\n'.encode('utf-8'))
    if producer == 'csharp':
        run(['dotnet', str(probe), 'pack', str(root)])
    else:
        run([shell, '-NoProfile', '-File', str(repo / 'scripts/dev/pack-determinism-fixture.ps1'),
             '-Root', str(root), '-Producer', producer])
    hashes = json.loads(run(['dotnet', str(probe), 'hash', str(root)]))
    # Independent reader verifies CRCs and both archive layers, not just equality
    # between producers that could otherwise share the same encoding mistake.
    with zipfile.ZipFile(root / 'package.zip') as package:
        nested = package.read('payload/artifact.zip')
        for archive in (package, zipfile.ZipFile(io.BytesIO(nested))):
            assert archive.testzip() is None, 'ZIP CRC failed'
            assert archive.namelist() == sorted(archive.namelist()), 'Non-ordinal entry order'
            for entry in archive.infolist():
                assert entry.date_time == (1980, 1, 1, 0, 0, 0)
                assert entry.compress_type == zipfile.ZIP_STORED
                assert entry.external_attr == 0
                if entry.filename in ('omp-artifact-package.json', 'omp-worker-plugin.json'):
                    content = archive.read(entry)
                    assert not content.startswith(b'\xef\xbb\xbf')
                    assert b'\r' not in content and b'\n' not in content
                    assert json.loads(content)['formatVersion'] == 1
    results.append(dict(Shell=shell, Producer=producer, **hashes))
    print(json.dumps(results[-1]), flush=True)
(output / 'hashes.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
assert len({r['PayloadHash'] for r in results}) == 1, 'Payload hashes differ'
assert len({r['ZipHash'] for r in results}) == 1, 'Zip hashes differ'
# A runner must match the recorded local wire contract, not merely agree with
# its own other producers. CI therefore also proves equality on another machine.
assert results[0]['PayloadHash'] == '02aeb6e185f6897c6ce0b462804dcfd22decce4f63815ccd629ebf803a62c858'
assert results[0]['ZipHash'] == '8995639c22fcf77c81cfa710ceefde59caded062b36573d0190815ebe4995814'

# A real payload change must still change both identities.
(payload / 'A.txt').write_text('changed', encoding='utf-8')
run(['dotnet', str(probe), 'pack', str(root)])
shutil.rmtree(root / 'extracted')
shutil.rmtree(root / 'unpacked')
changed = json.loads(run(['dotnet', str(probe), 'hash', str(root)]))
assert changed['PayloadHash'] != results[-1]['PayloadHash']
assert changed['ZipHash'] != results[-1]['ZipHash']
print('PASS: all shells/producers have identical ArtifactHash and zip SHA-256.', flush=True)

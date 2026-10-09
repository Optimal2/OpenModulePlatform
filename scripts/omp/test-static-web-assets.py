"""Exercise the production MSBuild normalizer with deliberately permuted endpoints."""
import argparse
import copy
import json
import os
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument('--output', required=True, type=Path)
args = parser.parse_args()
root = args.output.resolve()
root.mkdir(parents=True, exist_ok=False)
(root / 'temp').mkdir()
env = os.environ.copy()
env['TEMP'] = env['TMP'] = str(root / 'temp')
repo = Path(__file__).resolve().parents[2]
project = ET.Element('Project')
ET.SubElement(project, 'Import', Project=str(repo / 'build/OpenModulePlatform.DeterministicStaticWebAssets.targets'))
ET.ElementTree(project).write(root / 'normalize.proj', encoding='utf-8', xml_declaration=True)

endpoint = {
    'Route': 'asset.å.js', 'AssetFile': 'asset.å.js.gz',
    'Selectors': [{'Name': 'Content-Encoding', 'Value': 'gzip', 'Quality': '0.1'}],
    'ResponseHeaders': [
        {'Name': 'Last-Modified', 'Value': 'yesterday'},
        {'Name': 'ETag', 'Value': '"sha256+/="'},
        {'Name': 'Content-Type', 'Value': 'text/javascript'}],
    'EndpointProperties': [{'Name': 'z', 'Value': 'last'}, {'Name': 'a', 'Value': 'first'}],
    # Unknown arrays must keep their order; only documented sets are sortable.
    'FutureOrderedField': ['z', 'a']
}
manifest = {'Version': 1, 'ManifestType': 'Publish', 'Endpoints': []}
for route in ['z.js', 'A.js', 'a.js', 'å.js']:
    item = copy.deepcopy(endpoint)
    item['Route'] = route
    manifest['Endpoints'].append(item)
permuted = copy.deepcopy(manifest)
permuted['Endpoints'].reverse()
for item in permuted['Endpoints']:
    for key in ['Selectors', 'ResponseHeaders', 'EndpointProperties']:
        item[key].reverse()
    next(h for h in item['ResponseHeaders'] if h['Name'] == 'Last-Modified')['Value'] = 'tomorrow'


def normalize(name, data):
    path = root / f'{name}.json'
    path.write_text(json.dumps(data, ensure_ascii=False), encoding='utf-8')
    command = ['dotnet', 'msbuild', str(root / 'normalize.proj'), '-nologo', '-v:minimal',
               '-t:OmpNormalizeStaticWebAssetsPublishEndpointsLastModified',
               f'-p:StaticWebAssetEndpointsPublishManifestPath={path}']
    subprocess.run(command, check=True, timeout=120, env=env)
    content = path.read_bytes()
    subprocess.run(command, check=True, timeout=120, env=env)
    assert content == path.read_bytes(), 'Normalization is not idempotent'
    return content


first = normalize('first', manifest)
second = normalize('permuted', permuted)
assert first == second, 'Endpoint/set order or timestamp changes published bytes'
actual = json.loads(first)
assert len(actual['Endpoints']) == len(manifest['Endpoints'])
assert {e['Route'] for e in actual['Endpoints']} == {e['Route'] for e in manifest['Endpoints']}
for item in actual['Endpoints']:
    assert item['FutureOrderedField'] == ['z', 'a']
    assert item['Selectors'] == endpoint['Selectors']
    headers = {h['Name']: h['Value'] for h in item['ResponseHeaders']}
    assert headers['Last-Modified'] == 'Sat, 01 Jan 2000 00:00:00 GMT'
    assert headers['ETag'] == '"sha256+/="'
    assert headers['Content-Type'] == 'text/javascript'
changed = copy.deepcopy(manifest)
changed['Endpoints'][0]['AssetFile'] = 'changed.js.gz'
assert normalize('changed', changed) != first, 'A real asset change lost its identity'
print('PASS: endpoint ordering, timestamps, idempotence, semantics and content sensitivity.')

# A consumer and its OMP reference both carry this helper project and pass the
# same isolated root. Their obj/bin must not collide within that build graph.
paths = []
for name in ['platform', 'consumer with spaces']:
    project_path = root / name / 'DeterministicRazor.csproj'
    project_path.parent.mkdir()
    project_path.write_bytes((repo / 'build/DeterministicRazor/DeterministicRazor.csproj').read_bytes())
    result = subprocess.run([
        'dotnet', 'msbuild', str(project_path), '-nologo',
        '-getProperty:BaseIntermediateOutputPath,BaseOutputPath,PathMap',
        f'-p:OmpIsolatedBuildRoot={root / "isolated"}'
    ], check=True, capture_output=True, text=True, timeout=120, env=env)
    properties = json.loads(result.stdout)['Properties']
    for key in ['BaseIntermediateOutputPath', 'BaseOutputPath']:
        path = Path(properties[key]).resolve()
        assert path.is_relative_to(root / 'isolated'), f'{key} escaped the caller root'
        paths.append(path)
    assert '=/_/omp-build/obj/' in properties['PathMap']
    assert '=/_/omp-build/bin/' in properties['PathMap']
assert len(set(paths)) == 4, 'Consumer/platform helper copies share obj/bin'
print('PASS: consumer and platform Razor helper copies have distinct isolated outputs.')

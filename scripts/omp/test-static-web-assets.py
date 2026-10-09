"""Exercise the production MSBuild normalizer with deliberately permuted endpoints."""
import argparse
import copy
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
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

# The normalizer must tolerate the shapes a real publish manifest can carry:
# endpoints that omit Selectors/ResponseHeaders/EndpointProperties entirely, and
# members that are JSON null. Neither may crash (MSB4018/NRE) nor change bytes
# when the endpoint order is permuted.
sparse = {'Route': 'missing.js', 'AssetFile': 'missing.js', 'FutureOrderedField': ['z', 'a']}
nulls = {
    'Route': 'nulls.js', 'AssetFile': 'nulls.js',
    'Selectors': None, 'ResponseHeaders': None, 'EndpointProperties': None,
    'FutureOrderedField': ['z', 'a'],
}
sparse_manifest = {'Version': 1, 'ManifestType': 'Publish', 'Endpoints': [copy.deepcopy(sparse), copy.deepcopy(nulls)]}
sparse_permuted = copy.deepcopy(sparse_manifest)
sparse_permuted['Endpoints'].reverse()
sparse_first = normalize('sparse', sparse_manifest)
sparse_second = normalize('sparse-permuted', sparse_permuted)
assert sparse_first == sparse_second, 'Null/missing members changed published bytes'
sparse_actual = json.loads(sparse_first)
assert {e['Route'] for e in sparse_actual['Endpoints']} == {'missing.js', 'nulls.js'}
for item in sparse_actual['Endpoints']:
    assert item['FutureOrderedField'] == ['z', 'a']
    assert item['AssetFile'] in ('missing.js', 'nulls.js')
print('PASS: null and missing endpoint members are tolerated deterministically.')

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

# A consumer does not live inside the OMP repository: it imports the shared build
# files from its own Directory.Build.targets. The fixture is therefore built in
# the real system temp, OUTSIDE this repository. Under --output it would sit
# inside the repo and inherit this repository's Directory.Build.props, whose
# repo-root PathMap maps the two source paths below to different virtual paths
# and makes the two builds differ (reproduced when local-ci.ps1 passed --output
# under artifacts/). Build a tiny Razor web project from two different source
# paths (one with spaces and a longer name) and require identical assembly and
# PDB bytes, proving the determinism wrapper maps the consumer's project
# directory to a virtual path at both locations.
consumer_root = Path(tempfile.mkdtemp(prefix='omp-consumer-fixture-')).resolve()
fixture = consumer_root / 'template'
(fixture / 'Pages').mkdir(parents=True)
(fixture / 'consumer.csproj').write_text(
    '<Project Sdk="Microsoft.NET.Sdk.Web">\n'
    '  <PropertyGroup>\n'
    '    <TargetFramework>net10.0</TargetFramework>\n'
    '    <ContinuousIntegrationBuild>true</ContinuousIntegrationBuild>\n'
    '    <Deterministic>true</Deterministic>\n'
    '    <DebugType>portable</DebugType>\n'
    '    <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>\n'
    '    <EnableSourceControlManagerQueries>false</EnableSourceControlManagerQueries>\n'
    '  </PropertyGroup>\n'
    '</Project>\n',
    encoding='utf-8')
(fixture / 'Directory.Build.targets').write_text(
    '<Project>\n'
    '  <Import Project="' + str(repo / 'build/OpenModulePlatform.DeterministicStaticWebAssets.targets').replace('\\', '/') + '" />\n'
    '</Project>\n',
    encoding='utf-8')
(fixture / 'Pages' / 'Index.cshtml').write_text(
    '@page\n<h1>Determinism fixture</h1>\n<p>@(1 + 1)</p>\n',
    encoding='utf-8')
# Microsoft.NET.Sdk.Web sets OutputType=Exe, so the fixture needs an entry point
# just like a real consumer module; Razor Pages render from the .cshtml above.
(fixture / 'Program.cs').write_text(
    'using Microsoft.AspNetCore.Builder;\n'
    'using Microsoft.Extensions.DependencyInjection;\n'
    'var builder = WebApplication.CreateBuilder(args);\n'
    'builder.Services.AddRazorPages();\n'
    'var app = builder.Build();\n'
    'app.MapRazorPages();\n'
    'app.Run();\n',
    encoding='utf-8')


def build_consumer(source, isolated):
    subprocess.run([
        'dotnet', 'build', str(source / 'consumer.csproj'),
        '-c', 'Release', '--nologo',
        '-p:OmpIsolatedBuildRoot=' + str(isolated),
    ], check=True, capture_output=True, text=True, timeout=300, env=env)


consumer_bytes = {}
try:
    for leg in ['consumer-a', 'consumer with spaces and longer path']:
        source = consumer_root / leg
        shutil.copytree(fixture, source)
        build_consumer(source, consumer_root / (leg + '-isolated'))
        out = source / 'bin' / 'Release' / 'net10.0'
        consumer_bytes[leg] = (
            (out / 'consumer.dll').read_bytes(),
            (out / 'consumer.pdb').read_bytes(),
        )
    assert consumer_bytes['consumer-a'] == consumer_bytes['consumer with spaces and longer path'], \
        'Consumer-like Razor build differs between two source paths'
    print('PASS: consumer-like Razor web build is byte-identical from two source paths.')
finally:
    shutil.rmtree(consumer_root, ignore_errors=True)

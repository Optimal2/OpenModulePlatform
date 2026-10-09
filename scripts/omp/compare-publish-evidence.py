"""Compare complete publish/package evidence across shells or machines."""
import json
import sys
from pathlib import Path


def read(path):
    rows = json.loads(Path(path).read_text(encoding='utf-8-sig'))
    assert rows, f'No measured builds in {path}'
    results = {}
    for row in rows:
        key = (row['Project'], row['Leg'])
        assert key not in results, f'Duplicate build {key}'
        assert row['Files'] and row['PackageSha256'], f'Empty evidence for {key}'
        results[key] = (row['Files'], row['PackageSha256'])
    return results


first, second = map(read, sys.argv[1:])
assert first.keys() == second.keys(), 'Build inventories differ'
for key in first:
    assert first[key] == second[key], f'Publish/package bytes differ for {key}'
print(f'PASS: {len(first)} builds have identical payload and package hashes across runners.')

#!/usr/bin/env python3
"""Import a checksummed native ABI package closure, never a serializer binary."""
from pathlib import Path
import argparse, hashlib, json, shutil, xml.etree.ElementTree as ET, zipfile

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('manifest', type=Path)
args = parser.parse_args()
manifest = json.loads(args.manifest.read_text())
packages = manifest['packages']
feed = root / 'artifacts/native-feed'
feed.mkdir(parents=True, exist_ok=True)
pending = ['Neemle.DBAL.Runtime.NativeBootstrap']
rows = {}
while pending:
    identity = pending.pop()
    if identity in rows:
        continue
    if not identity.startswith('Neemle.DBAL.Runtime.') or identity.endswith('.Json'):
        raise ValueError('Only the native ABI backend is allowed: ' + identity)
    row = packages[identity]
    source = Path(row['archive'])
    digest = hashlib.sha256(source.read_bytes()).hexdigest()
    assert digest == row['sha256'], identity
    with zipfile.ZipFile(source) as archive:
        nuspec = ET.fromstring(archive.read(next(n for n in archive.namelist() if n.endswith('.nuspec'))))
        dependencies = []
        for node in nuspec.iter():
            if node.tag.rsplit('}', 1)[-1] == 'dependency':
                name, version = node.attrib['id'], node.attrib['version'].strip('[]')
                assert packages[name]['version'] == version, (name, version)
                dependencies.append(name)
        archive.testzip()
    target = feed / source.name
    if target.exists():
        assert hashlib.sha256(target.read_bytes()).hexdigest() == digest
    else:
        with target.open('xb') as output, source.open('rb') as input_stream:
            shutil.copyfileobj(input_stream, output)
    assert target.stat().st_nlink == 1
    rows[identity] = {'version': row['version'], 'archive': str(target.relative_to(root)), 'sha256': digest, 'dependencies': dependencies}
    pending.extend(dependencies)
proof = {'scope': 'Native ABI dependency only; serializer and generator are built from this checkout.',
         'originManifestSha256': hashlib.sha256(args.manifest.read_bytes()).hexdigest(), 'packages': rows}
(root / 'provenance/native-backend-import.json').write_text(json.dumps(proof, indent=2) + '\n')
print('Imported', len(rows), 'native ABI packages; no EF or serializer package.')

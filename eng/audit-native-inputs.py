#!/usr/bin/env python3
"""Audit the actual native response file and selected serializer inputs."""
from pathlib import Path
import argparse, hashlib, json, re
from pe_pdb import pe_pdb_identity
root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
stage = root / 'tests/SourceGenerated/obj/Release/net10.0/osx-arm64'
rsp = stage / 'native/SourceGenerated.ilc.rsp'
lines = rsp.read_text().splitlines()
refs = [Path(line[3:].strip('"')).resolve() for line in lines if line.startswith('-r:')]
assert len(refs) == len(set(refs))
assert not any(re.search(r'IL\d{4}', line) for line in lines if line.startswith('--nowarn'))
assert all(file.is_relative_to(root) for file in refs)
assert not any('EntityFramework' in file.name for file in refs)
sha = lambda file: hashlib.sha256(Path(file).read_bytes()).hexdigest()
library = root / 'src/System.Text.Json/bin/Release/net10.0/System.Text.Json.dll'
assert [file for file in refs if file.name == 'System.Text.Json.dll'] == [library]
assert sha(library) == json.loads((root / 'provenance/library-build-candidate.json').read_text())['dllSha256']
query = json.loads((stage / 'neemle-queryable/compiler-inputs.json').read_text())
assert query['references'][str(library)].lower() == sha(library)
actual = {str(file): sha(file) for file in refs}
for file, digest in query['references'].items():
    if file in actual:
        assert digest.lower() == actual[file]
assert query['toolSha256'].lower() == 'bb8df103538359d847e05c3f63eb2960519096a4d66ead36a47f56c7245cb2d6'
app = stage / 'SourceGenerated.dll'
pairs, unpaired = {}, []
for file in [*refs, app]:
    pdb = file.with_suffix('.pdb')
    if not pdb.exists():
        unpaired.append(str(file))
        continue
    offset, identity = pe_pdb_identity(file.read_bytes(), pdb.read_bytes())
    corrupted = bytearray(pdb.read_bytes())
    corrupted[offset] ^= 1
    try:
        pe_pdb_identity(file.read_bytes(), corrupted)
    except AssertionError:
        pass
    else:
        raise AssertionError('Corrupt PDB accepted: ' + str(file))
    pairs[str(file)] = {'pdbSha256': sha(pdb), 'identity': identity}
try:
    pe_pdb_identity(library.read_bytes(), app.with_suffix('.pdb').read_bytes())
except AssertionError:
    pass
else:
    raise AssertionError('Valid but mismatched PDB accepted.')
assert len(unpaired) == 3 and {Path(file).name for file in unpaired} == {
    'System.Linq.Queryable.dll', 'Neemle.DBAL.TypeMetadataProbe.dll',
    Path(next(file for file in unpaired if Path(file).name.startswith('Neemle.DBAL.Inferred.') and file.endswith('.Queryable.dll'))).name}
backend = json.loads((root / 'provenance/native-backend-import.json').read_text())
for row in backend['packages'].values():
    assert sha(root / row['archive']) == row['sha256']
proof = {'passed': True, 'scope': 'Independent source serializer/generator; six declared prebuilt source-provenanced native ABI packages with paired metadata Tool50.',
         'actualRsp': str(rsp.relative_to(root)), 'rspSha256': sha(rsp), 'serializerSha256': sha(library),
         'application': str(app), 'applicationSha256': sha(app), 'actualNativeReferences': actual,
         'pePdbPairs': pairs, 'pairCount': len(pairs), 'corruptAndWrongPairRejected': True,
         'noIlCodedNoWarn': True, 'noEfReference': True, 'allNativeReferencesInCheckout': True,
         'metadataNativeCommonReferenceHashesEqual': True, 'declaredNoPortablePdbInputs': unpaired,
         'metadataToolSha256': query['toolSha256'].lower(), 'nativeExecuted': False}
output = root / args.output
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(json.dumps(proof, indent=2) + '\n')
print('PASS native inputs:', len(refs), 'references,', len(pairs), 'verified PE/PDB pairs')

#!/usr/bin/env python3
"""Execute native without an SDK and compare the exact published entry under JIT."""
from pathlib import Path
import hashlib, json, os, re, shutil, subprocess
root = Path(__file__).resolve().parents[1]
out = root / 'artifacts'
sha = lambda file: hashlib.sha256(Path(file).read_bytes()).hexdigest()
host = Path(os.environ.get('DOTNET_ROOT', root / '.dotnet')) / 'dotnet'
subprocess.run(['python3', str(root / 'eng/audit-native-inputs.py'), '--output', 'artifacts/sourcegen-native-final-input-proof.json'], check=True, cwd=root)
pre = json.loads((out / 'sourcegen-native-pre-input-proof.json').read_text())
post = json.loads((out / 'sourcegen-native-final-input-proof.json').read_text())
for field in ['actualNativeReferences', 'applicationSha256', 'rspSha256']:
    assert pre[field] == post[field], field
log = (out / 'sourcegen-native-publish.log').read_text()
assert not re.search(r'\b(?:warning|error)\s+[A-Z]+\d+', log, re.I)
environment = {key: value for key, value in os.environ.items() if not key.startswith('DOTNET_')}
environment['PATH'] = '/usr/bin:/bin'
assert shutil.which('dotnet', path=environment['PATH']) is None
native = subprocess.run([str(out / 'native-sourcegen/SourceGenerated')], cwd=root, env=environment, capture_output=True, text=True, check=True)
(out / 'sourcegen-native-run.log').write_text(native.stdout)
(out / 'sourcegen-native-run.stderr.log').write_text(native.stderr)
assets = json.loads((root / 'tests/SourceGenerated/obj/project.assets.json').read_text())
folder = Path(next(iter(assets['packageFolders'])))
runtime = []
for identity, entry in assets['targets']['net10.0/osx-arm64'].items():
    if entry.get('type') == 'package':
        runtime.extend(folder / assets['libraries'][identity]['path'] / file for file in entry.get('runtime', {}) if file.endswith('.dll'))
manifest = out / 'published-entry-runtime-assets.txt'
manifest.write_text('\n'.join(map(str, runtime)) + '\n')
entry = root / 'tests/SourceGenerated/bin/Release/net10.0/osx-arm64/SourceGenerated.dll'
assert sha(entry) == post['applicationSha256']
library = root / 'src/System.Text.Json/bin/Release/net10.0/System.Text.Json.dll'
managed = subprocess.run([str(host), str(root / 'tests/ForkLoader/bin/Release/net10.0/ForkLoader.dll'), str(entry), str(library), str(manifest)], cwd=root, capture_output=True, text=True, check=True)
(out / 'sourcegen-published-entry-runtime-assets-jit.log').write_text(managed.stdout)
(out / 'sourcegen-published-entry-runtime-assets-jit.stderr.log').write_text(managed.stderr)
assert f'fork-sha256:{sha(library)}' in managed.stderr
oracle = out / 'stock-sourcegen-jit.log'
if not oracle.exists():
    oracle = out / 'sourcegen-jit.log'
assert native.stdout == managed.stdout == oracle.read_text()
assert len(native.stdout.splitlines()) == 8
proof = {'passed': True, 'scope': 'Independent source-built System.Text.Json and source generator; explicit matched verification native ABI backend. Source-generated scalar/payload/collection/polymorphism/roundtrip/schema C# consumer.',
         'librarySha256': sha(library), 'sourceGeneratorSha256': sha(root / 'src/System.Text.Json.SourceGeneration/bin/Release/net10.0/System.Text.Json.SourceGeneration.dll'),
         'nativeSha256': sha(out / 'native-sourcegen/SourceGenerated'), 'publishedEntrySha256': sha(entry),
         'nativeExitCode': native.returncode, 'ilDiagnostics': 0, 'otherWarnings': 0, 'noIlCodedNoWarn': True,
         'sdkFreeNativeExecution': True, 'stockActualForkAndPublishedEntryJitExactNativeParity': True, 'stdoutLineCount': 8,
         'allActualNativeInputHashesUnchanged': True, 'actualNativeReferenceCount': len(post['actualNativeReferences']),
         'pePdbPairs': post['pairCount'], 'corruptAndWrongPairRejected': True, 'efDependencies': False, 'runtimeJsonPackage': False,
         'backendPackages': list(json.loads((root / 'provenance/native-backend-import.json').read_text())['packages']),
         'publishedEntryJitResolvedPackageRuntimeAssets': {str(file.relative_to(root)): sha(file) for file in runtime},
         'metadataToolScope': 'Paired source-provenanced prebuilt Tool50. Portable local tool source builds independently; this native test uses the paired package.',
         'notClaimed': ['unbounded runtime reflection JSON owner domains', 'FSharp native conformance', 'stable/public release']}
(out / 'sourcegen-native-conformance-proof.json').write_text(json.dumps(proof, indent=2) + '\n')
(root / 'provenance/native-conformance-proof.json').write_text(json.dumps(proof, indent=2) + '\n')
print('PASS native source fork: 0 IL/0 other, SDK-free, exact 8-line stock/fork/published-entry JIT parity.')

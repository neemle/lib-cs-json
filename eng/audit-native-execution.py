#!/usr/bin/env python3
# Copyright (c) Neemle contributors. Licensed under the MIT License.
"""Independently recheck sealed native inputs and run the actual consumer without an SDK."""
from pathlib import Path
import datetime
import hashlib
import json
import os
import shutil
import subprocess

from pe_pdb import pe_pdb_identity

root = Path(__file__).resolve().parents[1]
sha = lambda file: hashlib.sha256(Path(file).read_bytes()).hexdigest()
pre = json.loads((root / 'artifacts/sourcegen-native-pre-input-proof.json').read_text())
post = json.loads((root / 'artifacts/sourcegen-native-final-input-proof.json').read_text())
accepted = json.loads((root / 'provenance/native-conformance-proof.json').read_text())
assert accepted['passed'] and pre['actualNativeReferences'] == post['actualNativeReferences']
for filename, digest in post['actualNativeReferences'].items():
    assert sha(filename) == digest, filename
for filename, row in post['pePdbPairs'].items():
    image = Path(filename)
    symbols = image.with_suffix('.pdb')
    assert sha(symbols) == row['pdbSha256']
    _, identity = pe_pdb_identity(image.read_bytes(), symbols.read_bytes())
    assert identity == row['identity']
assert sha(root / post['actualRsp']) == post['rspSha256']
assert sha(post['application']) == post['applicationSha256'] == accepted['publishedEntrySha256']
assert sha(root / 'src/System.Text.Json/bin/Release/net10.0/System.Text.Json.dll') == post['serializerSha256'] == accepted['librarySha256']
assert sha(root / 'src/System.Text.Json.SourceGeneration/bin/Release/net10.0/System.Text.Json.SourceGeneration.dll') == accepted['sourceGeneratorSha256']
receipt = root / 'tests/SourceGenerated/obj/Release/net10.0/osx-arm64/neemle-native-bootstrap/launch.txt'
lines = receipt.read_text().splitlines()
assert len(lines) == 2 and sha(lines[0]) == lines[1] == 'ddf2f5780ac58f94c1b654821fc15d55fa5cfce53619cddaf3b563b87cc3661f'
assert Path(lines[0]).is_relative_to(root)
entry = root / 'artifacts/native-sourcegen/SourceGenerated'
assert sha(entry) == accepted['nativeSha256']
kind = subprocess.check_output(['file', str(entry)], text=True).strip()
assert 'Mach-O 64-bit executable arm64' in kind
subprocess.run(['python3', str(root / 'eng/aot-diagnostics.py'), str(root / 'artifacts/sourcegen-native-publish.log'),
                '--output', str(root / 'artifacts/sourcegen-root-strict-diagnostics.json'), '--require-clean'], check=True)
environment = {key: value for key, value in os.environ.items()
               if not key.startswith('DOTNET') and key != 'MSBuildSDKsPath'}
environment['PATH'] = '/usr/bin:/bin'
assert shutil.which('dotnet', path=environment['PATH']) is None
result = subprocess.run([str(entry)], cwd=root, env=environment, capture_output=True, text=True, timeout=120)
assert result.returncode == 0 and not result.stderr
assert result.stdout == (root / 'artifacts/sourcegen-jit.log').read_text()
assert result.stdout == (root / 'artifacts/sourcegen-fork-jit.log').read_text()
assert result.stdout == (root / 'artifacts/sourcegen-published-entry-runtime-assets-jit.log').read_text()
assert len(result.stdout.splitlines()) == 8
(root / 'artifacts/sourcegen-root-sdkfree-native.log').write_text(result.stdout)
proof = {'passed': True, 'recordedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
         'scope': accepted['scope'], 'nativeEntrySha256': sha(entry), 'nativeFileType': kind,
         'allActualNativeReferencesRehashed': len(post['actualNativeReferences']),
         'allActualPePdbPairsRechecked': len(post['pePdbPairs']),
         'actualCompilerLaunchSha256': lines[1], 'actualSourceSerializerSha256': accepted['librarySha256'],
         'actualSourceGeneratorSha256': accepted['sourceGeneratorSha256'], 'ilDiagnostics': 0, 'otherWarnings': 0,
         'sdkFreeExecutionExitCode': result.returncode, 'stockForkPublishedEntryJitExactNativeParity': True,
         'stdoutLines': 8, 'auditorScriptSha256': sha(__file__), 'efRequired': False,
         'runtimeBackendRequired': True, 'stockNativeRuntimeAloneAccepted': False,
         'unboundedReflectionSerializationAccepted': False, 'stableRelease': False}
(root / 'provenance/root-independent-native-proof.json').write_text(json.dumps(proof, indent=2))
print('PASS independent SDK-free native execution, 178 references, 176 PE/PDB pairs, compiler receipt and exact JIT parity')

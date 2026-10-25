#!/usr/bin/env python3
"""Characterize the current ordinary-API gap; this is not an AOT success test."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path, default=Path('artifacts/ordinary-api-assessment'))
args = parser.parse_args()
output = (root / args.output).resolve()
output.mkdir(parents=True)
host = Path(os.environ.get('DOTNET_ROOT', root / '.dotnet')) / 'dotnet'
environment = os.environ | {'DOTNET_ROOT': str(host.parent), 'NUGET_PACKAGES': str(root / '.nuget/packages'), 'DOTNET_PROCESSOR_COUNT': '2'}
sha = lambda path: hashlib.sha256(Path(path).read_bytes()).hexdigest()
library = root / 'src/System.Text.Json/bin/Release/net10.0/System.Text.Json.dll'
original_library_hash = sha(library)


def run(label, command, require_success=True):
    result = subprocess.run(command, cwd=root, env=environment, capture_output=True)
    (output / (label + '.log')).write_bytes(result.stdout)
    (output / (label + '.stderr.log')).write_bytes(result.stderr)
    (output / (label + '-exit.json')).write_text(json.dumps({'exitCode': result.returncode}))
    if require_success:
        assert result.returncode == 0, (label, result.stderr.decode(), result.stdout.decode()[-5000:])
    return result


project = 'tests/OrdinaryApi/OrdinaryApi.csproj'
run('managed-build', [str(host), 'build', project, '-c', 'Release', '-p:PublishAot=false', '-bl:' + str(output / 'managed-build.binlog')])
app = root / 'tests/OrdinaryApi/bin/Release/net10.0/OrdinaryApi.dll'
oracle = output / 'stock-oracle'
oracle.mkdir()
for suffix in ['.dll', '.runtimeconfig.json']:
    shutil.copy2(app.with_suffix(suffix), oracle / ('OrdinaryApi' + suffix))
stock = run('stock-jit', [str(host), str(oracle / app.name)])
fork = run('isolated-fork-jit', [str(host), str(root / 'tests/ForkLoader/bin/Release/net10.0/ForkLoader.dll'), str(app), str(library)])
assert stock.stdout == fork.stdout and len(stock.stdout.splitlines()) == 4
assert ('fork-sha256:' + original_library_hash).encode() in fork.stderr
assert sha(library) == original_library_hash

# Native restore can add SDK-only edges to a referenced project's source lock.
# Retain that observation and restore the original lock even when staging fails.
lock = root / 'src/System.Text.Json/packages.lock.json'
original_lock = lock.read_bytes()
flags = ['-p:PublishAot=true', '-p:SelfContained=true', '-p:_TrimmerShowRedundantSuppressions=true']
try:
    run('native-restore', [str(host), 'restore', project, '-r', 'osx-arm64', *flags])
    stage = run('native-strict-stage', [str(host), 'build', project, '-c', 'Release', '-r', 'osx-arm64', '--no-restore', *flags,
                '-t:Build,_ComputeResolvedCopyLocalPublishAssets,_ComputeAssembliesToCompileToNative,WriteIlcRspFileForCompilation',
                '-bl:' + str(output / 'native-strict-stage.binlog')], require_success=False)
finally:
    (output / 'library-packages.lock.native-stage.json').write_bytes(lock.read_bytes())
    lock.write_bytes(original_lock)

text = (stage.stdout + stage.stderr).decode()
codes = sorted(set(re.findall(r'\b(?:warning|error) (IL\d+)\b', text)))
assert stage.returncode != 0 and {'IL2026', 'IL3050'} <= set(codes), text[-10000:]
assert 'Generating native code' not in text
assert sha(library) == original_library_hash
source = root / 'tests/OrdinaryApi/Program.cs'
assert 'JsonSerializable' not in source.read_text() and 'JsonSerializerContext' not in source.read_text()
owners = root / 'tools/Neemle.DBAL.RuntimeContracts/NativeJsonAttributeOwners.cs'
proof = {
    'managedOrdinaryApiParity': True, 'actualForkLibrarySha256': original_library_hash,
    'actualForkBindingVerifiedByIsolatedLoader': True,
    'stockAndForkOutputSha256': hashlib.sha256(stock.stdout).hexdigest(), 'ordinaryApiCases': 4,
    'noApplicationTypeListOrContext': True, 'nativeAotSupportEstablished': False,
    'nativeExecuted': False, 'nativeCompilerLaunched': False,
    'referencedLibrarySourceLockRestored': lock.read_bytes() == original_lock,
    'strictNativeStageExit': stage.returncode, 'preservedDiagnosticCodes': codes,
    'gap': 'Ordinary generic Serialize/Deserialize retain upstream Requires contracts. Current finite JSON owners come from context/type-info contracts; an automatic ordinary callsite and safe invocation route is not implemented.',
    'inputs': {str(path.relative_to(root)): sha(path) for path in [source, root / project, owners, root / 'eng/UseSourceLibrary.targets', library]},
    'supportClaim': 'Failing capability characterization, not a successful AOT sample or warning waiver.'
}
(output / 'assessment.json').write_text(json.dumps(proof, indent=2) + '\n')
print('CHARACTERIZED: ordinary fork JIT parity; native API remains blocked by genuine ' + ','.join(codes))

#!/usr/bin/env python3
"""Execute stock, app-local and independently isolated fork JIT witnesses."""
from pathlib import Path
import hashlib, json, os, subprocess
root = Path(__file__).resolve().parents[1]
out = root / 'artifacts'
out.mkdir(exist_ok=True)
dotnet = Path(os.environ.get('DOTNET_ROOT', root / '.dotnet')) / 'dotnet'
env = os.environ | {'DOTNET_ROOT': str(dotnet.parent), 'NUGET_PACKAGES': str(root / '.nuget/packages'), 'DOTNET_PROCESSOR_COUNT': '2'}
def run(name, args):
    result = subprocess.run([str(dotnet), *map(str, args)], cwd=root, env=env, capture_output=True, text=True)
    (out / (name + '.log')).write_text(result.stdout)
    (out / (name + '.stderr.log')).write_text(result.stderr)
    assert result.returncode == 0, (name, result.returncode, result.stderr)
    return result
for name in ['SourceGenerated', 'BindingProbe', 'ForkLoader']:
    run(name + '-managed-build', ['build', f'tests/{name}/{name}.csproj', '-c', 'Release', '-p:PublishAot=false'])
app = root / 'tests/SourceGenerated/bin/Release/net10.0/SourceGenerated.dll'
library = root / 'src/System.Text.Json/bin/Release/net10.0/System.Text.Json.dll'
digest = hashlib.sha256(library.read_bytes()).hexdigest()
# A copied consumer with its original runtimeconfig but no app-local serializer
# is an independent stock-runtime oracle, without modifying the shared runtime.
oracle = out / 'stock-jit-oracle'
oracle.mkdir(exist_ok=True)
for suffix in ['.dll', '.runtimeconfig.json']:
    target = oracle / ('SourceGenerated' + suffix)
    target.write_bytes(app.with_suffix(suffix).read_bytes())
stock = run('stock-sourcegen-jit', [oracle / app.name])
fork = run('sourcegen-actual-library-jit', [app])
isolated = run('sourcegen-isolated-fork-jit', [root / 'tests/ForkLoader/bin/Release/net10.0/ForkLoader.dll', app, library])
binding = run('sourcegen-actual-library-binding', [root / 'tests/BindingProbe/bin/Release/net10.0/BindingProbe.dll'])
selected = json.loads(binding.stdout)
assert selected['sha256'] == digest
assert Path(selected['path']).is_relative_to(root / 'tests/BindingProbe/bin')
assert stock.stdout == fork.stdout == isolated.stdout
assert f'fork-sha256:{digest}' in isolated.stderr
assert len(fork.stdout.splitlines()) == 8
(out / 'managed-conformance-proof.json').write_text(json.dumps({'passed': True, 'scope': 'Source-generated payload, scalar, collection, polymorphic roundtrip and schema; stock versus actual fork JIT.', 'librarySha256': digest, 'binding': selected, 'stdoutSha256': hashlib.sha256(fork.stdout.encode()).hexdigest(), 'lineCount': 8, 'nativeExecuted': False}, indent=2) + '\n')
print('PASS: stock/app-local/isolated fork JIT parity; actual library ' + digest)

#!/usr/bin/env python3
"""Publish the ordinary source-generated consumer using its declared ABI backend."""
from pathlib import Path
import argparse, fcntl, hashlib, json, os, resource, shutil, subprocess, time
root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--lock', type=Path, default=root / 'artifacts/native-publish.lock')
args = parser.parse_args()
resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
out = root / 'artifacts'
host = Path(os.environ.get('DOTNET_ROOT', root / '.dotnet')) / 'dotnet'
env = os.environ | {'DOTNET_ROOT': str(host.parent), 'PATH': str(host.parent) + os.pathsep + os.environ['PATH'],
                   'NUGET_PACKAGES': str(root / '.nuget/packages'), 'DOTNET_PROCESSOR_COUNT': '4',
                   'DOTNET_GCHeapHardLimit': '0x300000000'}
sha = lambda file: hashlib.sha256(Path(file).read_bytes()).hexdigest()
with args.lock.open('a+') as lock:
    fcntl.flock(lock, fcntl.LOCK_EX)
    proof = json.loads((out / 'sourcegen-native-pre-input-proof.json').read_text())
    assert all(sha(file) == digest for file, digest in proof['actualNativeReferences'].items())
    assert sha(proof['application']) == proof['applicationSha256']
    assert shutil.disk_usage(root).free > 5 * 1024**3
    (out / 'native-slot-acquired.json').write_text(json.dumps({'pid': os.getpid(), 'time': time.time(), 'lock': str(args.lock), 'cpuCount': 4, 'heapHardLimit': env['DOTNET_GCHeapHardLimit']}, indent=2))
    command = [str(host), 'publish', 'tests/SourceGenerated/SourceGenerated.csproj', '-c', 'Release', '-r', 'osx-arm64', '--no-restore',
               '-p:PublishAot=true', '-p:SelfContained=true', '-p:IlcSingleThreaded=false', '-p:_TrimmerShowRedundantSuppressions=true',
               '-bl:artifacts/sourcegen-native-publish.binlog', '-o', str(out / 'native-sourcegen')]
    with (out / 'sourcegen-native-publish.log').open('x') as log:
        result = subprocess.run(command, cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT)
    (out / 'sourcegen-native-publish-exit.json').write_text(json.dumps({'exitCode': result.returncode, 'command': command}, indent=2))
    assert result.returncode == 0, 'Inspect artifacts/sourcegen-native-publish.log'
print('Native publication completed; execution and final input audit remain required.')

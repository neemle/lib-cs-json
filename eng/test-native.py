#!/usr/bin/env python3
"""Build, audit, publish and execute the independent C# native witness."""
from pathlib import Path
import argparse, os, subprocess
root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--lock', type=Path, default=root / 'artifacts/native-publish.lock')
args = parser.parse_args()
if not (root / 'provenance/native-backend-import.json').exists():
    raise SystemExit('Import the declared native ABI package manifest with eng/import-native-backend.py first.')
host = Path(os.environ.get('DOTNET_ROOT', root / '.dotnet')) / 'dotnet'
env = os.environ | {'DOTNET_ROOT': str(host.parent), 'NUGET_PACKAGES': str(root / '.nuget/packages'), 'DOTNET_PROCESSOR_COUNT': '4'}
if (root / 'artifacts/sourcegen-native-publish.log').exists():
    raise SystemExit('Existing native evidence is preserved. Archive completed native-test outputs before starting another publication.')
subprocess.run(['python3', 'eng/build.py'], cwd=root, env=env, check=True)
subprocess.run(['python3', 'eng/test-managed.py'], cwd=root, env=env, check=True)
flags = ['-p:PublishAot=true', '-p:SelfContained=true', '-p:_TrimmerShowRedundantSuppressions=true']
commands = {
    'sourcegen-native-restore': [str(host), 'restore', 'tests/SourceGenerated/SourceGenerated.csproj', '-r', 'osx-arm64', *flags],
    'sourcegen-native-stage': [str(host), 'build', 'tests/SourceGenerated/SourceGenerated.csproj', '-c', 'Release', '-r', 'osx-arm64', '--no-restore',
                             '-t:Build,_ComputeResolvedCopyLocalPublishAssets,_ComputeAssembliesToCompileToNative,WriteIlcRspFileForCompilation', *flags,
                             '-bl:artifacts/sourcegen-native-stage.binlog']}
for name, command in commands.items():
    with (root / 'artifacts' / (name + '.log')).open('x') as log:
        subprocess.run(command, cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
subprocess.run(['python3', 'eng/audit-native-inputs.py', '--output', 'artifacts/sourcegen-native-pre-input-proof.json'], cwd=root, env=env, check=True)
subprocess.run(['python3', 'eng/publish-native.py', '--lock', str(args.lock)], cwd=root, env=env, check=True)
subprocess.run(['python3', 'eng/verify-native.py'], cwd=root, env=env, check=True)

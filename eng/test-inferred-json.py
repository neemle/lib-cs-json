#!/usr/bin/env python3
# Copyright (c) Neemle contributors. Licensed under the MIT License.
"""Build and verify inferred ordinary JSON calls with isolated, temporary outputs."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import re
import resource
import subprocess
import tempfile


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', type=Path, default=Path.home() / '.dotnet/dotnet')
    parser.add_argument('--evidence', type=Path, required=True)
    parser.add_argument('--backend-manifest', type=Path, help='Explicit external source backend manifest; no default or package acceptance')
    parser.add_argument('--publish-native', action='store_true')
    args = parser.parse_args()
    if args.publish_native and not args.backend_manifest:
        parser.error('--publish-native requires an explicit --backend-manifest')
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
    root = Path(__file__).resolve().parents[1]
    evidence = args.evidence.resolve()
    evidence.mkdir(parents=True, exist_ok=False)
    dotnet = args.dotnet.resolve(strict=True)
    sdk_version = subprocess.check_output([str(dotnet), '--version'], cwd=root, text=True).strip()
    sdk = dotnet.parent / 'sdk' / sdk_version
    versions = [path.name for path in (dotnet.parent / 'packs/Microsoft.NETCore.App.Ref').iterdir()
                if re.fullmatch(r'10\.[0-9]+\.[0-9]+', path.name)]
    version = max(versions, key=lambda value: tuple(map(int, value.split('.'))))
    references = dotnet.parent / 'packs/Microsoft.NETCore.App.Ref' / version / 'ref/net10.0'
    environment = os.environ | {'DOTNET_ROOT': str(dotnet.parent), 'DOTNET_PROCESSOR_COUNT': '4',
                               'MSBUILDDISABLENODEREUSE': '1', 'UseSharedCompilation': 'false'}
    projects = [('src/System.Text.Json', 'System.Text.Json'), ('src/System.Text.Json.SourceGeneration', 'System.Text.Json.SourceGeneration'),
                ('tools/InterceptionAwareAnalyzer', 'InterceptionAwareAnalyzer'), ('tools/InferredOrdinaryProbe', 'InferredOrdinaryProbe')]
    folders = [root / path for path, _ in projects] + [root / 'tests/InferredOrdinaryApi', root / 'eng', root / 'src/Shared', root / 'tools/SourceAudit']
    sources = {str(path.relative_to(root)): sha(path) for folder in folders for path in folder.rglob('*')
               if path.is_file() and path.suffix in {'.cs', '.csproj', '.props', '.targets', '.py', '.resx'}
               and not {'bin', 'obj', '__pycache__'}.intersection(path.parts)}
    result = {'passed': False, 'nativeAcceptance': False, 'normalPackageAcceptance': False, 'standaloneDistributionAcceptance': False,
              'scope': 'Compiler-inferred closed string Serialize<T>/Deserialize<T> operations; no user type catalog',
              'sdkVersion': sdk_version, 'frameworkVersion': version, 'sourceSha256': sources, 'commands': [],
              'analyzerWarningFiltering': False, 'manualApplicationTypeList': False}
    with tempfile.TemporaryDirectory(prefix='stj-inferred-json-') as temporary:
        work = Path(temporary).resolve()
        try:
            def run(label, command, cwd=root, env=environment):
                command = list(map(str, command))
                with (evidence / (label + '.log')).open('x') as log:
                    process = subprocess.run(command, cwd=cwd, env=env, stdout=log, stderr=subprocess.STDOUT)
                result['commands'].append({'label': label, 'arguments': command, 'exitCode': process.returncode})
                if process.returncode:
                    raise RuntimeError(label + ' failed; inspect its evidence log')
                return (evidence / (label + '.log')).read_text()

            for folder, name in projects:
                command = [dotnet, 'build', root / folder / (name + '.csproj'), '-c', 'Release', '--artifacts-path', work / 'build',
                           '-nr:false', '-m:1', '-p:EmitCompilerGeneratedFiles=true', '-p:CompilerGeneratedFilesOutputPath=' + str(work / 'generated' / name), '-p:NuGetLockFilePath=' + str(work / (name + '.packages.lock.json'))]
                run(name + '-build', command)
            binaries = {name: work / 'build/bin' / name / 'release' / (name + '.dll') for _, name in projects}
            binaries['InterceptionAwareAnalyzer'] = binaries['InterceptionAwareAnalyzer'].with_name('Neemle.SystemTextJson.AotAnalysis.dll')
            result['builtDllSha256'] = {name: sha(path) for name, path in binaries.items()}
            result['builtPdbSha256'] = {name: sha(path.with_suffix('.pdb')) for name, path in binaries.items()}
            generated = work / 'managed-proof'
            output = run('managed-controls', [dotnet, binaries['InferredOrdinaryProbe'], references, binaries['System.Text.Json'],
                         binaries['System.Text.Json.SourceGeneration'], binaries['InterceptionAwareAnalyzer'], root / 'tests/InferredOrdinaryApi/Program.cs', generated])
            result['managed'] = json.loads(output)
            assert result['managed']['Passed'] and len(result['managed']['Output'].splitlines()) == 22
            result['generatedSourceSha256'] = {path.name: sha(path) for path in generated.glob('*.cs')}
            result['consumerDllSha256'] = sha(generated / 'InferredConsumer.dll')
            from inferred_json_input_audit import audit
            result['actualSourceApiProof'] = audit(root, dotnet, sdk, version, work, binaries, run, evidence)
            if args.backend_manifest:
                from inferred_json_native import execute
                result['native'] = execute(root, dotnet, sdk, version, binaries, work, evidence, environment, run,
                                           args.backend_manifest.resolve(strict=True), args.publish_native)
                result['nativeAcceptance'] = result['native']['nativeAcceptance']
            assert all(sha(root / name) == expected for name, expected in sources.items())
            result['passed'] = True
        except Exception as error:
            result['error'] = str(error)
    result['temporaryOutputsRemoved'] = not work.exists()
    result['logsSha256'] = {path.name: sha(path) for path in evidence.glob('*.log')}
    (evidence / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({key: result[key] for key in ['passed', 'nativeAcceptance', 'normalPackageAcceptance', 'temporaryOutputsRemoved']}))
    raise SystemExit(not result['passed'])


if __name__ == '__main__':
    main()

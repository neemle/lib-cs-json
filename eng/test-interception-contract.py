#!/usr/bin/env python3
# Copyright (c) Neemle contributors. Licensed under the MIT License.
"""Execute an isolated Roslyn interceptor/dataflow oracle, then clean its outputs."""
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
    args = parser.parse_args()
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
    root = Path(__file__).resolve().parents[1]
    evidence = args.evidence.resolve()
    evidence.mkdir(parents=True, exist_ok=False)
    dotnet = args.dotnet.resolve(strict=True)
    sdk_version = subprocess.check_output([str(dotnet), '--version'], cwd=root, text=True).strip()
    sdk = dotnet.parent / 'sdk' / sdk_version
    versions = [path for path in (dotnet.parent / 'packs/Microsoft.NETCore.App.Ref').iterdir()
                if re.fullmatch(r'10\.[0-9]+\.[0-9]+', path.name)]
    version = max(versions, key=lambda path: tuple(map(int, path.name.split('.')))).name
    references = dotnet.parent / 'packs/Microsoft.NETCore.App.Ref' / version / 'ref/net10.0'
    cache = Path(os.environ.get('NUGET_PACKAGES', str(Path.home() / '.nuget/packages')))
    stock = cache / 'microsoft.net.illink.tasks' / version / 'analyzers/dotnet/cs/ILLink.RoslynAnalyzer.dll'
    if not stock.is_file():
        raise FileNotFoundError('Restore the selected SDK ILLink analyzer into the shared cache first: ' + str(stock))
    project = root / 'tools/InterceptionAwareAnalyzer'
    sources = {str(path.relative_to(root)): sha(path) for folder in (project, root / 'tools/InterceptionContractProbe')
               for path in folder.rglob('*') if path.is_file() and not {'bin', 'obj', '__pycache__'}.intersection(path.parts)}
    result = {'passed': False, 'scope': 'Static executable-interceptor analyzer prototype; ordinary JSON inference and native acceptance remain incomplete',
              'sdkVersion': sdk_version, 'frameworkReferenceVersion': version, 'stockAnalyzerSha256': sha(stock),
              'roslynSha256': {path.name: sha(path) for path in (sdk / 'Roslyn/bincore').glob('Microsoft.CodeAnalysis*.dll')},
              'sourceSha256': sources, 'builds': [], 'diagnosticFiltering': False,
              'nativeOrFrameworkPublishExecuted': False}
    environment = os.environ | {'DOTNET_ROOT': str(dotnet.parent), 'DOTNET_PROCESSOR_COUNT': '2'}
    with tempfile.TemporaryDirectory(prefix='stj-interception-contract-test-') as temporary:
        work = Path(temporary)
        try:
            for name in ('InterceptionAwareAnalyzer', 'InterceptionContractProbe'):
                command = [str(dotnet), 'build', str(root / 'tools' / name / (name + '.csproj')),
                           '-c', 'Release', '--artifacts-path', str(work), '-nr:false',
                           '-p:RestorePackagesWithLockFile=false']
                process = subprocess.run(command, cwd=root, env=environment, capture_output=True, text=True)
                log = evidence / (name + '-build.log')
                log.write_text(process.stdout + process.stderr)
                result['builds'].append({'name': name, 'exitCode': process.returncode, 'logSha256': sha(log)})
                if process.returncode != 0:
                    raise RuntimeError('Strict prototype build rejected: ' + name)
            analyzer = work / 'bin/InterceptionAwareAnalyzer/release/Neemle.SystemTextJson.AotAnalysis.dll'
            probe = work / 'bin/InterceptionContractProbe/release/InterceptionContractProbe.dll'
            command = [str(dotnet), str(probe), str(references), str(stock), str(analyzer)]
            process = subprocess.run(command, cwd=root, env=environment, capture_output=True, text=True)
            (evidence / 'execution.log').write_text(process.stdout + process.stderr)
            if process.returncode != 0:
                raise RuntimeError('Executable-contract oracle failed: ' + str(process.returncode))
            oracle = json.loads(process.stdout)
            assert oracle['Passed'] and len(oracle['Reports']) == 3 and len(oracle['Dataflow']) == 8
            result['oracle'] = oracle
            result['prototypeAnalyzerSha256'] = sha(analyzer)
            result['probeSha256'] = sha(probe)
            result['changedSources'] = [name for name, expected in sources.items() if sha(root / name) != expected]
            assert not result['changedSources']
            result['passed'] = True
        except Exception as error:
            result['error'] = str(error)
    result['temporaryOutputsRemoved'] = not work.exists()
    (evidence / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({key: result[key] for key in ('passed', 'temporaryOutputsRemoved')}))
    raise SystemExit(not result['passed'])


if __name__ == '__main__':
    main()

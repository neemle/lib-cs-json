#!/usr/bin/env python3
# Copyright (c) Neemle contributors. Licensed under the MIT License.
"""Check the actual compiler command of the maintained source-generated consumer."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import shlex
import shutil
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path, default=Path('artifacts/source-generator-independent-audit'))
parser.add_argument('--binlog', type=Path, default=Path('artifacts/sourcegen-consumer-build.binlog'))
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
output = (root / args.output).resolve()
output.mkdir(parents=True, exist_ok=False)
host = Path(os.environ.get('DOTNET_ROOT', str(root / '.dotnet'))) / 'dotnet'
if not host.is_file():
    installed = shutil.which('dotnet')
    if installed is None:
        raise SystemExit('Install the SDK selected by global.json or provide .dotnet/dotnet.')
    host = Path(installed).resolve()
environment = os.environ | {'DOTNET_ROOT': str(host.parent), 'DOTNET_PROCESSOR_COUNT': '4'}
sha = lambda file: hashlib.sha256(Path(file).read_bytes()).hexdigest()
binlog = (root / args.binlog).resolve()
with (output / 'auditor-build.log').open('x') as log:
    subprocess.run([str(host), 'build', str(root / 'tools/BuildAudit/BuildAudit.csproj'), '-c', 'Release'],
                   cwd=root, env=environment, stdout=log, stderr=subprocess.STDOUT, check=True)
subprocess.run([str(host), str(root / 'tools/BuildAudit/bin/Release/net10.0/BuildAudit.dll'),
                str(binlog), str(output / 'actual-compiler-commands.json')], cwd=root, env=environment, check=True)
commands = json.loads((output / 'actual-compiler-commands.json').read_text())['commands']
matches = []
for row in commands:
    if row['taskName'] != 'Csc':
        continue
    arguments = shlex.split(row['command'])
    if any(argument.startswith(('/out:', '-out:')) and argument.endswith('/SourceGenerated.dll') for argument in arguments):
        matches.append(arguments)
assert len(matches) == 1, 'Rebuild the source-generated consumer to retain its actual Csc command in the binary log.'
arguments = matches[0]
analyzers = [argument.split(':', 1)[1] for argument in arguments if argument.startswith(('/analyzer:', '-analyzer:'))]
json_generators = [Path(path).resolve() for path in analyzers if Path(path).name == 'System.Text.Json.SourceGeneration.dll']
expected = root / 'src/System.Text.Json.SourceGeneration/bin/Release/net10.0/System.Text.Json.SourceGeneration.dll'
assert json_generators == [expected], json_generators
assert all('NoWarn' not in argument and not argument.lower().startswith(('/nowarn:', '-nowarn:'))
           or 'il' not in argument.lower() for argument in arguments)
references = [argument.split(':', 1)[1] for argument in arguments if argument.startswith(('/reference:', '-reference:'))]
json_references = [Path(path).resolve() for path in references if Path(path).name == 'System.Text.Json.dll']
assert len(json_references) == 1 and json_references[0].is_relative_to(root / 'src/System.Text.Json')
generated_options = [argument.split(':', 1)[1] for argument in arguments
                     if argument.startswith(('/generatedfilesout:', '-generatedfilesout:'))]
assert len(generated_options) == 1
generated = Path(generated_options[0])
if not generated.is_absolute():
    generated = root / 'tests/SourceGenerated' / generated
generated = generated.resolve()
assert generated.is_relative_to(root / 'tests/SourceGenerated/obj')
files = list(generated.rglob('*.cs'))
assert files, 'The source generator must have emitted actual consumer source files.'
proof = {'passed': True, 'actualBinlogSha256': sha(binlog), 'actualCscCount': len(matches),
         'auditorScriptSha256': sha(__file__),
         'actualSourceGenerator': str(expected.relative_to(root)), 'sourceGeneratorSha256': sha(expected),
         'jsonSourceGeneratorCount': len(json_generators), 'actualLibraryReference': str(json_references[0].relative_to(root)),
         'actualLibraryReferenceSha256': sha(json_references[0]), 'generatedSourceCount': len(files),
         'generatedSourceHashes': {str(file.relative_to(root)): sha(file) for file in files},
         'sdkJsonGeneratorAbsentFromConsumerCommand': True, 'noIlCodedNoWarn': True,
         'otherCheckoutDependency': False, 'nativeAcceptance': False, 'jitRuntimeBindingVerified': False}
(output / 'proof.json').write_text(json.dumps(proof, indent=2))
(root / 'provenance/source-generator-independent-proof.json').write_text(json.dumps(proof, indent=2))
print('PASS actual Csc selected exactly one locally built JSON generator and the local library reference:', len(files))

#!/usr/bin/env python3
# Copyright (c) Neemle contributors. Licensed under the MIT License.
"""Audit the locally built library without an EF or another checkout dependency."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import xml.etree.ElementTree as ET

from pe_pdb import pe_pdb_identity

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path, default=Path('artifacts/library-independent-audit'))
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
output = (root / args.output).resolve()
output.mkdir(parents=True, exist_ok=False)
sha = lambda file: hashlib.sha256(Path(file).read_bytes()).hexdigest()
host = Path(os.environ.get('DOTNET_ROOT', str(root / '.dotnet'))) / 'dotnet'
if not host.is_file():
    installed = shutil.which('dotnet')
    if installed is None:
        raise SystemExit('Install the SDK selected by global.json or provide .dotnet/dotnet.')
    host = Path(installed).resolve()
environment = os.environ | {'DOTNET_ROOT': str(host.parent), 'DOTNET_PROCESSOR_COUNT': '4'}
sdk_version = json.loads((root / 'global.json').read_text())['sdk']['version']
sdk = host.parent / 'sdk' / sdk_version
assert sdk.is_dir(), 'The SDK selected by global.json must be installed.'
runtime_version = json.loads((root / 'provenance/source-inventory.json').read_text())['upstream']['runtimeVersion']
stock = host.parent / 'shared/Microsoft.NETCore.App' / runtime_version / 'System.Text.Json.dll'
references = sorted((host.parent / 'packs/Microsoft.NETCore.App.Ref' / runtime_version / 'ref/net10.0').glob('*.dll'))
assert stock.is_file() and references
candidate_path = root / 'provenance/library-build-candidate.json'
candidate = json.loads(candidate_path.read_text())
dll = root / 'src/System.Text.Json/bin/Release/net10.0/System.Text.Json.dll'
pdb = dll.with_suffix('.pdb')
assert sha(dll) == candidate['dllSha256'] and sha(pdb) == candidate['pdbSha256']
inventory = json.loads((root / 'provenance/source-inventory.json').read_text())
project = root / 'src/System.Text.Json/System.Text.Json.csproj'
tree = ET.parse(project).getroot()
assert not list(tree.iter('PackageReference')) and not list(tree.iter('ProjectReference'))
assert '/xef/' not in project.read_text() and 'artifacts/' not in project.read_text()
source_inputs = {}
for item in tree.iter():
    if item.tag not in ('Compile', 'EmbeddedResource'):
        continue
    path = (project.parent / item.attrib['Include']).resolve()
    assert path.is_relative_to(root) and path.is_file(), str(path)
    relative = str(path.relative_to(root))
    assert sha(path) == inventory['files'][relative]['sha256'], relative
    source_inputs[relative] = sha(path)
position, identity = pe_pdb_identity(dll.read_bytes(), pdb.read_bytes())
broken = bytearray(pdb.read_bytes())
broken[position] ^= 1
for image, symbols in [(dll.read_bytes(), broken), (stock.read_bytes(), pdb.read_bytes())]:
    try:
        pe_pdb_identity(image, symbols)
    except AssertionError:
        pass
    else:
        raise AssertionError('Corrupt/wrong PE and PDB pair accepted.')


def run(label, command):
    with (output / (label + '.log')).open('x') as log:
        result = subprocess.run(command, cwd=root, env=environment, stdout=log, stderr=subprocess.STDOUT)
    assert result.returncode == 0, label + ' failed; inspect ' + str(output / (label + '.log'))


run('source-audit-tool-build', [str(host), 'build', str(root / 'tools/SourceAudit/SourceAudit.csproj'),
                              '-c', 'Release', '-p:UseSharedCompilation=false'])
run('source-export', [str(host), str(root / 'tools/SourceAudit/bin/Release/net10.0/SourceAudit.dll'),
                      str(pdb), str(output / 'sources')])
documents = json.loads((output / 'sources/documents.json').read_text())
compiled = set()
for document in documents:
    path = Path(document['document'])
    assert path.is_relative_to(root), 'External source document: ' + str(path)
    embedded = Path(document['embeddedFile'])
    algorithm = {'8829d00f-11b8-4213-878b-770e8597ac16': 'sha256',
                 'ff1816ec-aa5e-4d10-87f7-6f4963833460': 'sha1'}[document['hashAlgorithm']]
    assert hashlib.new(algorithm, embedded.read_bytes()).hexdigest() == document['hash'].lower()
    relative = str(path.relative_to(root))
    if relative in source_inputs:
        assert sha(embedded) == source_inputs[relative]
        compiled.add(relative)
assert compiled == {file for file in source_inputs if file.endswith('.cs')}
verification = ET.Element('Project')
task = 'Microsoft.DotNet.ApiCompat.Task.ValidateAssembliesTask'
ET.SubElement(verification, 'UsingTask', TaskName=task,
              AssemblyFile=str(sdk / 'Sdks/Microsoft.NET.Sdk/tools/net10.0/Microsoft.DotNet.ApiCompat.Task.dll'))
target = ET.SubElement(verification, 'Target', Name='Verify')
ET.SubElement(target, task, LeftAssemblies=str(stock), RightAssemblies=str(dll),
              LeftAssembliesReferences=','.join(map(str, references)),
              RightAssembliesReferences=','.join(map(str, references)),
              RoslynAssembliesPath=str(sdk / 'Roslyn/bincore'), EnableRuleCannotChangeParameterName='true')
api_project = output / 'api-compat.proj'
ET.ElementTree(verification).write(api_project, encoding='unicode')
run('api-compat', [str(host), 'msbuild', str(api_project), '-t:Verify', '--nologo'])
assert sha(dll) == candidate['dllSha256'] and sha(pdb) == candidate['pdbSha256']
assert all(sha(root / file) == digest for file, digest in source_inputs.items())
proof = {'passed': True, 'dllSha256': sha(dll), 'pdbSha256': sha(pdb), 'pePdbIdentity': identity,
         'auditorScriptSha256': sha(__file__), 'pePdbReaderSha256': sha(root / 'eng/pe_pdb.py'),
         'stockAssemblySha256': sha(stock), 'runtimeVersion': runtime_version,
         'upstreamCommit': inventory['upstream']['commit'], 'sourceInputCount': len(source_inputs),
         'embeddedSourceDocumentCount': len(documents), 'compiledSourceCount': len(compiled),
         'allDeclaredSourceInputsLocalAndUnchanged': True, 'allSourceChecksumsVerified': True,
         'corruptPdbAndWrongPairRejected': True, 'officialPublicApiAndParameterNamesCompatible': True,
         'requiresEfOrOtherCheckoutForAudit': False, 'sourceInputs': source_inputs,
         'nativeAcceptance': False, 'jitExecutionAcceptance': False}
(output / 'proof.json').write_text(json.dumps(proof, indent=2))
(root / 'provenance/library-independent-proof.json').write_text(json.dumps({
    key: value for key, value in proof.items() if key != 'sourceInputs'}, indent=2))
print('PASS standalone library source, PE/PDB, corruption controls and official public API/parameter names:',
      len(source_inputs), len(documents))

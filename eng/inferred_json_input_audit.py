# Copyright (c) Neemle contributors. Licensed under the MIT License.
"""Source/PDB and public-API audit for the exact ephemeral ordinary-JSON build."""
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET
from pe_pdb import pe_pdb_identity


def audit(root, dotnet, sdk, runtime, work, binaries, run, evidence):
    sha = lambda path: hashlib.sha256(Path(path).read_bytes()).hexdigest()
    run('source-auditor-build', [dotnet, 'build', root / 'tools/SourceAudit/SourceAudit.csproj', '-c', 'Release',
                              '--artifacts-path', work / 'audit-build', '-m:1', '-nr:false'])
    auditor = work / 'audit-build/bin/SourceAudit/release/SourceAudit.dll'
    results = {}
    for name in ['System.Text.Json', 'System.Text.Json.SourceGeneration', 'InterceptionAwareAnalyzer']:
        dll = binaries[name]; pdb = dll.with_suffix('.pdb')
        position, identity = pe_pdb_identity(dll.read_bytes(), pdb.read_bytes())
        corrupt = bytearray(pdb.read_bytes()); corrupt[position] ^= 1
        for image, symbols in [(dll.read_bytes(), corrupt), (binaries['InferredOrdinaryProbe'].read_bytes(), pdb.read_bytes())]:
            try: pe_pdb_identity(image, symbols)
            except AssertionError: pass
            else: raise ValueError('Corrupt/wrong PE-PDB pair accepted: ' + name)
        output = work / 'embedded-source' / name
        run('source-export-' + name, [dotnet, auditor, pdb, output])
        documents = json.loads((output / 'documents.json').read_text())
        actual = {}
        for row in documents:
            original = Path(row['document'])
            embedded = Path(row['embeddedFile'])
            algorithm = {'8829d00f-11b8-4213-878b-770e8597ac16': 'sha256', 'ff1816ec-aa5e-4d10-87f7-6f4963833460': 'sha1'}[row['hashAlgorithm']]
            if hashlib.new(algorithm, embedded.read_bytes()).hexdigest() != row['hash'].lower():
                raise ValueError('Embedded source checksum mismatch: ' + str(original))
            if not original.is_relative_to(root) and not original.is_relative_to(work):
                raise ValueError('External source compiler input: ' + str(original))
            physical = original
            if not physical.is_file() and original.is_relative_to(work):
                candidates = [path for path in (work / 'generated' / name).rglob(original.name) if sha(path) == sha(embedded)]
                if len(candidates) != 1:
                    raise ValueError('Generated embedded source has no unique actual emitted file: ' + str(original))
                physical = candidates[0]
            if not physical.is_file() or sha(physical) != sha(embedded):
                raise ValueError('Compiled source differs from actual source input: ' + str(original))
            actual[str(original)] = sha(embedded)
        results[name] = {'dllSha256': sha(dll), 'pdbSha256': sha(pdb), 'identity': identity,
                         'compiledSourceSha256': actual, 'corruptAndWrongPairRejected': True}

    stock = dotnet.parent / 'shared/Microsoft.NETCore.App' / runtime / 'System.Text.Json.dll'
    reference_directory = dotnet.parent / 'packs/Microsoft.NETCore.App.Ref' / runtime / 'ref/net10.0'
    project = ET.Element('Project')
    task = 'Microsoft.DotNet.ApiCompat.Task.ValidateAssembliesTask'
    ET.SubElement(project, 'UsingTask', TaskName=task,
                  AssemblyFile=str(sdk / 'Sdks/Microsoft.NET.Sdk/tools/net10.0/Microsoft.DotNet.ApiCompat.Task.dll'))
    target = ET.SubElement(project, 'Target', Name='Compare')
    ET.SubElement(target, task, LeftAssemblies=str(stock), RightAssemblies=str(binaries['System.Text.Json']),
                  LeftAssembliesReferences=','.join(map(str, [stock.parent, reference_directory])),
                  RightAssembliesReferences=','.join(map(str, [binaries['System.Text.Json'].parent, reference_directory])),
                  RoslynAssembliesPath=str(sdk / 'Roslyn/bincore'), EnableRuleCannotChangeParameterName='true')
    api = work / 'public-api.proj'
    ET.ElementTree(project).write(api, encoding='unicode')
    run('public-api', [dotnet, 'msbuild', api, '-t:Compare', '-m:1', '-nr:false'])
    result = {'passed': True, 'stockRuntimeVersion': runtime, 'stockDllSha256': sha(stock),
              'publicApiAndParameterNamesPreserved': True, 'actualInputs': results}
    (evidence / 'actual-source-api-proof.json').write_text(json.dumps(result, indent=2) + '\n')
    return result

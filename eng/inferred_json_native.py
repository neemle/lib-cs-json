# Copyright (c) Neemle contributors. Licensed under the MIT License.
"""Explicit RAW backend staging for an inferred ordinary-API JSON consumer."""
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import sys
from xml.sax.saxutils import escape
from pe_pdb import pe_pdb_identity


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def execute(root, dotnet, sdk, runtime, binaries, work, evidence, environment, run, manifest_path, publish):
    backend = json.loads(manifest_path.read_text())
    if backend['formatVersion'] != 1 or backend['runtimeVersion'] != runtime or backend['rid'] != 'osx-arm64' or not backend['rawExternalBackend']:
        raise ValueError('The explicit RAW backend must match the selected runtime patch and architecture.')
    core = Path(backend['coreLib']['path']).resolve(strict=True)
    compiler = Path(backend['compiler']['path']).resolve(strict=True)
    tool = Path(backend['metadataTool']['path']).resolve(strict=True)
    expected_backend = {str(core): backend['coreLib']['sha256'], str(core.with_suffix('.pdb')): backend['coreLib']['pdbSha256'],
                        str(tool): backend['metadataTool']['sha256'], **backend['compiler']['payloadSha256']}
    for row in backend['producerProofs']:
        expected_backend[row['path']] = row['sha256']

    def check_backend():
        if any(not Path(path).is_file() or sha(path) != digest for path, digest in expected_backend.items()):
            raise ValueError('Explicit backend source-provenanced payload changed.')
    check_backend()
    result = {'rawExternalBackend': True, 'backendManifestSha256': sha(manifest_path), 'runtimeVersion': runtime,
              'nativeAcceptance': False, 'normalPackageAcceptance': False, 'standaloneDistributionAcceptance': False,
              'backendSha256': expected_backend, 'fixtureManualJsonTypeList': False}
    folder = work / 'native-consumer'
    folder.mkdir()
    shutil.copy2(root / 'global.json', work / 'global.json')
    shutil.copy2(root / 'tests/InferredOrdinaryApi/Parity.cs', folder / 'Parity.cs')
    (folder / 'Program.cs').write_text('System.Console.WriteLine(Entry.Run());\n')
    project = folder / 'OrdinaryJson.csproj'
    project.write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><LangVersion>preview</LangVersion>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors><IsAotCompatible>true</IsAotCompatible><UseSharedCompilation>false</UseSharedCompilation>
<EmbedAllSources>true</EmbedAllSources><DebugType>portable</DebugType><NoWarn></NoWarn><_TrimmerShowRedundantSuppressions>true</_TrimmerShowRedundantSuppressions>
<SuppressTrimAnalysisWarnings>false</SuppressTrimAnalysisWarnings><SuppressAotAnalysisWarnings>false</SuppressAotAnalysisWarnings>
</PropertyGroup><Import Project="''' + escape(str(root / 'eng/UseSourceGenerator.targets')) + '''"/>
<Import Project="''' + escape(str(root / 'eng/InferOrdinaryJson.targets')) + '''"/></Project>''')
    capture = folder / 'capture'; capture.mkdir()
    bridge = folder / 'json-bridge'; bridge.mkdir()
    bridge_paths = bridge / 'paths.txt'
    probe = folder / 'Neemle.DBAL.TypeMetadataProbe.dll'
    wrapper = folder / 'compiler'; wrapper.mkdir()
    expected = wrapper / 'expected-inputs.json'
    receipt = evidence / 'native-launch-receipt.json'
    wrapper_code = '''#!/usr/bin/env python3
import hashlib,json,os,sys
from pathlib import Path
sha=lambda p:hashlib.sha256(Path(p).read_bytes()).hexdigest()
manifest=json.loads(Path(sys.argv[1]).read_text())
for path,digest in manifest['files'].items():
 if sha(path)!=digest:
  if path.endswith('.rsp'): Path(manifest['drift']).write_text(Path(path).read_text())
  raise SystemExit('Native input changed: '+path)
Path(manifest['receipt']).write_text(json.dumps({'passed':True,'compilerSha256':sha(manifest['compiler']),'referenceCount':manifest['referenceCount'],'checkedFileCount':len(manifest['files'])},indent=2)+'\\n')
os.execv(manifest['dotnet'],[manifest['dotnet'],manifest['compiler'],*sys.argv[2:]])
'''
    (wrapper / 'guard.py').write_text(wrapper_code)
    (wrapper / 'ilc').write_text('#!/bin/sh\nset -eu\nexec ' + ' '.join(shlex.quote(str(item)) for item in [sys.executable, wrapper / 'guard.py', expected]) + ' "$@"\n')
    (wrapper / 'ilc').chmod(0o755)
    targets = '''<Project><PropertyGroup>
<_LocalJsonLibrary>''' + escape(str(binaries['System.Text.Json'])) + '''</_LocalJsonLibrary>
<_LocalJsonSourceGenerator>''' + escape(str(binaries['System.Text.Json.SourceGeneration'])) + '''</_LocalJsonSourceGenerator>
<NeemleJsonContractAnalyzer>''' + escape(str(binaries['InterceptionAwareAnalyzer'])) + '''</NeemleJsonContractAnalyzer>
</PropertyGroup><ItemGroup><FrameworkReference Update="Microsoft.NETCore.App" RuntimePackLabels="NativeAOT"/></ItemGroup>
<Target Name="CaptureOrdinaryJsonCompilation" BeforeTargets="CoreCompile" DependsOnTargets="SelectLocallyBuiltJsonLibrary;UseLocallyBuiltJsonSourceGenerator;UseExecutableJsonContractAnalyzer">
<WriteLinesToFile File="''' + escape(str(capture / 'analyzers.txt')) + '''" Lines="@(Analyzer->'%(FullPath)')" Overwrite="true"/>
<WriteLinesToFile File="''' + escape(str(capture / 'compile-refs.txt')) + '''" Lines="@(ReferencePathWithRefAssemblies->'%(FullPath)')" Overwrite="true"/>
</Target><Target Name="CaptureOrdinaryJsonNativeInputs" AfterTargets="ComputeIlcCompileInputs" BeforeTargets="WriteIlcRspFileForCompilation" DependsOnTargets="SelectLocallyBuiltJsonNativeLibrary">
<ItemGroup><IlcReference Remove="@(IlcReference)" Condition="'%(IlcReference.Filename)' == 'System.Private.CoreLib' Or '%(IlcReference.Filename)' == 'System.Text.Json'"/>
<IlcReference Include="''' + escape(str(core)) + '''"/><IlcReference Include="''' + escape(str(binaries['System.Text.Json'])) + '''"/>
<IlcReference Include="''' + escape(str(probe)) + '''" Condition="Exists(''' + "'" + escape(str(probe)) + "')" + '''"/>
</ItemGroup><ReadLinesFromFile File="''' + escape(str(bridge_paths)) + '''" Condition="Exists(''' + "'" + escape(str(bridge_paths)) + "')" + '''"><Output TaskParameter="Lines" ItemName="_InferredJsonBridge"/></ReadLinesFromFile>
<ItemGroup><IlcReference Include="@(_InferredJsonBridge)"/><_MetadataReference Include="@(IlcReference)" Condition="'%(IlcReference.Filename)' != 'Neemle.DBAL.TypeMetadataProbe'"/></ItemGroup>
<PropertyGroup><IlcToolsPath>''' + escape(str(wrapper)) + '''/</IlcToolsPath></PropertyGroup>
<WriteLinesToFile File="''' + escape(str(capture / 'refs.txt')) + '''" Lines="@(_MetadataReference->'%(FullPath)')" Overwrite="true"/>
<WriteLinesToFile File="''' + escape(str(capture / 'entry.txt')) + '''" Lines="@(IlcCompileInput->'%(FullPath)')" Overwrite="true"/>
</Target><Target Name="PrepareOrdinaryJsonRsp" DependsOnTargets="Build;_ComputeResolvedCopyLocalPublishAssets;_ComputeAssembliesToCompileToNative;ComputeIlcCompileInputs;CaptureOrdinaryJsonNativeInputs;WriteIlcRspFileForCompilation"/></Project>'''
    (folder / 'Directory.Build.targets').write_text(targets)
    result['rawTargetsSha256'] = sha(folder / 'Directory.Build.targets')
    result['projectSha256'] = sha(project)
    common = ['-c', 'Release', '-r', backend['rid'], '--artifacts-path', work / 'native-build', '-p:PublishAot=true',
              '-p:PublishAotUsingRuntimePack=true', '-p:SelfContained=true', '-p:RuntimeFrameworkVersion=' + runtime,
              '-p:NoWarn=', '-p:_TrimmerShowRedundantSuppressions=true', '-p:PublishDir=' + str(work / 'publish') + '/', '-nr:false', '-m:1']
    run('native-entry-build', [dotnet, 'build', project, *common])
    analyzers = [Path(line).resolve() for line in (capture / 'analyzers.txt').read_text().splitlines() if line]
    for name in ['System.Text.Json.SourceGeneration', 'InterceptionAwareAnalyzer']:
        selected = [path for path in analyzers if path.name == binaries[name].name]
        if selected != [binaries[name]]: raise ValueError('Actual compiler analyzer mismatch: ' + name)
    if any(path.name == 'ILLink.RoslynAnalyzer.dll' for path in analyzers):
        raise ValueError('The actual compiler must use exactly the executable-contract analyzer.')
    selected = [Path(line).resolve() for line in (capture / 'compile-refs.txt').read_text().splitlines() if Path(line).name == 'System.Text.Json.dll']
    if selected != [binaries['System.Text.Json']]: raise ValueError('Actual compile selected a stock or duplicate serializer reference.')
    result['actualAnalyzerSha256'] = {str(path): sha(path) for path in analyzers}

    def stage(label):
        run(label, [dotnet, 'msbuild', project, '-t:PrepareOrdinaryJsonRsp', '-p:Configuration=Release',
                    '-p:RuntimeIdentifier=' + backend['rid'], '-p:PublishAot=true', '-p:PublishAotUsingRuntimePack=true',
                    '-p:SelfContained=true', '-p:RuntimeFrameworkVersion=' + runtime, '-p:ArtifactsPath=' + str(work / 'native-build'),
                    '-p:NoWarn=', '-p:_TrimmerShowRedundantSuppressions=true', '-p:PublishDir=' + str(work / 'publish') + '/', '-nr:false', '-m:1'])

    stage('initial-native-rsp')
    entry = Path((capture / 'entry.txt').read_text().strip()).resolve(strict=True)
    refs_file = capture / 'refs.txt'
    run('finite-json-bridge', [dotnet, tool, '--json-bridge', refs_file, entry, bridge, dotnet,
                             sdk / 'Roslyn/bincore/csc.dll', bridge_paths, binaries['System.Text.Json.SourceGeneration']])
    stage('json-native-rsp')
    metadata_files = {str(Path(path).resolve()): sha(Path(path)) for path in refs_file.read_text().splitlines() if path}
    run('finite-type-metadata', [dotnet, tool, '--type-metadata-probe', refs_file, entry, probe])
    stage('final-native-rsp')
    responses = list((work / 'native-build').rglob('*.ilc.rsp'))
    if len(responses) != 1: raise ValueError('Expected exactly one actual native response.')
    response = responses[0]

    def inspect():
        text = response.read_text()
        references = [Path(line[3:].strip('"')).resolve(strict=True) for line in text.splitlines() if line.startswith('-r:')]
        if any(re.search(r'(?i)IL\d{4}|(?<!\d)[23]\d{3}(?!\d)', line) for line in text.splitlines() if 'nowarn' in line.lower()):
            raise ValueError('IL-coded NoWarn reached the actual response.')
        if any('--noaotwarn' in line or '--notrimwarn' in line for line in text.splitlines()): raise ValueError('Native analysis was disabled.')
        for selected in [core, binaries['System.Text.Json'], probe]:
            if [path for path in references if path.name == selected.name] != [selected]: raise ValueError('Native source input mismatch: ' + selected.name)
        if any('EntityFramework' in path.name or path.name.startswith('Npgsql') for path in references): raise ValueError('The JSON consumer must have no EF or provider references.')
        actual = {str(path): sha(path) for path in references}
        if any(actual.get(path) != digest for path, digest in metadata_files.items()): raise ValueError('Metadata/native input closure mismatch.')
        return {'responseSha256': sha(response), 'referenceSha256': actual, 'entrySha256': sha(entry)}

    before = inspect()
    result['finalInputs'] = before
    result['actualResponseArguments'] = response.read_text().splitlines()
    raw_pairs = []
    for name in before['referenceSha256']:
        image = Path(name); pdb = image.with_suffix('.pdb')
        if pdb.is_file():
            position, identity = pe_pdb_identity(image.read_bytes(), pdb.read_bytes())
            broken = bytearray(pdb.read_bytes()); broken[position] ^= 1
            try: pe_pdb_identity(image.read_bytes(), broken)
            except AssertionError: pass
            else: raise ValueError('Corrupt PDB accepted: ' + name)
            raw_pairs.append({'path': name, 'pdbSha256': sha(pdb), 'identity': identity})
    result['rawPePdbChecks'] = raw_pairs
    check_backend()
    expected.write_text(json.dumps({'files': {**expected_backend, **before['referenceSha256'], str(entry): sha(entry), str(response): sha(response)},
                                   'compiler': str(compiler), 'dotnet': str(dotnet), 'receipt': str(receipt), 'drift': str(evidence / 'actual-rsp-drift.txt'), 'referenceCount': len(before['referenceSha256'])}))
    entry_jit = run('actual-entry-jit', [dotnet, binaries['InferredOrdinaryProbe'], '--entry', binaries['System.Text.Json'], entry])
    managed = json.loads((evidence / 'managed-controls.log').read_text())
    if entry_jit.rstrip() != managed['Controls'][-1]['Output']: raise ValueError('Actual native entry JIT differs from ordinary source oracle.')
    result['actualEntryJitOutputSha256'] = hashlib.sha256(entry_jit.encode()).hexdigest()
    (evidence / 'native-preflight.json').write_text(json.dumps(result, indent=2) + '\n')
    if publish:
        native_env = environment | {'DOTNET_GCHeapHardLimit': '0x300000000', 'PATH': environment.get('PATH', '')}
        with Path(backend['sharedNativeLock']).open('a') as native_lock:
            fcntl.flock(native_lock, fcntl.LOCK_EX)
            check_backend(); inspect()
            run('native-publish', [dotnet, 'publish', project, *common, '--no-restore', '-o', work / 'publish'], env=native_env)
            run('native-diagnostic-audit', [sys.executable, root / 'eng/aot-diagnostics.py', evidence / 'native-publish.log', '--output', evidence / 'native-diagnostics.json', '--require-clean'])
            if inspect() != before: raise ValueError('Native response or source inputs changed during publication.')
            sdk_free = {key: value for key, value in environment.items() if not key.startswith('DOTNET_')}
            sdk_free['PATH'] = '/usr/bin:/bin'
            if shutil.which('dotnet', path=sdk_free['PATH']) is not None: raise ValueError('SDK-free execution still resolves dotnet.')
            binary = work / 'publish/OrdinaryJson'
            native_text = run('sdk-free-native-run', [binary], env=sdk_free)
            if native_text != entry_jit: raise ValueError('Native differs from actual published-entry JIT/ordinary oracle.')
            result['nativeBinarySha256'] = sha(binary)
            result['nativeOutputSha256'] = hashlib.sha256(native_text.encode()).hexdigest()
            result['nativeAcceptance'] = True
        check_backend()
    return result

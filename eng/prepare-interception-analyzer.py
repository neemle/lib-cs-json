#!/usr/bin/env python3
# Copyright (c) Neemle contributors. Licensed under the MIT License.
"""Prepare one invocation-target change while retaining exact upstream sources."""
from pathlib import Path
import hashlib
import json
import xml.etree.ElementTree as E

root = Path(__file__).resolve().parents[1]
project = root / 'tools/InterceptionAwareAnalyzer'
source = project / 'upstream/ILLink.RoslynAnalyzer/DataFlow/LocalDataFlowVisitor.cs'
original = source.read_text()
old = '=> ProcessMethodCall(operation, operation.TargetMethod, operation.Instance, operation.Arguments, state);'
new = '=> ProcessMethodCall(operation, Neemle.SystemTextJson.AotAnalysis.ExecutableInvocationContract.Resolve(Compilation, operation), operation.Instance, operation.Arguments, state);'
if original.count(old) != 1:
    raise ValueError('The pinned invocation dataflow boundary changed.')
target = project / 'src/LocalDataFlowVisitor.cs'
target.parent.mkdir(exist_ok=True)
contents = original.replace(old, new)
if target.exists() and target.read_text() != contents:
    raise ValueError('Refusing to replace a different prepared invocation visitor.')
target.write_text(contents)
compatibility = []
for relative in ('DataFlow/CapturedReferenceValue.cs', 'DataFlow/InterproceduralState.cs',
                 'DataFlow/LocalStateLattice.cs', 'DataFlow/MethodBodyValue.cs',
                 'TrimAnalysis/TypeProxy.cs', 'TrimAnalysis/GenericParameterProxy.cs',
                 'TrimAnalysis/MethodProxy.cs', 'COMAnalyzer.cs'):
    source_path = project / 'upstream/ILLink.RoslynAnalyzer' / relative
    text = source_path.read_text()
    if relative.startswith('DataFlow/'):
        text = text.replace('bool Equals(object obj)', 'bool Equals(object? obj)')
    text = text.replace('override string ToString()', 'override string? ToString()')
    if relative == 'COMAnalyzer.cs':
        text = text.replace('Enum.IsDefined(typeof(UnmanagedType), unmanagedType)',
                            'Enum.IsDefined(typeof(UnmanagedType), unmanagedType ?? throw new ArgumentNullException("value"))')
    destination = project / 'src' / Path(relative).name
    destination.write_text(text)
    compatibility.append({'path': relative, 'upstreamSha256': hashlib.sha256(source_path.read_bytes()).hexdigest(),
                          'preparedSha256': hashlib.sha256(destination.read_bytes()).hexdigest()})
resource = E.parse(project / 'upstream/ILLink.Shared/SharedStrings.resx').getroot()
wrapper = 'namespace ILLink.Shared { internal static class SharedStrings {\n'
wrapper += 'internal static global::System.Resources.ResourceManager ResourceManager { get; } = new("ILLink.Shared.SharedStrings", typeof(SharedStrings).Assembly);\n'
wrapper += '\n'.join('internal static string ' + row.attrib['name'] + ' => ResourceManager.GetString("' + row.attrib['name'] + '")!;' for row in resource.findall('data'))
(project / 'src/SharedStrings.g.cs').write_text(wrapper + '\n} }\n')
(root / 'provenance/interception-analyzer-preparation.json').write_text(json.dumps({
    'upstreamVisitorSha256': hashlib.sha256(source.read_bytes()).hexdigest(),
    'preparedVisitorSha256': hashlib.sha256(target.read_bytes()).hexdigest(),
    'changedExecutableBoundary': 'VisitInvocation passes the entire selected interceptor symbol to ProcessMethodCall',
    'frameworkSupport': 'Targets net10.0; uses actual framework Index/TypeName/DynamicallyAccessedMemberTypes instead of compiling duplicate BCL source types',
    'nullableContractAlignment': compatibility,
    'diagnosticFiltering': False, 'nativeAcceptance': False,
}, indent=2) + '\n')

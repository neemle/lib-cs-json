# System.Text.Json source fork

This is an independent source home for System.Text.Json, its JSON Schema support,
and its C# source generator. It preserves the upstream `System.Text.Json`
namespace, public API, signed assembly identity and JIT behavior. The source is
pinned to .NET 10.0.13, commit
`6aa72daa24f66c01c3d7b6a32519e9d21396deec`. Original MIT notices and third-party
notices are retained. New finite native metadata helpers use Neemle namespaces.

The serializer and generator are built from local sources. Their projects have
no Entity Framework dependency and do not reference another checkout. Source
and import records are in `provenance/`; generated outputs are ignored by Git.
Additional finite attribute, constructor and inference regression sources are
preserved under `tests/RegressionSources`, with explicit source-only provenance.

## Build and JIT checks

Install .NET SDK 10.0.402, then set `DOTNET_ROOT` to its directory. Alternatively,
put that SDK in `.dotnet/`. No private package feed is needed for managed builds.

```sh
python3 eng/build.py
python3 eng/test-managed.py
python3 eng/audit-library.py
python3 eng/audit-source-generator.py --binlog artifacts/sourcegen-consumer-build.binlog
```

`tests/SourceGenerated` uses ordinary `JsonSerializerContext` declarations and the
generator built here. The source consumer targets explicitly replace the SDK's
framework serializer reference with the local build. The checks compare its
actual app-local assembly with a stock-runtime oracle and an isolated load
context; they verify the selected DLL hash. Schema export is covered too.

## Native backend

The finite reflection adaptations use a generated metadata ABI. They require the
matched Neemle native runtime backend: .NET 10.0.13 CoreLib14, compiler24 and
metadata tool50. A stock NativeAOT runtime alone does not supply that ABI.
The runtime backend is a separate verification dependency; it does not provide
the serializer or its source generator.

An explicit backend manifest can be imported with:

```sh
python3 eng/import-native-backend.py /path/to/native-backend-manifest.json
python3 eng/test-native.py
python3 eng/audit-native-execution.py
```

This copies and verifies only the six runtime ABI packages into
`artifacts/native-feed`. It rejects serializer or EF packages. Ordinary JIT
builds do not reference the backend package. Native publishing uses the
conditional package reference in the source-generated consumer.

The independently built source-generated C# consumer has now been published and
executed without an SDK on macOS ARM64 (`osx-arm64`), using the selected .NET
10.0.13 runtime: **0 IL diagnostics and 0 other warnings**, with exact
stock JIT, fork JIT and published-entry JIT output parity. Its actual native
inputs contain the serializer built here, not a package serializer. The proof is
`provenance/native-conformance-proof.json`: 178 actual references and 176 matched
PE/PDB pairs, including corrupt and wrong-pair negative controls.
These native calls use the `JsonTypeInfo<T>` overloads supplied by an application-declared
`JsonSerializerContext` and `JsonSerializable` declarations. They do **not** prove automatic
AOT support for ordinary `Serialize<T>(value, options)` or `Deserialize<T>(json, options)`
even when `T` is statically closed. The separate ordinary-call implementation and its .NET 10.0.14 proof are described below.

Other runtime identifiers and .NET 10 servicing patches require their own
publish-and-execute checks before claiming support.

| Scope | Evidence |
| --- | --- |
| Local serializer source and public API | Strict build, source/PDB audit, official API compatibility check |
| Local C# generator | Actual compiler/analyzer inputs and emitted source audit |
| JIT payload, scalar, collection, polymorphism and schema | Stock, app-local and isolated fork execution parity |
| Native same C# consumer | SDK-free execution, strict diagnostics and unchanged actual input hashes |
| Closed string `Serialize<T>` / `Deserialize<T>` without application context declarations | Inferred managed controls and a bounded .NET 10.0.14 RAW native consumer pass; normal package distribution is not established |
| Unbounded runtime JSON types, native F# or a stable release | Not established by this consumer |

The portable generator currently targets the selected .NET 10 SDK and is loaded
explicitly by the source consumer. It is a separately built analyzer, rather
than a byte-identical copy of the SDK analyzer. No stable/public release is
declared.

## Provenance and limitations

The historical `provenance/library-independent-proof.json` records the independent official
SDK API compatibility check, actual PE/PDB binding, embedded source checksums,
and corrupt/wrong-pair negative controls. The metadata inference tool is also
included as source under `tools/`; it has no EF project or package reference.
Current ordinary-call source/PDB checks and official .NET 10.0.14 public API/parameter-name compatibility are recorded in `provenance/inferred-ordinary-api-managed-v3/actual-source-api-proof.json` (347 serializer, 57 generator and 138 analyzer compiled documents). Historical proofs keep their original hashes.
Unsupported runtime metadata contracts fail with actionable errors. The fork
does not replace genuine unsafe-operation diagnostics with empty results.

## Inferred ordinary JSON calls

The new source-generator path recognizes actual canonical closed calls to
`JsonSerializer.Serialize<T>(T, JsonSerializerOptions?)` returning a string and
`JsonSerializer.Deserialize<T>(string, JsonSerializerOptions?)`. The caller writes
no `JsonSerializable` attributes or context/type catalog. The generator derives
roots from those callsites, asks the existing upstream parser/emitter for the
transitive serialization graph, and emits typed executable operations. Registering
the inferred factory stores a delegate and does not run application constructors.
This path is enabled for `PublishAot=true`; `NeemleJsonInferOrdinaryCalls=true`
explicitly enables the same path for reflection-disabled managed controls.
The ordinary reflection-enabled JIT compilation stays unchanged by default.

`eng/InferOrdinaryJson.targets` exposes these compile properties and selects the
source-owned executable-contract analyzer. Import it alongside
`eng/UseSourceGenerator.targets` after building the local serializer, generator
and `tools/InterceptionAwareAnalyzer` projects. The analyzer retains all original
ILLink analysis and changes invocation dataflow to the actual compiler-selected
interceptor symbol, including constructed generic, parameter/ref/out/return DAM
and Requires contracts. It never deletes or demotes a diagnostic. Its independent
11-control oracle tests both false-positive and false-negative directions against
the stock analyzer and direct-call dataflow oracles:

```sh
python3 eng/test-interception-contract.py --evidence artifacts/interception-check
python3 eng/test-inferred-json.py --evidence artifacts/ordinary-json-check
```

The managed controls use the actual source-built serializer in an isolated load
context, so a higher servicing file version in the shared framework cannot silently
replace the fork. They cover root and child objects, closed generic/array graphs,
custom read/write converters, options/cache identity, nullable references, readonly
default options, copying options, resolver setter/chain replacement, and resolver
null/throw precedence. The builtin readonly default keeps its public Empty resolver
when reflection is disabled; a private inferred semantic flag participates in
cache equality/hash so explicit Empty resolvers and copies cannot inherit its
fallback. Supplied custom resolvers are never followed by an inferred fallback.
The singleton's privilege is not copied by `new JsonSerializerOptions(default)`.

`provenance/inferred-ordinary-api-native14-v4/result.json` records a separate
actual macOS ARM64 native publication on .NET 10.0.14: **0 IL diagnostics / 0 other
warnings**, SDK-free seven-line execution equal to ordinary reflection-enabled JIT
and the exact published-entry JIT, 177 references and 175 checked PE/PDB pairs.
Actual compiler/RSP/serializer/generator/analyzer hashes are recorded and all
native inputs were rehashed before and after compilation. This deliberately uses
an explicitly selected external source-provenanced private ABI backend through
`--backend-manifest`; it is a RAW conformance test. It has no EF or provider
references. **Normal package and standalone distribution acceptance remain false.**
The historical eight-line context-declared proof above is a different source tuple.

Open generic parameters, direct `object` runtime domains and inaccessible roots
fail actionably. Other overload families (UTF-8/span/stream/async), calls hidden in
prebuilt or generic wrappers, arbitrary runtime-loaded graphs, late registration
of new contracts after a cache miss, and other SDK/RID combinations are not claimed
by this implementation. Unsupported byte overloads retain the original public
Requires diagnostics, and actually used unsafe converters still report genuine
IL2026/IL3050. Unknown runtime types do not become empty contracts. The public
upstream Requires annotations are preserved.

`tests/OrdinaryApi` and its older assessment remain the original failing baseline;
`tests/InferredOrdinaryApi` is the new ordinary-call control. The separate sibling
analyzer prototype does not yet compose the independently tested nested-type-name
resolver patch from the parent project.

For deterministic source builds before and after Git commits,
`IncludeSourceRevisionInInformationalVersion` is disabled intentionally. The
project's declared informational version remains stable; the upstream commit is
recorded separately in provenance. A fresh post-commit build reproduced the
accepted serializer and generator byte hashes exactly.

## Build-output retention

Generated build outputs, the private package cache and duplicate local SDK were
removed during cleanup on 2026-10-25. The source, Git history and small provenance
records remain. Native proof records describe historical executions; their
ignored compiler inputs and binaries are no longer retained. Reinstall the SDK
selected by `global.json` and restore/rebuild the paired backend before repeating
those checks. Use one shared SDK/cache and remove completed temporary outputs.

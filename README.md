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
even when `T` is statically closed. That inference and invocation path is still missing.

Other runtime identifiers and .NET 10 servicing patches require their own
publish-and-execute checks before claiming support.

| Scope | Evidence |
| --- | --- |
| Local serializer source and public API | Strict build, source/PDB audit, official API compatibility check |
| Local C# generator | Actual compiler/analyzer inputs and emitted source audit |
| JIT payload, scalar, collection, polymorphism and schema | Stock, app-local and isolated fork execution parity |
| Native same C# consumer | SDK-free execution, strict diagnostics and unchanged actual input hashes |
| Closed ordinary `Serialize<T>` / `Deserialize<T>` without application context declarations | Not implemented; the strict capability stage reports genuine IL2026 / IL3050 |
| Unbounded runtime JSON types, native F# or a stable release | Not established by this consumer |

The portable generator currently targets the selected .NET 10 SDK and is loaded
explicitly by the source consumer. It is a separately built analyzer, rather
than a byte-identical copy of the SDK analyzer. No stable/public release is
declared.

## Provenance and limitations

`provenance/library-independent-proof.json` records the independent official
SDK API compatibility check, actual PE/PDB binding, embedded source checksums,
and corrupt/wrong-pair negative controls. The metadata inference tool is also
included as source under `tools/`; it has no EF project or package reference.
Unsupported runtime metadata contracts fail with actionable errors. The fork
does not replace genuine unsafe-operation diagnostics with empty results.

## Ordinary API inference gap

`tests/OrdinaryApi` is a failing capability characterization, not a supported AOT
sample. It uses only the ordinary generic serializer APIs, with no application
type list or context declaration. Its four payload/nested/options operations
match stock JIT when the actual source-built serializer is loaded. The strict
native build stage stops on the preserved public Requires contracts (IL2026 and
IL3050); no native compiler or executable is run. Evidence is under
`artifacts/ordinary-api-assessment-v3/assessment.json`.

The current finite tool discovers JSON owners from canonical context/type-info
contracts. It does not infer the closed serialization graph from ordinary
`Serialize<T>` / `Deserialize<T>` callsites. Closing this gap requires an automatic
canonical callsite/type-flow contract, generated typed materialization/member
operations, and a safe native call route that preserves options, converters,
attribute effects and errors. The original JIT API and public Requires annotations
must remain intact. Merely generating attribute roots or removing those
annotations would not establish that behavior. Unknown runtime graphs need an
explicit inference boundary.

For deterministic source builds before and after Git commits,
`IncludeSourceRevisionInInformationalVersion` is disabled intentionally. The
project's declared informational version remains stable; the upstream commit is
recorded separately in provenance. A fresh post-commit build reproduced the
accepted serializer and generator byte hashes exactly.

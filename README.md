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
executed without an SDK: **0 IL diagnostics and 0 other warnings**, with exact
stock JIT, fork JIT and published-entry JIT output parity. Its actual native
inputs contain the serializer built here, not a package serializer. The proof is
`provenance/native-conformance-proof.json`: 178 actual references and 176 matched
PE/PDB pairs, including corrupt and wrong-pair negative controls.

| Scope | Evidence |
| --- | --- |
| Local serializer source and public API | Strict build, source/PDB audit, official API compatibility check |
| Local C# generator | Actual compiler/analyzer inputs and emitted source audit |
| JIT payload, scalar, collection, polymorphism and schema | Stock, app-local and isolated fork execution parity |
| Native same C# consumer | SDK-free execution, strict diagnostics and unchanged actual input hashes |
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

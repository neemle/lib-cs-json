# System.Text.Json fork requirements

Preserve upstream System.Text.Json namespaces, assembly identity, public API, JIT behavior and license notices. New support helpers use Neemle namespaces. Own real library, schema and generator sources; do not depend on EF or paths into another checkout. Pin upstream .NET 10.0.13 source and document each adaptation. Build on the latest selected stable .NET 10 SDK. Native adaptations infer operations from actual serialization contracts, without manual application CLR type catalogs or warning suppression. Preserve exact generated bridge ABI and genuine unsafe-operation diagnostics; unknown contracts fail actionably. Existing cross-project focused evidence does not establish this independent checkout. Build and execute an ordinary source-generated JIT consumer using the actual locally built DLL, then publish and execute native with explicit supported runtime ABI and actual input/source proof. No stable/public release is authorized.

Implemented evidence now covers the local serializer library, schema implementation, explicitly selected source generator, portable metadata-tool build, official serializer API/identity compatibility, actual source/PDB inputs, and the eight-line ordinary C# source-generated JIT/native consumer. Native runs use the declared CF14/compiler24/Tool50 source-provenanced backend; they do not claim stock NativeAOT support without that ABI. Both independent native auditors execute SDK-free and require strict diagnostics plus actual input hashes. The source generator is a portable explicitly selected private analyzer build, not a claim of binary compatibility with the SDK analyzer identity.

Seventeen original finite member, raw-role, constructor, unsafe and F# regression source files are preserved under tests/RegressionSources with exact provenance. They are source-only imports; their origin execution is not claimed for this checkout. Unbounded runtime JSON owner domains, native F# conformance and a stable/public release remain outside the executed consumer's evidence.


Ordinary-call extension (implemented bounded scope): infer canonical closed string
Serialize<T>/Deserialize<T> callsites without user context declarations; generate
actual upstream metadata factories and typed executable operations, preserve
custom resolver precedence and options/cache identity, and retain genuine unsafe
contracts. The source-owned analyzer must analyze the actual compiler-selected
interceptor before invoking its existing dataflow rules, including generic/DAM
contracts, without diagnostic filtering. Ordinary JIT stays unchanged by default.
An executed .NET 10.0.14 RAW native ordinary-call consumer now passes strict zero
warnings on an explicit source-provenanced external backend; this is not normal
package or standalone distribution acceptance. Open/dynamic, unsupported overload,
private/prebuilt-wrapper and runtime-loaded domains remain explicitly bounded.

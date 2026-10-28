# Linux/musl JSON demo

This executable consumes the local serializer, source generator and executable-contract
analyzer through project references and the repository's existing consumer targets.
The library itself remains a library. The example uses ordinary closed generic
`JsonSerializer.Serialize` / `Deserialize<Order>` calls, a POCO and a nested list.
It checks exact JSON and deserialized values and exits nonzero on failure. It has
no application context declaration or manually maintained type catalog.

Reflection-based JSON serialization is disabled. Ordinary-call inference is enabled
for the managed control as well as Native AOT. Compiler, trimming and AOT warnings
remain errors; no diagnostic is suppressed by this sample. This small fixture does
not establish full API compatibility, package delivery, unrestricted runtime type
discovery or reflection freedom across the entire library.

## Managed control

Use the repository-pinned .NET SDK **10.0.403** (`global.json`, no SDK roll-forward).
Run from the repository root; a normal public NuGet connection is required on a
cold package cache. `dotnet` must refer to that SDK.

```sh
dotnet build samples/MuslAotDemo/MuslAotDemo.csproj -c Release \
  -p:TreatWarningsAsErrors=true -bl:artifacts/libc38-managed.binlog
fork_sha256=$(sha256sum src/System.Text.Json/bin/Release/net10.0/System.Text.Json.dll | cut -d ' ' -f 1)
dotnet samples/MuslAotDemo/bin/Release/net10.0/MuslAotDemo.dll \
  --expected-fork-sha256 "$fork_sha256"
```

On macOS use `shasum -a 256` instead of `sha256sum`. The managed program reports
the default host's selected serializer identity, path and SHA256 and requires the
source-built hash. The hash check is outside the JSON paths. A generated module
initializer can fail before these diagnostics if a stock serializer shadows the
fork; retain the exception and capture `DOTNET_HOST_TRACE=1` with
`DOTNET_HOST_TRACEFILE=/absolute/path/host.log` when investigating that failure.
No custom assembly loader is installed by this demo.

Expected JSON, followed by `roundtrip: PASS`:

```json
{"Id":42,"Customer":"musl","Lines":[{"Sku":"json","Quantity":2}]}
```

## Static Native AOT publish

Build on Linux with musl for each target architecture. An Alpine SDK environment
needs .NET SDK **10.0.403**, `clang`, `build-base`, `zlib-dev`, `binutils` and `file`;
install `icu-libs` for the default globalization runtime check. Use a native
ARM64 environment for `linux-musl-arm64` and a native x64 environment for
`linux-musl-x64`, or explicitly record container emulation. A glibc build host
needs a supported musl cross-toolchain; these commands assume the matching Alpine
architecture. A Docker host alone does not provide that toolchain.

Default globalization is preserved. No backend manifest, custom CoreLib or
private feed is imported by this demo. Thus these commands also expose missing
fork/backend delivery prerequisites; they must succeed unchanged before the
requested stock-toolchain tuple can be claimed supported.

```sh
dotnet publish samples/MuslAotDemo/MuslAotDemo.csproj -c Release \
  -r linux-musl-x64 -p:PublishAot=true -p:StaticExecutable=true \
  -p:TreatWarningsAsErrors=true -p:ILLinkTreatWarningsAsErrors=true \
  -p:IlcTreatWarningsAsErrors=true -o artifacts/libc38-linux-musl-x64 \
  -bl:artifacts/libc38-linux-musl-x64.binlog

dotnet publish samples/MuslAotDemo/MuslAotDemo.csproj -c Release \
  -r linux-musl-arm64 -p:PublishAot=true -p:StaticExecutable=true \
  -p:TreatWarningsAsErrors=true -p:ILLinkTreatWarningsAsErrors=true \
  -p:IlcTreatWarningsAsErrors=true -o artifacts/libc38-linux-musl-arm64 \
  -bl:artifacts/libc38-linux-musl-arm64.binlog
```

These produce an executable, not a static library archive. Do not substitute
`NativeLib=Static`. Publish outputs, diagnostics and actual compiler references
must be retained separately for each RID. Rebuild the project references before
publishing; `--no-build` can accidentally reuse incompatible generated files.

After a successful publish, check the actual binary (repeat for each RID):

```sh
binary=artifacts/libc38-linux-musl-x64/MuslAotDemo
file "$binary"
readelf -h "$binary"  # Machine must match x86-64 or AArch64 for its RID.
readelf -l "$binary"  # Must contain no INTERP / PT_INTERP segment.
readelf -d "$binary"  # Must contain no NEEDED / DT_NEEDED entry.
"$binary"            # Must print the expected JSON and roundtrip: PASS.
```

A static PIE is allowed. ELF headers alone do not prove execution or absence of
runtime dynamic loading. Execute again in an empty container/chroot with only
the binary and no SDK, managed runtime or shared libraries before claiming that
stronger result. Default globalization can require ICU at runtime even when no
ELF `DT_NEEDED` entry lists it; report such a failure explicitly.

For a separately labelled **invariant-only fixture**, optionally add
`-p:InvariantGlobalization=true` to a publish command. That changes supported
globalization semantics and cannot replace the default-globalization result or
prove full-library support.

## Recorded validation

The managed strict build passed with SDK 10.0.403 and no warnings or errors.
On the validation host, the default .NET 10.0.14 shared framework replaced the
source-built 10.0.13 serializer (same signed assembly identity, higher file
version), causing `TypeLoadException` for
`Neemle.SystemTextJson.InferredJsonOperations` in the generated module initializer.
That default launch failed. A separate `dotnet exec --fx-version 10.0.10` control
using an already installed runtime selected the source-built DLL, verified its
hash and passed the ordinary JSON roundtrip. It is a distinct runtime tuple,
not a pass for the default 10.0.14 launch.

Native validation used Alpine **3.24**, SDK **10.0.403**, runtime/NativeAOT
**10.0.14**, and official `mcr.microsoft.com/dotnet/sdk:10.0-alpine` images pinned
to these architecture-specific digests:

| Architecture | Immutable image digest |
| --- | --- |
| ARM64 | `sha256:bf500481e8dac63987f2b1e617938f72a77bcfb9b310a234f39483abf35c9bff` |
| x64 | `sha256:60a079c9d3ffc98b2968c3153b995ede679f7d6504e3ae7d2b68225a7fd84f5a` |

The Docker 29.9.1 engine was Linux/aarch64 on an ARM64 macOS host: ARM64 ran on
the engine's native architecture; x64 used emulation. Both builds installed the
Alpine prerequisites above (clang 22.1.3, binutils 2.45.1, musl 1.2.6) and
explicitly used `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0`. No invariant-only
publish was substituted. The actual ILC response selected the source-built
`/work/src/System.Text.Json/bin/Release/net10.0/System.Text.Json.dll`, with
reflection-based JSON disabled and `--warnaserror` enabled.

The current validation uses the corrected nested-DTO consumer, with `Program.cs`
SHA256 `a65d65c9be1127a312b1959c9ed91b509373305bb56cc79380dff9593bfee939`.
The historical default x64 attempt below predates that correction and used
`Program.cs` SHA256 `357b366aa7f8d3c0a1dd68877eca47ea9e52131d6d83b130c343dc1ff7355ef6`.

| RID / attempt | Strict publish | ELF static inspection | Builder execution | Binary-only execution |
| --- | --- | --- | --- | --- |
| `linux-musl-arm64` | PASS, 0 warnings/errors | PASS, AArch64 static PIE, no INTERP/NEEDED | FAIL, exit 134 | FAIL, exit 139 |
| `linux-musl-x64`, historical default, pre-correction source | FAIL, ILC exit 134; 0 warnings, 1 build error | NOT RUN | NOT RUN | NOT RUN |
| `linux-musl-x64`, `IlcSingleThreaded=true` | PASS, 0 warnings/errors | PASS, x86-64 static PIE, no INTERP/NEEDED | FAIL, exit 134 | FAIL, attribute-query exception; timed out and stopped (143) |

Current executable SHA256 values:

```text
ARM64  9ea66dc10551292dd06bbf2d42493bca3a83f2c9c3d1828a2c7d29fe43706ace
x64    80ddc980f8fcc8693bc436dbe2ba8242f0739507038d0d07c10e6c9c9ccc5453
```

The first x64 publish failed inside the stock 10.0.14 ILC with an access
violation in `System.Threading.Tasks.RangeWorker.FindNewWork`, called by
`ILCompiler.ILScanner.CompileMultiThreaded`. The cause is unresolved; the
emulated environment and this observed compiler failure do not identify a
serializer defect or an emulator defect. The selected SDK's
`Microsoft.NETCore.Native.targets` maps `IlcSingleThreaded=true` to
`--parallelism:1`, a standard
[NativeAOT troubleshooting option](https://github.com/dotnet/runtime/blob/main/src/coreclr/nativeaot/docs/troubleshooting.md).
The current x64 verification recompiled the corrected consumer with
`-p:IlcSingleThreaded=true -p:BuildProjectReferences=false`, reusing the unchanged
source-built serializer/generator/analyzer DLLs and PDBs from the earlier x64
build. Their exact hashes and actual compiler references were retained. The
canonical publish commands above build all project references; ARM64 verification
also rebuilt those references. The previous x64 compiler crash remains a
separate observation rather than a result for the corrected consumer.

Both native executables reached the JSON path and failed with:

```text
System.TypeLoadException: Attempted to load a type that was not created during ahead of time compilation.
at Neemle.DBAL.Runtime.NativeAttributeQueries.ReadAttributes(...)
at Neemle.DBAL.Runtime.NativeAttributeQueries.ReadAttribute[TAttribute](...)
at System.Text.Json.Serialization.Metadata.JsonPolymorphismOptions.CreateFromAttributeDeclarations(...)
```

The binary-only image's imported filesystem contained only `MuslAotDemo`, with
no SDK, managed runtime, ICU or other shared libraries, and explicit invariant
mode disabled. Both binary-only runs reproduced that failure. The emulated x64
process remained alive after printing the exception beyond a 30-second ceiling
and was explicitly stopped; its resulting exit 143 is not a spontaneous exit.
Therefore a successful static link
does **not** establish a working native JSON consumer or the full reflection-free
contract. The observed attribute-query failure belongs to the existing native
and RF backlog surfaces (LIBC-4 / LIBC-19); this demo does not repair that runtime
capability. The earlier exception also prevents any wider globalization claim.

Current local ignored evidence is retained in `artifacts/libc38-r2-managed/`,
`artifacts/libc38-r2-native-arm64/` and `artifacts/libc38-r2-native-x64/`:
diagnostic
logs/binlogs, selected compiler inputs/hashes, ILC response files, image and SDK
metadata, `readelf` output and separate run logs/exit codes. Those validation
outputs are generated artifacts, not prerequisites imported by the sample.
Earlier pre-correction evidence remains under `artifacts/libc38-managed/` and
`artifacts/libc38-native-{arm64,x64,x64-singlethreaded}/` and does not attest to
the corrected consumer binary.

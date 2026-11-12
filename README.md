# Neemle.SystemTextJson

A compile-time JSON generator for .NET 10 NativeAOT applications on Linux musl
(x64 and ARM64). The package uses the .NET SDK's `System.Text.Json` reader and
writer and generates direct serialization code for supported source types.

Build the package and run the console demo inside Microsoft's official image:

```sh
docker run --rm --platform linux/arm64 -v "$PWD:/work" -w /work \
  -e DEMO_RID=linux-musl-arm64 \
  mcr.microsoft.com/dotnet/sdk:10.0.403-alpine3.24-aot \
  sh -ec 'apk add --no-cache bash >/dev/null; bash eng/build.sh'
```

Use `linux-musl-x64` on x64. The package is written to the project root; set
`PACKAGE_OUTPUT_DIR` to choose another directory and `PACKAGE_VERSION` to choose
the version. Omit `DEMO_RID` to build only the package. To push it, supply both
`NUGET_SOURCE` and `NUGET_API_KEY` to the container.

The package rewrites supported calls before C# compilation and reports an error
for unsupported types, options, and converter usages. It does not yet provide
full System.Text.Json API or behavioral compatibility. The demo builds without
reflection metadata, with invariant globalization and fatal warnings; it checks
its results and prints `JSON_DEMO_PASS` on success.

MIT. See [LICENSE.TXT](LICENSE.TXT).

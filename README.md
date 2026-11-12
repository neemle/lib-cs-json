# Neemle.SystemTextJson

A compile-time JSON generator for .NET 10 NativeAOT on Linux musl (ARM64 and x64).
It generates serialization code for supported types using `System.Text.Json`.

Build the NuGet package with a stable .NET SDK version 10 or later:

```sh
dotnet build Neemle.SystemTextJson.slnx -c Release
dotnet pack src/JsonGenerator/JsonGenerator.csproj -c Release -o .dev-temp/packages
```

To build and run the demo on a Linux musl ARM64 machine or in Microsoft's
`mcr.microsoft.com/dotnet/sdk:10.0.403-alpine3.24-aot` image:

```sh
dotnet restore samples/JsonAotDemo/JsonAotDemo.csproj -r linux-musl-arm64 \
  --source .dev-temp/packages --source https://api.nuget.org/v3/index.json
dotnet publish samples/JsonAotDemo/JsonAotDemo.csproj -c Release -r linux-musl-arm64 --no-restore
.dev-temp/bin/samples/JsonAotDemo/JsonAotDemo/Release/net10.0/linux-musl-arm64/publish/JsonAotDemo
```

Use `linux-musl-x64` for x64. The package generates code during compilation and
reports unsupported types, options and converter usages. It does not yet provide
full System.Text.Json compatibility. The demo uses invariant globalization,
no reflection metadata and fatal warnings; it prints `JSON_DEMO_PASS` on success.

MIT. See [LICENSE.TXT](LICENSE.TXT).

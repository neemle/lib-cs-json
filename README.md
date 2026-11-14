# Neemle.JSON

Add Neemle.JSON to a .NET 10 or .NET 11 application and keep using `System.Text.Json` as
usual. During the build, the package generates the JSON code needed for
NativeAOT: no manual generator setup or `JsonSerializerContext` is required.

Use it to publish a statically linked Linux musl application, such as an Alpine
Linux application on ARM64 or x64, without JSON runtime reflection metadata.
The package targets `System.Text.Json`; other libraries need their own NativeAOT
support.

## Use

Add the dependency to your project:

```sh
dotnet add package Neemle.JSON
```

Then use `System.Text.Json` normally:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

var json = JsonSerializer.Serialize(new Message { Name = "Ada" });
var message = JsonSerializer.Deserialize<Message>(json)!;

Console.WriteLine(json);         // {"display_name":"Ada"}
Console.WriteLine(message.Name); // Ada

public sealed class Message
{
    [JsonPropertyName("display_name")]
    public string Name { get; set; } = "";
}
```

Publish for Linux musl with the appropriate .NET NativeAOT toolchain installed:

```sh
dotnet publish -c Release -r linux-musl-arm64 \
  -p:PublishAot=true -p:StaticExecutable=true -p:InvariantGlobalization=true \
  -p:JsonSerializerIsReflectionEnabledByDefault=false
```

Use `linux-musl-x64` for x64. The package also passes `--reflectiondata:none` to
NativeAOT and makes build warnings fatal. Publishing downloads and builds the matching
official .NET JSON runtime, so it requires network access to GitHub.

The package uses an MSBuild DLL task and Roslyn from the SDK. NuGet restores
the `Neemle.CSharpComposer` dependency automatically.

## Demo

The .NET 10 demo exercises the 103 `JsonSerializer` serialization and deserialization
overloads. The separate .NET 11 demo also exercises newer JSON features.

## Build and try locally

To build the package locally, use a stable .NET 10 SDK:

```sh
dotnet build Neemle.JSON.slnx -c Release
dotnet pack src/JsonGenerator/JsonGenerator.csproj -c Release -o .dev-temp/packages
```

For another project, add the `PackageReference` shown above and restore with the
absolute path to the local package directory as an additional source:

```sh
dotnet restore path/to/your-app.csproj \
  --source /absolute/path/to/system-text-json/.dev-temp/packages \
  --source https://api.nuget.org/v3/index.json
```

To build and run the included static ARM64 demo on Linux musl, or inside
Microsoft's `mcr.microsoft.com/dotnet/sdk:10.0-alpine-aot` image:

```sh
dotnet restore samples/JsonAotDemo/JsonAotDemo.csproj -r linux-musl-arm64 \
  --source .dev-temp/packages --source https://api.nuget.org/v3/index.json
dotnet publish samples/JsonAotDemo/JsonAotDemo.csproj -c Release -r linux-musl-arm64 --no-restore
.dev-temp/bin/samples/JsonAotDemo/JsonAotDemo/Release/net10.0/linux-musl-arm64/publish/JsonAotDemo
```

Use `linux-musl-x64` for x64. The demo uses invariant globalization and prints
`JSON_DEMO_PASS` after its checks pass.

CI runs the JIT and NativeAOT demos for .NET 10 and .NET 11 in parallel on ARM64 and x64, then compares
their output as JSON values where applicable, ignoring object property order.

MIT. See [LICENSE.TXT](LICENSE.TXT).

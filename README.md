# Neemle.JSON

Neemle.JSON generates JSON serialization code at build time for .NET 10
applications. It lets you use the usual `System.Text.Json` API in a statically
linked NativeAOT application for Linux musl, without runtime reflection metadata
or manually registering a `JsonSerializerContext`.

Use it when deploying to a musl environment such as Alpine Linux on ARM64 or x64
and you want to keep normal `JsonSerializer.Serialize` and `Deserialize` calls.

## Use

Add the package to your .NET 10 application:

```sh
dotnet add package Neemle.JSON --version 0.1.10
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

Publish for Linux musl with the .NET 10 NativeAOT toolchain installed:

```sh
dotnet publish -c Release -r linux-musl-arm64 \
  -p:PublishAot=true -p:StaticExecutable=true -p:InvariantGlobalization=true
```

Use `linux-musl-x64` for x64. The package configures reflection metadata and
fatal build warnings automatically. Publishing downloads and builds the matching
official .NET JSON runtime, so it requires network access to GitHub.

The published 0.1.10 package also requires Git and `sh` during NativeAOT
publishing. The current source removes those package-specific requirements and
uses an MSBuild DLL task, SDK Roslyn, and an automatically restored
`Neemle.CSharpComposer` dependency. These changes are not yet published.

## Demo

The demo exercises all 103 `JsonSerializer` serialization and deserialization
overloads in .NET 10 using a single model.

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

CI runs the JIT and NativeAOT demos in parallel on ARM64 and x64, then compares
their output as JSON values where applicable, ignoring object property order.

MIT. See [LICENSE.TXT](LICENSE.TXT).

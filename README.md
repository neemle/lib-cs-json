# Neemle.JSON

Use ordinary `System.Text.Json` calls in a .NET NativeAOT application targeting
static Linux musl on ARM64 or x64. The package generates JSON serialization code
at build time for supported types, so those operations can run without runtime
reflection metadata or a manually written `JsonSerializerContext`.

This is useful when deploying a small, statically linked application to a musl
system, such as an Alpine Linux container, and you want to keep using the usual
`JsonSerializer.Serialize` and `Deserialize` calls.

## Example

Add the package to your application project:

```xml
<PackageReference Include="Neemle.JSON" Version="0.1.10" />
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

The generator is included in the package and runs during the build; no explicit
analyzer or context registration is needed. For NativeAOT builds, the package
requires reflection metadata to be disabled and warnings to be treated as errors.
It builds a patched JSON runtime from the matching official .NET runtime sources,
so publishing also requires Git and network access to GitHub.

## Demo

The demo exercises all 103 `JsonSerializer` serialization and deserialization
overloads in .NET 10 using a single model.

## Build and try locally

To build the package locally, use a stable .NET SDK version 10 or later:

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

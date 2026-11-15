# Neemle.JSON

Neemle.JSON supports both .NET 10 and .NET 11. Add it to your application
and keep using `System.Text.Json` as usual. During C# compilation, the package generates
the JSON code needed for NativeAOT without rewriting your source files: no manual generator setup or
`JsonSerializerContext` is required.

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

Use `linux-musl-x64` for x64. Set `JsonSerializerIsReflectionEnabledByDefault=false`
to disable reflection-based JSON serialization. Warning settings remain under the
application's control.

The package registers a Roslyn source generator automatically. NuGet restores the
`Neemle.CSharpComposer` dependency automatically. The matching JSON runtime is
prepared by an MSBuild task when publishing for NativeAOT.

For a solution with multiple projects, add Neemle.JSON to every project whose
JSON calls need generation. Adding it only to the application does not generate
code for referenced projects or precompiled dependencies.

For ASP.NET Core HTTP JSON, register the generated resolver before mapping endpoints:

```csharp
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, Neemle.Json.GeneratedJsonResolver.Instance));
```

The resolver combines generated metadata registered by loaded projects. Add the
package to each project that needs JSON generation. Roslyn generators do not see
other generators' output in the same compilation; put generated models in a
referenced project so they are compiled before the consuming project.

NativeAOT publishing normally downloads the matching .NET runtime source from
GitHub. For an offline build, provide the matching official `runtime-<version>`
source archive with `-p:NeemleRuntimeSourceArchive=/path/to/runtime.tar.gz`, or
reuse a previously built matching patched assembly with
`-p:NeemleJsonRuntimeAssembly=/path/to/System.Text.Json.dll`. Restore the SDK and
NuGet dependencies beforehand. The archive or assembly must match the runtime
version and the Neemle.JSON package used for the build. Source builds using the
offline archive disable the NuGet vulnerability-data query for that build.

## Demo

The demos explicitly build with `--reflectiondata:none`. This strict setting applies
to the whole application and is not imposed by the package, so frameworks such as
ASP.NET Core can retain the metadata they need.

The [.NET 10 demo](samples/JsonAotDemo) exercises the 103 `JsonSerializer`
serialization and deserialization overloads. The separate
[.NET 11 demo](samples/JsonAotDemo11) also exercises features introduced in .NET 11.

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

For .NET 11, use the `mcr.microsoft.com/dotnet/sdk:11.0-alpine-aot` image and
the separate demo:

```sh
dotnet restore samples/JsonAotDemo11/JsonAotDemo11.csproj -r linux-musl-arm64 \
  --source .dev-temp/packages --source https://api.nuget.org/v3/index.json
dotnet publish samples/JsonAotDemo11/JsonAotDemo11.csproj -c Release -r linux-musl-arm64 --no-restore
.dev-temp/bin/samples/JsonAotDemo11/JsonAotDemo11/Release/net11.0/linux-musl-arm64/publish/JsonAotDemo11
```

CI runs the JIT and NativeAOT demos for .NET 10 and .NET 11 in parallel on ARM64 and x64, then compares
their output as JSON values where applicable, ignoring object property order.

MIT. See [LICENSE.TXT](LICENSE.TXT).

// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;

if (args.Length is < 2 or > 3) throw new ArgumentException("Expected consumer DLL, locally built System.Text.Json DLL, and optional resolved runtime-asset manifest.");
var consumer = Path.GetFullPath(args[0]);
var json = Path.GetFullPath(args[1]);
var dependencies = args.Length == 3
    ? File.ReadAllLines(args[2]).Where(line => !string.IsNullOrWhiteSpace(line)).Select(Path.GetFullPath).ToDictionary(path => AssemblyName.GetAssemblyName(path).Name!, StringComparer.Ordinal)
    : new Dictionary<string, string>(StringComparer.Ordinal);
dependencies["System.Text.Json"] = json;
var context = new ForkContext(dependencies);
try
{
    var app = context.LoadFromAssemblyPath(consumer);
    var entry = app.EntryPoint ?? throw new InvalidOperationException("Consumer has no entry point.");
    var result = entry.Invoke(null, entry.GetParameters().Length == 0 ? null : [Array.Empty<string>()]);
    if (result is Task task) await task;
    var selected = context.Assemblies.Single(a => a.GetName().Name == "System.Text.Json");
    if (selected.Location != json) throw new InvalidOperationException("The locally built serializer was not selected.");
    Console.Error.WriteLine($"fork-path:{selected.Location}");
    Console.Error.WriteLine($"fork-sha256:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(selected.Location))).ToLowerInvariant()}");
    foreach (var dependency in context.Assemblies.Where(a => a != app && a != selected))
        Console.Error.WriteLine($"dependency-path:{dependency.Location}");
}
finally { context.Unload(); }

sealed class ForkContext(Dictionary<string, string> dependencies) : AssemblyLoadContext(isCollectible: true)
{
    protected override Assembly? Load(AssemblyName name) => dependencies.TryGetValue(name.Name!, out var path) ? LoadFromAssemblyPath(path) : null;
}

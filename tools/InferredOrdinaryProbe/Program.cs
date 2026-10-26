// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

if (args.Length == 3 && args[0] == "--entry")
{
    AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
    using var isolated = new ForkContext(args[1]);
    Assembly actual = isolated.LoadFromAssemblyPath(Path.GetFullPath(args[2]));
    try { actual.EntryPoint!.Invoke(null, [Array.Empty<string>()]); }
    catch (TargetInvocationException error) { throw error.InnerException!; }
    return;
}
if (args.Length != 6) throw new ArgumentException("Expected reference dir, fork library, generator, analyzer, fixture and evidence directory.");
var references = Directory.GetFiles(args[0], "*.dll").Where(path => Path.GetFileName(path) != "System.Text.Json.dll")
    .Append(args[1]).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
var parseOptions = new CSharpParseOptions(LanguageVersion.Preview).WithFeatures([new("InterceptorsNamespaces", "Neemle.SystemTextJson.Generated")]);
var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(args[4]), parseOptions, Path.GetFullPath(args[4]), encoding: System.Text.Encoding.UTF8);
var compilation = CSharpCompilation.Create("InferredConsumer", [tree], references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
Assembly generatorAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[2]));
var generator = (IIncrementalGenerator)Activator.CreateInstance(generatorAssembly.GetType("System.Text.Json.SourceGeneration.JsonSourceGenerator")!)!;
var provider = new OptionsProvider();
GeneratorDriver driver = CSharpGeneratorDriver.Create([generator.AsSourceGenerator()], parseOptions: parseOptions, optionsProvider: provider);
driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation updated, out ImmutableArray<Diagnostic> generatorDiagnostics);
var generated = driver.GetRunResult().GeneratedTrees;
if (generated.Length == 0) throw new InvalidOperationException("No inferred operation source was generated.");
Directory.CreateDirectory(args[5]);
foreach (var source in generated)
    File.WriteAllText(Path.Combine(args[5], Path.GetFileName(source.FilePath)), source.ToString());
var diagnostics = updated.GetDiagnostics().Concat(generatorDiagnostics).Where(d => d.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error).ToArray();
if (diagnostics.Length != 0) throw new InvalidOperationException("Compiler/generator: " + string.Join('\n', diagnostics.Select(d => d.ToString())));
var analyzer = new AnalyzerFileReference(args[3], new Loader()).GetAnalyzers(LanguageNames.CSharp);
var analysis = await updated.WithAnalyzers(analyzer, new AnalyzerOptions([], provider)).GetAnalyzerDiagnosticsAsync();
File.WriteAllText(Path.Combine(args[5], "diagnostics.json"), JsonSerializer.Serialize(analysis.Select(d => new { d.Id, Message = d.GetMessage(), Severity = d.Severity.ToString() }), new JsonSerializerOptions { WriteIndented = true }));
if (analysis.Length != 0) throw new InvalidOperationException("Analyzer: " + string.Join('\n', analysis));
using var pe = new MemoryStream();
using var pdb = new MemoryStream();
var emit = updated.Emit(pe, pdb);
if (!emit.Success) throw new InvalidOperationException(string.Join('\n', emit.Diagnostics));
File.WriteAllBytes(Path.Combine(args[5], "InferredConsumer.dll"), pe.ToArray());
File.WriteAllBytes(Path.Combine(args[5], "InferredConsumer.pdb"), pdb.ToArray());

AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
using var lease = new ForkContext(args[1]);
Assembly fork = lease.LoadFromAssemblyPath(Path.GetFullPath(args[1]));
Assembly consumer = lease.LoadFromStream(new MemoryStream(pe.ToArray()), new MemoryStream(pdb.ToArray()));
// Registration must run before Default is first initialized, as in a normal generated application.
System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(consumer.ManifestModule.ModuleHandle);
Type optionsType = fork.GetType("System.Text.Json.JsonSerializerOptions")!;
object builtinDefault = optionsType.GetProperty("Default")!.GetValue(null)!;
string output;
try { output = (string)consumer.GetType("Entry")!.GetMethod("Run")!.Invoke(null, [builtinDefault])!; }
catch (TargetInvocationException error) { throw error.InnerException!; }
var parity = await Controls.Run(args, references, parseOptions, generator, analyzer);
Console.WriteLine(JsonSerializer.Serialize(new { Passed = true, GeneratedTrees = generated.Length, ReflectionDisabled = true, Output = output, Controls = parity }));

sealed class ForkContext(string library) : AssemblyLoadContext(isCollectible: true), IDisposable
{
    protected override Assembly? Load(AssemblyName name) => name.Name == "System.Text.Json" ? LoadFromAssemblyPath(Path.GetFullPath(library)) : null;
    public void Dispose() => Unload();
}
sealed class Loader : IAnalyzerAssemblyLoader
{
    public void AddDependencyLocation(string fullPath) { }
    public Assembly LoadFromPath(string fullPath) => AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
}
sealed class OptionsProvider(bool infer = true) : AnalyzerConfigOptionsProvider
{
    private readonly AnalyzerConfigOptions options = new Options(infer);
    public override AnalyzerConfigOptions GlobalOptions => options;
    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => options;
    public override AnalyzerConfigOptions GetOptions(AdditionalText file) => options;
}
sealed class Options(bool infer) : AnalyzerConfigOptions
{
    public override bool TryGetValue(string key, out string value)
    {
        value = key == "build_property.NeemleJsonInferOrdinaryCalls" && !infer ? "false" : "true";
        return key is "build_property.EnableTrimAnalyzer" or "build_property.EnableAotAnalyzer" or "build_property.NeemleJsonInferOrdinaryCalls";
    }
}

// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

if (args.Length is < 2 or > 3) throw new ArgumentException("Expected framework references, stock analyzer DLL and optional corrected analyzer DLL.");
var references = Directory.GetFiles(args[0], "*.dll").Select(path => MetadataReference.CreateFromFile(path)).ToArray();
var options = new CSharpParseOptions(LanguageVersion.Preview).WithFeatures([new("InterceptorsNamespaces", "Probe.Generated")]);
var analyzerReference = new AnalyzerFileReference(args[1], new Loader());
var analyzers = analyzerReference.GetAnalyzers(LanguageNames.CSharp);
var corrected = args.Length == 3 ? new AnalyzerFileReference(args[2], new Loader()).GetAnalyzers(LanguageNames.CSharp) : [];
if (analyzers.IsEmpty) throw new InvalidOperationException("The selected stock analyzer did not load.");
var reports = new List<object>();
foreach (var (name, originalUnsafe, targetUnsafe, intercept) in new[] {
    ("unsafe-original-safe-target", true, false, true),
    ("safe-original-unsafe-target", false, true, true),
    ("unintercepted-unsafe", true, false, false) })
{
    string requirements = "[System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(\"genuine trim contract\")] "
        + "[System.Diagnostics.CodeAnalysis.RequiresDynamicCode(\"genuine code contract\")]";
    var source = $$"""
        namespace Probe {
            public static class Original {
                {{(originalUnsafe ? requirements : "")}}
                public static int Identity<T>(int value) => value + 10;
            }
            public static class Entry {
                public static int Run(int value) => Original.Identity<int>(value);
            }
        }
        """;
    var tree = CSharpSyntaxTree.ParseText(source, options, path: "/contract/" + name + ".cs");
    var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
    var compilation = CSharpCompilation.Create(name, [tree], references,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    if (intercept)
    {
        // These Roslyn APIs are inspected by their actual selected SDK metadata.
        // Reflection stays in this build-time oracle; no application executes here.
        var locationMethod = typeof(Microsoft.CodeAnalysis.CSharp.CSharpExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == "GetInterceptableLocation" && method.GetParameters().Length == 3);
        var location = locationMethod.Invoke(null, [compilation.GetSemanticModel(tree), invocation, CancellationToken.None])
            ?? throw new InvalidOperationException("No interceptable source location.");
        int version = (int)location.GetType().GetProperty("Version")!.GetValue(location)!;
        string data = (string)location.GetType().GetProperty("Data")!.GetValue(location)!;
        string generated = $$"""
            namespace System.Runtime.CompilerServices {
                [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = true)]
                sealed class InterceptsLocationAttribute(int version, string data) : System.Attribute { }
            }
            namespace Probe.Generated {
                public static class Hooks {
                    [System.Runtime.CompilerServices.InterceptsLocation({{version}}, "{{data}}")]
                    {{(targetUnsafe ? requirements : "")}}
                    public static int Identity(int value) => value + 20;
                }
            }
            """;
        compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(generated, options, path: "/generated/" + name + ".g.cs"));
    }
    var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
    if (errors.Length != 0) throw new InvalidOperationException(string.Join('\n', errors.Select(d => d.ToString())));
    var model = compilation.GetSemanticModel(tree);
    var original = ((IInvocationOperation)model.GetOperation(invocation)!).TargetMethod;
    var interceptorMethod = typeof(Microsoft.CodeAnalysis.CSharp.CSharpExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == "GetInterceptorMethod" && method.GetParameters().Length == 3);
    var actual = (IMethodSymbol?)interceptorMethod.Invoke(null, [model, invocation, CancellationToken.None]);
    var analyzerOptions = new AnalyzerOptions([], new OptionsProvider());
    var diagnostics = await compilation.WithAnalyzers(analyzers, analyzerOptions).GetAnalyzerDiagnosticsAsync();
    var requires = diagnostics.Where(d => d.Id is "IL2026" or "IL3050").Select(d => new {
        d.Id, Message = d.GetMessage(), Path = d.Location.SourceTree?.FilePath }).ToArray();
    if (diagnostics.Any(d => d.Id == "AD0001") || requires.Length != (originalUnsafe ? 2 : 0))
        throw new InvalidOperationException("Stock analyzer control failed: " + string.Join('\n', diagnostics));
    var correctedDiagnostics = corrected.IsEmpty ? []
        : await compilation.WithAnalyzers(corrected, analyzerOptions).GetAnalyzerDiagnosticsAsync();
    var correctedRequires = correctedDiagnostics.Where(d => d.Id is "IL2026" or "IL3050").Select(d => new {
        d.Id, Message = d.GetMessage(), Path = d.Location.SourceTree?.FilePath }).ToArray();
    if (!corrected.IsEmpty && (correctedDiagnostics.Any(d => d.Id == "AD0001")
        || correctedRequires.Length != ((intercept ? targetUnsafe : originalUnsafe) ? 2 : 0)))
        throw new InvalidOperationException("Executable-contract analyzer control failed: " + string.Join('\n', correctedDiagnostics));
    using var output = new MemoryStream();
    var emitted = compilation.Emit(output);
    if (!emitted.Success) throw new InvalidOperationException(string.Join('\n', emitted.Diagnostics));
    output.Position = 0;
    using var pe = new PEReader(output, PEStreamOptions.LeaveOpen);
    var metadata = pe.GetMetadataReader();
    var entry = metadata.MethodDefinitions.Select(handle => metadata.GetMethodDefinition(handle))
        .Single(method => metadata.GetString(method.Name) == "Run");
    byte[] body = pe.GetMethodBody(entry.RelativeVirtualAddress).GetILBytes()!;
    int call = Array.IndexOf(body, (byte)0x28);
    var token = MetadataTokens.EntityHandle(BitConverter.ToInt32(body, call + 1));
    if (token.Kind == HandleKind.MethodSpecification)
        token = metadata.GetMethodSpecification((MethodSpecificationHandle)token).Method;
    string callName;
    if (token.Kind == HandleKind.MethodDefinition) {
        var definition = metadata.GetMethodDefinition((MethodDefinitionHandle)token);
        var type = metadata.GetTypeDefinition(definition.GetDeclaringType());
        callName = metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name) + "." + metadata.GetString(definition.Name);
    } else throw new InvalidOperationException("Unexpected controlled call token " + token.Kind);
    int expected = intercept ? 21 : 11;
    var assembly = Assembly.Load(output.ToArray());
    var run = assembly.GetType("Probe.Entry")!.GetMethod("Run")!.CreateDelegate<Func<int, int>>();
    if (run(1) != expected) throw new InvalidOperationException("Interception did not execute the emitted target.");
    if (intercept != (actual is not null)) throw new InvalidOperationException("Roslyn interceptor identity mismatch.");
    reports.Add(new { name, OperationTarget = original.ToDisplayString(),
        ExecutableTarget = (actual ?? original).ToDisplayString(), EmittedCall = callName,
        StockAnalyzerDiagnostics = requires, CorrectedAnalyzerDiagnostics = correctedRequires, ActualResult = expected });
}
var dataflow = corrected.IsEmpty ? [] : await DataflowControls.Run(options, references, analyzers, corrected);
Console.WriteLine(JsonSerializer.Serialize(new { Passed = true, Reports = reports, Dataflow = dataflow }, new JsonSerializerOptions { WriteIndented = true }));

sealed class Loader : IAnalyzerAssemblyLoader
{
    public void AddDependencyLocation(string fullPath) { }
    public Assembly LoadFromPath(string fullPath) => AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
}
sealed class OptionsProvider : AnalyzerConfigOptionsProvider
{
    private readonly AnalyzerConfigOptions options = new Options();
    public override AnalyzerConfigOptions GlobalOptions => options;
    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => options;
    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => options;
}
sealed class Options : AnalyzerConfigOptions
{
    public override bool TryGetValue(string key, out string value)
    {
        value = "true";
        return key is "build_property.EnableTrimAnalyzer" or "build_property.EnableAotAnalyzer";
    }
}

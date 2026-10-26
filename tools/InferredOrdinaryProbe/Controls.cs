// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

internal static class Controls
{
    internal static async Task<object> Run(string[] args, MetadataReference[] references, CSharpParseOptions options,
        IIncrementalGenerator generator, ImmutableArray<DiagnosticAnalyzer> analyzers)
    {
        var results = new List<object>();
        foreach (var (name, source, expectedGenerated, expectedRequires, expectedUnsupported) in new[]
        {
            ("open generic stays unsafe", "using System.Text.Json; public static class Entry { public static string Run<T>(T value) => JsonSerializer.Serialize(value); }", false, 2, 1),
            ("runtime object stays unsafe", "using System.Text.Json; public static class Entry { public static string Run(object value) => JsonSerializer.Serialize(value); }", false, 2, 1),
            ("unhandled overload stays unsafe", "using System.Text.Json; public static class Entry { public static byte[] Run(int value) => JsonSerializer.SerializeToUtf8Bytes(value); }", false, 2, 0),
            ("genuine unsafe converter remains diagnosed", "using System; using System.Text.Json; using System.Text.Json.Serialization; using System.Diagnostics.CodeAnalysis; [JsonConverter(typeof(Converter))] public class Item {} public class Converter : JsonConverter<Item> { [RequiresUnreferencedCode(\"genuine converter ctor\")] [RequiresDynamicCode(\"genuine converter code\")] public Converter() {} public override Item Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new Item(); public override void Write(Utf8JsonWriter writer, Item value, JsonSerializerOptions options) {} } public static class Entry { public static string Run(Item value) => JsonSerializer.Serialize(value); }", true, 3, 0),
        })
        {
            var tree = CSharpSyntaxTree.ParseText(source, options, "/controls/" + name + ".cs");
            Compilation original = CSharpCompilation.Create("Control", [tree], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            var provider = new OptionsProvider();
            GeneratorDriver driver = CSharpGeneratorDriver.Create([generator.AsSourceGenerator()], parseOptions: options, optionsProvider: provider);
            driver = driver.RunGeneratorsAndUpdateCompilation(original, out Compilation updated, out var generatorDiagnostics);
            var diagnostics = await updated.WithAnalyzers(analyzers, new AnalyzerOptions([], provider)).GetAnalyzerDiagnosticsAsync();
            int unsupported = generatorDiagnostics.Count(d => d.Id == "NEEMLEJSON001");
            int requires = diagnostics.Count(d => d.Id is "IL2026" or "IL3050");
            if (unsupported != expectedUnsupported || requires != expectedRequires || diagnostics.Any(d => d.Id == "AD0001") ||
                driver.GetRunResult().GeneratedTrees.Any() != expectedGenerated)
                throw new InvalidOperationException(name + ": " + string.Join('\n', generatorDiagnostics.Concat(diagnostics)));
            results.Add(new { Name = name, UnsupportedContracts = unsupported, Diagnostics = diagnostics.Select(d => new { d.Id, Message = d.GetMessage() }).ToArray() });
        }

        string paritySource = File.ReadAllText(Path.Combine(Path.GetDirectoryName(args[4])!, "Parity.cs"));
        string? oracle = null;
        foreach (bool infer in new[] { false, true })
        {
            var tree = CSharpSyntaxTree.ParseText(paritySource, options, "/parity/Program.cs");
            Compilation original = CSharpCompilation.Create("Parity" + infer, [tree], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            var provider = new OptionsProvider(infer);
            GeneratorDriver driver = CSharpGeneratorDriver.Create([generator.AsSourceGenerator()], parseOptions: options, optionsProvider: provider);
            driver = driver.RunGeneratorsAndUpdateCompilation(original, out Compilation updated, out var generatorDiagnostics);
            if (updated.GetDiagnostics().Any(d => d.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error) || !generatorDiagnostics.IsEmpty)
                throw new InvalidOperationException(string.Join('\n', updated.GetDiagnostics().Concat(generatorDiagnostics)));
            var diagnostics = await updated.WithAnalyzers(analyzers, new AnalyzerOptions([], provider)).GetAnalyzerDiagnosticsAsync();
            int expected = infer ? 0 : 14;
            if (diagnostics.Length != expected || diagnostics.Any(d => d.Id is not ("IL2026" or "IL3050")))
                throw new InvalidOperationException("Parity analyzer: " + string.Join('\n', diagnostics));
            var intercepted = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Count(node => Microsoft.CodeAnalysis.CSharp.CSharpExtensions.GetInterceptorMethod(updated.GetSemanticModel(tree), node) is not null);
            if (intercepted != (infer ? 7 : 0)) throw new InvalidOperationException("Actual compiler interceptor count mismatch.");
            using var pe = new MemoryStream();
            var emit = updated.Emit(pe);
            if (!emit.Success) throw new InvalidOperationException(string.Join('\n', emit.Diagnostics));
            File.WriteAllBytes(Path.Combine(args[5], "Parity" + infer + ".dll"), pe.ToArray());
            AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", !infer);
            using var lease = new ForkContext(args[1]);
            Assembly consumer = lease.LoadFromStream(new MemoryStream(pe.ToArray()));
            string output;
            try { output = (string)consumer.GetType("Entry")!.GetMethod("Run")!.Invoke(null, null)!; }
            catch (TargetInvocationException error) { throw error.InnerException!; }
            if (oracle is null) oracle = output;
            else if (output != oracle) throw new InvalidOperationException("Plain reflection-enabled JIT and inferred reflection-disabled JIT differ.");
            results.Add(new { Name = infer ? "inferred reflection-disabled parity" : "ordinary JIT unchanged", ActualCompilerInterceptors = intercepted, Diagnostics = diagnostics.Select(d => d.Id).ToArray(), Output = output });
        }

        return results;
    }
}

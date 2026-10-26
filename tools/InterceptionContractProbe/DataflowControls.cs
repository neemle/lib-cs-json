// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

internal static class DataflowControls
{
    private const string Dam = "[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods)]";

    internal static async Task<List<object>> Run(CSharpParseOptions options, MetadataReference[] references,
        ImmutableArray<DiagnosticAnalyzer> stock, ImmutableArray<DiagnosticAnalyzer> corrected)
    {
        var reports = new List<object>();
        foreach (var (name, oldParameter, newParameter, oldReturn, newReturn, kind, generic) in new[] {
            ("parameter-removes-requirement", true, false, false, false, "value", false),
            ("parameter-adds-requirement", false, true, false, false, "value", false),
            ("return-adds-guarantee", false, false, false, true, "return", false),
            ("return-removes-guarantee", false, false, true, false, "return", false),
            ("ref-replaces-contract", true, false, false, false, "ref", false),
            ("out-replaces-contract", false, true, false, false, "out", false),
            ("constructed-generic-return", false, false, false, true, "return", true),
            ("constructed-generic-parameter", false, true, false, false, "value", true) })
        {
            string suffix = generic ? "<T>" : "";
            string useSuffix = generic ? "<int>" : "";
            string returnType = kind is "ref" or "out" ? "void" : "System.Type";
            string modifier = kind is "ref" or "out" ? kind + " " : "";
            string body = kind is "ref" or "out" ? "{ value = typeof(string); }" : "=> typeof(string);";
            string oldDeclaration = $"{(oldReturn ? "[return: " + Dam[1..] : "")} public static {returnType} Map{suffix}({(oldParameter ? Dam : "")} {modifier}System.Type value) {body}";
            string newDeclaration = $"{(newReturn ? "[return: " + Dam[1..] : "")} public static {returnType} Map{suffix}({(newParameter ? Dam : "")} {modifier}System.Type value) {body}";
            string statement = kind switch {
                "return" => $"Need(Original.Map{useSuffix}(typeof(string)));",
                "ref" => $"Original.Map{useSuffix}(ref value); Need(value);",
                "out" => $"Original.Map{useSuffix}(out value); Need(value);",
                _ => $"_ = Original.Map{useSuffix}(value);"
            };
            string source = $$"""
                namespace FlowProbe {
                    public static class Original { {{oldDeclaration}} }
                    public static class Entry {
                        public static void Need({{Dam}} System.Type value) { }
                        public static void Run(System.Type value) { {{statement}} }
                    }
                }
                """;
            var tree = CSharpSyntaxTree.ParseText(source, options, path: "/contract/" + name + ".cs");
            var compilation = CSharpCompilation.Create(name, [tree], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Single(node => node.Expression.ToString().StartsWith("Original.Map", StringComparison.Ordinal));
            var locationMethod = typeof(Microsoft.CodeAnalysis.CSharp.CSharpExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(method => method.Name == "GetInterceptableLocation" && method.GetParameters().Length == 3);
            var location = locationMethod.Invoke(null, [compilation.GetSemanticModel(tree), invocation, CancellationToken.None])!;
            int version = (int)location.GetType().GetProperty("Version")!.GetValue(location)!;
            string data = (string)location.GetType().GetProperty("Data")!.GetValue(location)!;
            const string attribute = "namespace System.Runtime.CompilerServices { [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple=true)] sealed class InterceptsLocationAttribute(int version, string data) : System.Attribute {} }";
            string target = $"namespace Probe.Generated {{ public static class Hooks {{ [System.Runtime.CompilerServices.InterceptsLocation({version}, \"{data}\")] {newDeclaration} }} }}";
            compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(attribute + target, options, path: "/generated/" + name + ".g.cs"));
            var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length != 0) throw new InvalidOperationException(string.Join('\n', errors.Select(d => d.ToString())));
            var selectedMethod = typeof(Microsoft.CodeAnalysis.CSharp.CSharpExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(method => method.Name == "GetInterceptorMethod" && method.GetParameters().Length == 3);
            var selected = (IMethodSymbol)selectedMethod.Invoke(null, [compilation.GetSemanticModel(tree), invocation, CancellationToken.None])!;
            var resolver = corrected[0].GetType().Assembly.GetType("Neemle.SystemTextJson.AotAnalysis.ExecutableInvocationContract")!
                .GetMethod("Resolve", BindingFlags.NonPublic | BindingFlags.Static)!;
            var resolved = (IMethodSymbol)resolver.Invoke(null, [compilation,
                (Microsoft.CodeAnalysis.Operations.IInvocationOperation)compilation.GetSemanticModel(tree).GetOperation(invocation)!])!;
            if (generic && (resolved.TypeArguments.Length != 1 || resolved.TypeArguments[0].SpecialType != SpecialType.System_Int32))
                throw new InvalidOperationException("The executable contract did not construct generic arguments: " + resolved.ToDisplayString());

            // The oracle directly calls the same executable method. This checks
            // full dataflow rather than accepting/removing selected diagnostic IDs.
            var directTree = CSharpSyntaxTree.ParseText(source.Replace("Original.Map", "global::Probe.Generated.Hooks.Map", StringComparison.Ordinal), options, path: "/direct/" + name + ".cs");
            var directTarget = CSharpSyntaxTree.ParseText($"namespace Probe.Generated {{ public static class Hooks {{ {newDeclaration} }} }}", options, path: "/direct-target/" + name + ".cs");
            var direct = CSharpCompilation.Create(name + "Direct", [directTree, directTarget], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            errors = direct.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length != 0) throw new InvalidOperationException(string.Join('\n', errors.Select(d => d.ToString())));
            var analyzerOptions = new AnalyzerOptions([], new OptionsProvider());
            var expected = await direct.WithAnalyzers(stock, analyzerOptions).GetAnalyzerDiagnosticsAsync();
            var actual = await compilation.WithAnalyzers(corrected, analyzerOptions).GetAnalyzerDiagnosticsAsync();
            string[] Normalize(ImmutableArray<Diagnostic> diagnostics) => diagnostics
                .Select(d => d.Id + ": " + d.GetMessage()).Order(StringComparer.Ordinal).ToArray();
            if (expected.Any(d => d.Id == "AD0001") || actual.Any(d => d.Id == "AD0001")
                || !Normalize(actual).SequenceEqual(Normalize(expected)))
                throw new InvalidOperationException(name + " executable dataflow mismatch. Expected:\n" + string.Join('\n', Normalize(expected))
                    + "\nActual:\n" + string.Join('\n', Normalize(actual)));
            using var output = new MemoryStream();
            var emit = compilation.Emit(output);
            if (!emit.Success) throw new InvalidOperationException(string.Join('\n', emit.Diagnostics));
            reports.Add(new { name, RoslynInterceptorDefinition = selected.ToDisplayString(),
                ExecutableInterceptor = resolved.ToDisplayString(), DirectExecutableOracleDiagnostics = Normalize(expected),
                CorrectedAnalyzerDiagnostics = Normalize(actual), EmittedWithoutErrors = true });
        }
        return reports;
    }
}

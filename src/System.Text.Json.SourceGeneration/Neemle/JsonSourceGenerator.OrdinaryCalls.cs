// Copyright (c) Neemle contributors. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using SourceGenerators;

namespace System.Text.Json.SourceGeneration
{
    public sealed partial class JsonSourceGenerator
    {
        private void InitializeOrdinaryCalls(IncrementalGeneratorInitializationContext context)
        {
            var calls = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax,
                static (syntax, cancellation) =>
                {
                    var invocation = (InvocationExpressionSyntax)syntax.Node;
                    if (syntax.SemanticModel.GetSymbolInfo(invocation, cancellation).Symbol is not IMethodSymbol method ||
                        !method.IsStatic || !method.IsGenericMethod || method.TypeArguments.Length != 1 ||
                        method.Name is not ("Serialize" or "Deserialize") || method.Parameters.Length != 2 ||
                        method.ContainingType.ToDisplayString() != "System.Text.Json.JsonSerializer" ||
                        method.ContainingAssembly.Identity.Name != "System.Text.Json" ||
                        !SymbolEqualityComparer.Default.Equals(method.Parameters[1].Type, syntax.SemanticModel.Compilation.GetTypeByMetadataName("System.Text.Json.JsonSerializerOptions")) ||
                        method.Name == "Deserialize" && method.Parameters[0].Type.SpecialType != SpecialType.System_String ||
                        method.Name == "Serialize" && !SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, method.TypeArguments[0]))
                    {
                        return null;
                    }

                    return new OrdinaryCall(invocation, method, syntax.SemanticModel);
                }).Where(static call => call is not null).Collect();

            var enabled = context.AnalyzerConfigOptionsProvider.Select(static (provider, _) =>
                provider.GlobalOptions.TryGetValue("build_property.NeemleJsonInferOrdinaryCalls", out string? value) && value == "true" ||
                provider.GlobalOptions.TryGetValue("build_property.PublishAot", out value) && value == "true");

            context.RegisterSourceOutput(calls.Combine(context.CompilationProvider).Combine(enabled), (output, input) =>
            {
                if (input.Right && !input.Left.Left.IsEmpty)
                {
                    EmitOrdinaryCalls(output, input.Left.Right, input.Left.Left!);
                }
            });
        }

        private sealed record OrdinaryCall(InvocationExpressionSyntax Syntax, IMethodSymbol Method, SemanticModel Model);

        private void EmitOrdinaryCalls(SourceProductionContext output, Compilation compilation, ImmutableArray<OrdinaryCall?> calls)
        {
            INamedTypeSymbol? operations = compilation.GetTypeByMetadataName("Neemle.SystemTextJson.InferredJsonOperations");
            INamedTypeSymbol? serializer = compilation.GetTypeByMetadataName("System.Text.Json.JsonSerializer");
            if (operations is null || serializer is null || !SymbolEqualityComparer.Default.Equals(operations.ContainingAssembly, serializer.ContainingAssembly))
            {
                output.ReportDiagnostic(Diagnostic.Create(OrdinaryUnsupported, calls[0]!.Syntax.GetLocation(), "The selected System.Text.Json library does not provide inferred operation infrastructure."));
                return;
            }

            var accepted = new List<(OrdinaryCall Call, string Type, int Version, string Data)>();
            foreach (OrdinaryCall? candidate in calls)
            {
                OrdinaryCall call = candidate!;
                ITypeSymbol type = call.Method.TypeArguments[0];
                if (!SymbolEqualityComparer.Default.Equals(call.Method.ContainingType, serializer) || !IsClosedAccessible(type, compilation))
                {
                    output.ReportDiagnostic(Diagnostic.Create(OrdinaryUnsupported, call.Syntax.GetLocation(), "The serialized type must be closed and accessible to the generated context; open or runtime-selected contracts remain unsupported."));
                    continue;
                }

                // The compiler owns the location encoding. Do not reconstruct paths or call offsets.
                var location = Microsoft.CodeAnalysis.CSharp.CSharpExtensions.GetInterceptableLocation(call.Model, call.Syntax, output.CancellationToken);
                if (location is null)
                {
                    output.ReportDiagnostic(Diagnostic.Create(OrdinaryUnsupported, call.Syntax.GetLocation(), "The selected compiler cannot advertise an interceptable call location."));
                    continue;
                }

                accepted.Add((call, type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), location.Version, location.Data));
            }

            if (accepted.Count == 0)
            {
                return;
            }

            const string contextName = "NeemleInferredJsonContext";
            var source = new StringBuilder("#nullable enable\nnamespace Neemle.SystemTextJson.Generated {\n");
            source.AppendLine("[global::System.Text.Json.Serialization.JsonSourceGenerationOptions(GenerationMode = global::System.Text.Json.Serialization.JsonSourceGenerationMode.Metadata)]");
            foreach (string type in accepted.Select(call => call.Type).Distinct().OrderBy(type => type, StringComparer.Ordinal))
            {
                source.Append("[global::System.Text.Json.Serialization.JsonSerializable(typeof(").Append(type).AppendLine("))]");
            }

            source.Append("internal partial class ").Append(contextName).AppendLine(" : global::System.Text.Json.Serialization.JsonSerializerContext {} }");
            string contextSource = source.ToString();
            var tree = CSharpSyntaxTree.ParseText(contextSource, (CSharpParseOptions)calls[0]!.Syntax.SyntaxTree.Options, "Neemle.InferredJson.Context.g.cs", Encoding.UTF8);
            Compilation augmented = compilation.AddSyntaxTrees(tree);
            var parser = new Parser(new KnownTypeSymbols(augmented));
            ClassDeclarationSyntax declaration = tree.GetRoot(output.CancellationToken).DescendantNodes().OfType<ClassDeclarationSyntax>().Single();
            ContextGenerationSpec? spec = parser.ParseContextGenerationSpec(declaration, augmented.GetSemanticModel(tree), output.CancellationToken);
            ReportDiagnosticsAndEmitSource(output, (spec, parser.Diagnostics.ToImmutableEquatableArray()));
            if (spec is null)
            {
                return;
            }

            output.AddSource("Neemle.InferredJson.Context.g.cs", SourceText.From(contextSource, Encoding.UTF8));
            source.Clear();
            source.AppendLine("#nullable enable\nnamespace System.Runtime.CompilerServices { [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple=true)] internal sealed class InterceptsLocationAttribute : global::System.Attribute { public InterceptsLocationAttribute(int version, string data) {} } }");
            source.AppendLine("namespace Neemle.SystemTextJson.Generated { internal static class InferredOperations {");
            source.AppendLine("[global::System.Runtime.CompilerServices.ModuleInitializer] internal static void Register() => global::Neemle.SystemTextJson.InferredJsonOperations.Register(static (type, options) => ((global::System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver)NeemleInferredJsonContext.Default).GetTypeInfo(type, options));");
            for (int i = 0; i < accepted.Count; i++)
            {
                var call = accepted[i];
                bool serialize = call.Call.Method.Name == "Serialize";
                source.Append("[global::System.Runtime.CompilerServices.InterceptsLocation(").Append(call.Version).Append(", ").Append(SymbolDisplay.FormatLiteral(call.Data, true)).AppendLine(")]");
                source.Append("internal static ").Append(serialize ? "string" : call.Call.Method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier))).Append(" Operation").Append(i).Append('(')
                    .Append(serialize ? call.Type : "string").Append(" value, global::System.Text.Json.JsonSerializerOptions? options) => global::Neemle.SystemTextJson.InferredJsonOperations.")
                    .Append(call.Call.Method.Name).Append('<').Append(call.Type).AppendLine(">(value, options);");
            }

            source.AppendLine("} }");
            output.AddSource("Neemle.InferredJson.Operations.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
        }

        private static bool IsClosedAccessible(ITypeSymbol type, Compilation compilation)
            => type switch
            {
                ITypeParameterSymbol => false,
                IArrayTypeSymbol array => IsClosedAccessible(array.ElementType, compilation),
                INamedTypeSymbol named => !named.IsUnboundGenericType && named.SpecialType != SpecialType.System_Object &&
                    named.TypeKind is not (TypeKind.Dynamic or TypeKind.Error) && compilation.IsSymbolAccessibleWithin(named, compilation.Assembly) &&
                    named.TypeArguments.All(argument => IsClosedAccessible(argument, compilation)),
                _ => false,
            };

        private static readonly DiagnosticDescriptor OrdinaryUnsupported = new(
            "NEEMLEJSON001", "JSON operation requires an inferred contract", "{0}", "AOT", DiagnosticSeverity.Error, true);
    }
}

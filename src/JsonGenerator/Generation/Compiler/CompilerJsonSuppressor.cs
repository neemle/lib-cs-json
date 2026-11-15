using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Neemle.Json.Generator;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CompilerJsonSuppressor : DiagnosticSuppressor
{
    private static readonly SuppressionDescriptor Trim = new("NEEMLE0001", "IL2026",
        "The JSON call is intercepted by generated code.");
    private static readonly SuppressionDescriptor Aot = new("NEEMLE0002", "IL3050",
        "The JSON call is intercepted by generated code.");
    private static readonly SuppressionDescriptor EnumFactory = new("NEEMLE0003", "IL3050",
        "The NativeAOT JSON runtime uses generated enum converters.");

    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions => [Trim, Aot, EnumFactory];

    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        foreach (var diagnostic in context.ReportedDiagnostics)
        {
            if (diagnostic.Location.SourceTree is not { } tree) continue;
            var model = context.GetSemanticModel(tree);
            var node = tree.GetRoot(context.CancellationToken).FindNode(diagnostic.Location.SourceSpan);
            if (diagnostic.Id == "IL3050" && node.DescendantNodesAndSelf().OfType<BaseObjectCreationExpressionSyntax>()
                .FirstOrDefault(creation => creation.Span == diagnostic.Location.SourceSpan) is { } creation &&
                model.GetTypeInfo(creation).Type?.ToDisplayString() == "System.Text.Json.Serialization.JsonStringEnumConverter")
            {
                context.ReportSuppression(Suppression.Create(EnumFactory, diagnostic));
                continue;
            }
            var invocation = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
            if (invocation is not null && HttpJsonCall.TryCreate(model, invocation, out _))
            {
                context.ReportSuppression(Suppression.Create(diagnostic.Id == "IL2026" ? Trim : Aot, diagnostic));
                continue;
            }
            if (invocation is null || !CompilerJsonCall.TryCreate(model, invocation, out var call)) continue;
            var emitter = new CodecEmitter(model.Compilation.GetSpecialType(SpecialType.System_Object));
            if (call.Type is not ITypeParameterSymbol && !emitter.TryRegister(call.Type, out _) &&
                !new AsyncContextEmitter().RegisterUnsupported(call.Type)) continue;
            context.ReportSuppression(Suppression.Create(diagnostic.Id == "IL2026" ? Trim : Aot, diagnostic));
        }
    }
}

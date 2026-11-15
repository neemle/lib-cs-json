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

    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions => [Trim, Aot];

    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        foreach (var diagnostic in context.ReportedDiagnostics)
        {
            if (diagnostic.Location.SourceTree is not { } tree) continue;
            var model = context.GetSemanticModel(tree);
            var node = tree.GetRoot(context.CancellationToken).FindNode(diagnostic.Location.SourceSpan);
            var invocation = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
            if (invocation is null || !CompilerJsonCall.TryCreate(model, invocation, out var call)) continue;
            var emitter = new CodecEmitter(model.Compilation.GetSpecialType(SpecialType.System_Object));
            if (call.Type is not ITypeParameterSymbol && !emitter.TryRegister(call.Type, out _) &&
                !new AsyncContextEmitter().RegisterUnsupported(call.Type)) continue;
            context.ReportSuppression(Suppression.Create(diagnostic.Id == "IL2026" ? Trim : Aot, diagnostic));
        }
    }
}

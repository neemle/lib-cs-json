using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Model;
using static CodecApiEmitter;

internal static class NullableContractEmitter
{
    internal static CsNode? WriteCheck(ISymbol member, ITypeSymbol type, CsNode value, CsNode path) =>
        RequiresCheck(member, type, false) ? Check(NullTest(value), path) : null;

    internal static CsNode? ReadCheck(ISymbol member, ITypeSymbol type, CsNode element, CsNode path) =>
        RequiresCheck(member, type, true) ? Check(NodeBinary(CsOperator.Equal, NodeMember(element, "ValueKind"),
            NodeMember(Cs.Symbol("JsonValueKind"), "Null")), path) : null;

    private static bool RequiresCheck(ISymbol member, ITypeSymbol type, bool reading)
    {
        if (!type.IsReferenceType) return false;
        string required = reading ? "DisallowNullAttribute" : "NotNullAttribute";
        string allowed = reading ? "AllowNullAttribute" : "MaybeNullAttribute";
        bool Has(string name) => member.GetAttributes().Any(attribute => attribute.AttributeClass?.Name == name &&
            attribute.AttributeClass.ContainingNamespace.ToDisplayString() == "System.Diagnostics.CodeAnalysis");
        return Has(required) || !Has(allowed) && type.NullableAnnotation == NullableAnnotation.NotAnnotated;
    }

    private static CsNode Check(CsNode isNull, CsNode path) => NodeIf(NodeBinary(CsOperator.AndAlso,
        NodeBinary(CsOperator.Equal, Cs.Symbol("RespectNullableAnnotations", new()
        { Target = Cs.Symbol("options"), ConditionalAccess = true }), Cs.Value(true)), isNull),
        NodeThrow(NodeNew("JsonException", Cs.Value(null), path, Cs.Value(null), Cs.Value(null))));
}

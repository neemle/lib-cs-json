using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Model;
using static CodecApiEmitter;

internal static class IgnoreConditionEmitter
{
    internal static CsNode? Condition(ISymbol member, ITypeSymbol type, string local)
    {
        bool explicitCondition = member.GetAttributes().Any(a => a.AttributeClass?.Name == "JsonIgnoreAttribute" &&
            a.AttributeClass.ContainingNamespace.ToDisplayString() == "System.Text.Json.Serialization");
        if (explicitCondition) return CodecEmitter.IgnoreCondition(member) switch
        {
            2 => NotDefault(type, local),
            3 => NullTest(Cs.Symbol(local), true),
            _ => null
        };
        CsNode condition = NodeBinary(CsOperator.OrElse, IsNotConfigured("WhenWritingDefault"), NotDefault(type, local));
        if (!CanBeNull(type)) return condition;
        return NodeBinary(CsOperator.AndAlso, condition,
            NodeBinary(CsOperator.OrElse, IsNotConfigured("WhenWritingNull"), NullTest(Cs.Symbol(local), true)));
    }

    private static bool CanBeNull(ITypeSymbol type) => type.IsReferenceType ||
        type is INamedTypeSymbol named && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

    private static CsNode IsNotConfigured(string name) => NodeBinary(CsOperator.NotEqual,
        Cs.Symbol("DefaultIgnoreCondition", new() { Target = Cs.Symbol("options"), ConditionalAccess = true }),
        NodeMember(Cs.Symbol("JsonIgnoreCondition"), name));

    private static CsNode NotDefault(ITypeSymbol type, string local)
    {
        var comparer = TypeRef($"System.Collections.Generic.EqualityComparer<{TypeName(type)}>");
        var equals = Cs.Symbol("Equals", new()
        {
            Target = NodeMember(comparer, "Default"),
            AsCall = true,
            Arguments = Cs.Compound(Cs.Symbol(local), DefaultValue())
        });
        return Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { equals } });
    }
}

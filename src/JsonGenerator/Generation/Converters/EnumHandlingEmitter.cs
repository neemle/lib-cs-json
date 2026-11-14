using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Model;
using static Neemle.Json.Generator.CodecApiEmitter;

namespace Neemle.Json.Generator;

internal static class EnumHandlingEmitter
{
    private const string ConverterName = "System.Text.Json.Serialization.JsonStringEnumConverter<TEnum>";

    internal static bool? StringConverter(ISymbol member, ITypeSymbol valueType)
    {
        var attribute = member.GetAttributes().FirstOrDefault(item =>
            CodecEmitter.IsJsonAttribute(item, "JsonConverterAttribute"));
        if (attribute is null) return null;
        if (attribute.ConstructorArguments.Length != 1 ||
            attribute.ConstructorArguments[0].Value is not INamedTypeSymbol converter)
            return false;
        if (converter.ToDisplayString() == "System.Text.Json.Serialization.JsonStringEnumConverter")
            return valueType.TypeKind == Microsoft.CodeAnalysis.TypeKind.Enum;
        if (converter.OriginalDefinition.ToDisplayString() != ConverterName || converter.TypeArguments.Length != 1)
            return false;
        return SymbolEqualityComparer.Default.Equals(converter.TypeArguments[0], valueType);
    }

    internal static CsNode[] Arguments(ISymbol member, ITypeSymbol type) =>
        StringConverter(member, type) == true ? [Cs.Value(true)] : [];

    internal static CsNode UseRegisteredOrDefault(int index, ITypeSymbol type)
    {
        var registered = NodeBinary(CsOperator.AndAlso, NodeBinary(CsOperator.AndAlso,
            NullTest(Cs.Symbol("options"), true),
            NodeCall($"Table_{index}.TryGetValue", Cs.Symbol("options"), OutVar("entry"))),
            NodeMember(Cs.Symbol("entry"), "StringEnum"));
        CsNode fallback = StringConverter(type, type) == true ? Cs.Value(true) : registered;
        return NodeBinary(CsOperator.Coalesce, Cs.Symbol("enumOverride"), fallback);
    }

    internal static CsNode AllowRegisteredConverter(CsNode condition) => NodeBinary(CsOperator.AndAlso,
        NodeBinary(CsOperator.NotEqual, Cs.Symbol("enumOverride"), Cs.Value(true)), condition);
}

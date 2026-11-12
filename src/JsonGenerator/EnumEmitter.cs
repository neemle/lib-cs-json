using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static CodecApiEmitter;
using TypeKind = Microsoft.CodeAnalysis.TypeKind;

static class EnumEmitter
{
    public static bool CanEmit(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Enum || type.EnumUnderlyingType is null ||
            type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.FlagsAttribute") ||
            Underlying(type.EnumUnderlyingType) is null) return false;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return type.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue)
            .All(f => names.Add(JsonName(f)));
    }

    public static void EmitHelpers(TypeBuilder<FileBuilder> root, INamedTypeSymbol type, int index)
    {
        if (!CanEmit(type)) throw new InvalidOperationException("Unsupported enum.");
        var numeric = Underlying(type.EnumUnderlyingType)!.Value;
        string enumType = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var fields = type.GetMembers().OfType<IFieldSymbol>()
            .Where(field => field.HasConstantValue && field.ConstantValue is not null).ToArray();
        EmitTables(root, index, enumType, fields);
        EmitWriter(root, index, enumType, numeric.Type);
        EmitReader(root, index, enumType, numeric);
    }

    private static void EmitTables(TypeBuilder<FileBuilder> root, int index, string enumType, IFieldSymbol[] fields)
    {
        var names = fields.Select(field => Cs.Literal(JsonName(field))).ToArray();
        var target = new CsExpression(ExpressionKind.TypeReference, "", []) { Type = NodeType(enumType) };
        var values = fields.Select(field => Cs.Member(target, field.Name)).ToArray();
        root.Field(Cs.Type("string").Array(), $"EnumNames_{index}").Private().Static().ReadOnly()
            .Initialize(ArrayInitializer(Cs.Type("string"), names)).End();
        root.Field(NodeType(enumType).Array(), $"EnumValues_{index}").Private().Static().ReadOnly()
            .Initialize(ArrayInitializer(NodeType(enumType), values)).End();
    }

    private static void EmitWriter(TypeBuilder<FileBuilder> root, int index, string enumType, string underlying)
    {
        var match = NodeBinary(CsOperator.Equal, Cs.Symbol("value"),
            Index(Cs.Symbol($"EnumValues_{index}"), Cs.Symbol("i")));
        var write = Call("WriteStringValue", Cs.Symbol("w"), Index(Cs.Symbol($"EnumNames_{index}"), Cs.Symbol("i")));
        var loop = Loop(index, "Values", Cs.Tree(NodeIf(match, write, Cs.Instruction(InstructionKind.Return))));
        root.Method(Cs.Void, $"WriteEnum_{index}").Private().Static()
            .Parameter(Cs.Type("Utf8JsonWriter"), "w").Parameter(NodeType(enumType), "value")
            .Parameter(Cs.Bool, "stringEnum")
            .Body(Cs.Tree(NodeIf(Cs.Symbol("stringEnum"), loop), Call("WriteNumberValue", Cs.Symbol("w"),
                Cast(underlying, Cs.Symbol("value"))))).End();
    }

    private static void EmitReader(TypeBuilder<FileBuilder> root, int index, string enumType,
        (string Type, string Reader) numeric)
    {
        var equals = Call("Equals", TypeRef("global::System.String"), Cs.Symbol("name"),
            Index(Cs.Symbol($"EnumNames_{index}"), Cs.Symbol("i")),
            NodeMember(TypeRef("global::System.StringComparison"), "OrdinalIgnoreCase"));
        var loop = Loop(index, "Names", Cs.Tree(NodeIf(equals,
            Return(Index(Cs.Symbol($"EnumValues_{index}"), Cs.Symbol("i"))))));
        CsNode parse = Call("TryParse", TypeRef(numeric.Type), Cs.Symbol("name"),
            NodeMember(TypeRef("global::System.Globalization.NumberStyles"), "Integer"),
            NodeMember(TypeRef("global::System.Globalization.CultureInfo"), "InvariantCulture"),
            OutVar("parsed"));
        var body = new CsNode[] { Cs.Symbol("name", new() { AsVar = true, Value = Call("GetString", Cs.Symbol("e")) }),
            loop, NodeIf(parse, Return(Cast(enumType, Cs.Symbol("parsed")))), ThrowJson() };
        root.Method(NodeType(enumType), $"ReadEnum_{index}").Private().Static()
            .Parameter(Cs.Type("JsonElement"), "e").Parameter(Cs.Bool, "stringEnum")
            .Body(Cs.Tree(NodeIf(NodeBinary(CsOperator.AndAlso, Cs.Symbol("stringEnum"), Kind("String")), body),
                NodeIf(NodeBinary(CsOperator.AndAlso, Kind("Number"), Call(numeric.Reader, Cs.Symbol("e"),
                    OutVar("number"))), Return(Cast(enumType, Cs.Symbol("number")))),
                ThrowJson())).End();
    }

    private static CsInstruction Loop(int index, string table, CsTree body) => Cs.Instruction(InstructionKind.For, new()
    {
        Initializer = Cs.Symbol("i", new() { AsVar = true, Value = Cs.Value(0) }),
        Condition = NodeBinary(CsOperator.LessThan, Cs.Symbol("i"),
            NodeMember(Cs.Symbol($"Enum{table}_{index}"), "Length")),
        Iterators = Cs.Compound(Cs.PostIncrement(Cs.Id("i"))), Body = body
    });

    private static CsSymbol Call(string name, CsNode target, params CsNode[] args) =>
        Cs.Symbol(name, new() { Target = target, AsCall = true, Arguments = Cs.Compound(args) });

    private static CsInstruction ThrowJson() => Cs.Instruction(InstructionKind.Throw, new()
        { Value = Cs.Symbol(new() { AsNew = true, Type = Cs.Type("JsonException") }) });

    private static CsSymbol Kind(string name) => NodeBinary(CsOperator.Equal,
        NodeMember(Cs.Symbol("e"), "ValueKind"), NodeMember(Cs.Symbol("JsonValueKind"), name));

    private static (string Type, string Reader)? Underlying(INamedTypeSymbol? type)
    {
        if (type is null || CodecEmitter.IntegralGetter(type.SpecialType) is not { } getter) return null;
        return (type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), "Try" + getter);
    }

    private static string JsonName(IFieldSymbol field) => field.GetAttributes()
        .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() ==
            "System.Text.Json.Serialization.JsonStringEnumMemberNameAttribute")
        ?.ConstructorArguments.FirstOrDefault().Value as string ?? field.Name;

    private static CsExpression ArrayInitializer(CsType type, CsExpression[] values) =>
        new(ExpressionKind.Array, "", []) { Type = type,
            Initializer = new CsExpression(ExpressionKind.Initializer, "", values) };
}

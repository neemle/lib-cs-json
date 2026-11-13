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
            Underlying(type.EnumUnderlyingType) is null) return false;
        return EnumHandlingEmitter.StringConverter(type, type) != false;
    }

    internal static bool IsFlags(ITypeSymbol type) => type.GetAttributes()
        .Any(attribute => attribute.AttributeClass?.ToDisplayString() == "System.FlagsAttribute");

    public static void EmitHelpers(TypeBuilder<FileBuilder> root, INamedTypeSymbol type, int index, bool display)
    {
        if (!CanEmit(type)) throw new InvalidOperationException("Unsupported enum.");
        var numeric = Underlying(type.EnumUnderlyingType)!.Value;
        string enumType = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        bool flags = IsFlags(type);
        var candidates = type.GetMembers().OfType<IFieldSymbol>()
            .Where(field => field.HasConstantValue && field.ConstantValue is not null)
            .OrderBy(field => FlagsEnumEmitter.Bits(field.ConstantValue!));
        var fields = flags ? candidates.OrderByDescending(field =>
            System.Numerics.BitOperations.PopCount(FlagsEnumEmitter.Bits(field.ConstantValue!))).ToArray() :
            candidates.ToArray();
        EmitTables(root, index, enumType, fields);
        if (flags) FlagsEnumEmitter.Emit(root, index, enumType, fields);
        if (display) EmitDisplay(root, index, enumType, numeric.Type, flags, fields);
        EmitWriter(root, index, enumType, numeric.Type, flags);
        EmitReader(root, index, enumType, numeric, flags);
        EmitKeyWriter(root, index, enumType, numeric.Type, flags);
        EmitAllowInteger(root, index);
        EmitNamingPolicy(root, index);
    }

    private static void EmitDisplay(TypeBuilder<FileBuilder> root, int index, string enumType, string underlying,
        bool flags, IFieldSymbol[] fields)
    {
        var names = fields.Select(field => Cs.Literal(field.Name)).ToArray();
        root.Field(Cs.Type("string").Array(), $"EnumClrNames_{index}").Private().Static().ReadOnly()
            .Initialize(ArrayInitializer(Cs.Type("string"), names)).End();
        var match = NodeBinary(CsOperator.Equal, Cs.Symbol("value"),
            Index(Cs.Symbol($"EnumValues_{index}"), Cs.Symbol("i")));
        var body = new List<CsNode> { Loop(index, "Values", Cs.Tree(NodeIf(match,
            Return(Index(Cs.Symbol($"EnumClrNames_{index}"), Cs.Symbol("i")))))) };
        if (flags)
        {
            var order = fields.Select((field, position) => (field, position))
                .Where(item => FlagsEnumEmitter.Bits(item.field.ConstantValue!) != 0)
                .OrderByDescending(item => FlagsEnumEmitter.Bits(item.field.ConstantValue!))
                .Select(item => Cs.Literal(item.position)).ToArray();
            root.Field(Cs.Int.Array(), $"EnumClrOrder_{index}").Private().Static().ReadOnly()
                .Initialize(ArrayInitializer(Cs.Int, order)).End();
            body.Add(NodeVar("remaining", Cs.Symbol("value")));
            body.Add(NodeVar("text", Cs.Value("")));
            var flag = Index(Cs.Symbol($"EnumValues_{index}"), Cs.Symbol("position"));
            var contains = NodeBinary(CsOperator.Equal,
                NodeBinary(CsOperator.BitwiseAnd, Cs.Symbol("remaining"), flag), flag);
            var name = Index(Cs.Symbol($"EnumClrNames_{index}"), Cs.Symbol("position"));
            var separator = Cs.Symbol(new()
            {
                AsConditional = true,
                Parts = new CsNode[] {
                NodeBinary(CsOperator.Equal, Cs.Symbol("text"), Cs.Value("")), Cs.Value(""),
                NodeBinary(CsOperator.Add, Cs.Value(", "), Cs.Symbol("text")) }
            });
            body.Add(NodeEach("position", Cs.Symbol($"EnumClrOrder_{index}"), NodeIf(contains,
                NodeAssign(Cs.Symbol("text"), NodeBinary(CsOperator.Add, name, separator)),
                NodeAssign(Cs.Symbol("remaining"), NodeBinary(CsOperator.ExclusiveOr, Cs.Symbol("remaining"), flag)))));
            body.Add(NodeIf(NodeBinary(CsOperator.AndAlso,
                NodeBinary(CsOperator.Equal, Cs.Symbol("remaining"), Cast(enumType, Cs.Value(0))),
                NodeBinary(CsOperator.NotEqual, Cs.Symbol("text"), Cs.Value(""))), Return(Cs.Symbol("text"))));
        }
        body.Add(Return(Cs.Symbol("ToString", new()
        {
            Target = Cast(underlying, Cs.Symbol("value")),
            AsCall = true,
            Arguments = Cs.Compound(NodeMember(Cs.Symbol("CultureInfo"), "InvariantCulture"))
        })));
        root.Method(Cs.Type("string"), $"FormatEnumDisplay_{index}").Private().Static()
            .Parameter(NodeType(enumType), "value").Body(Cs.Tree(body.ToArray())).End();
    }

    private static void EmitKeyWriter(TypeBuilder<FileBuilder> root, int index, string enumType, string underlying,
        bool flags)
    {
        var match = NodeBinary(CsOperator.Equal, Cs.Symbol("value"),
            Index(Cs.Symbol($"EnumValues_{index}"), Cs.Symbol("i")));
        var policy = Cs.Symbol("DictionaryKeyPolicy", new() { Target = Cs.Symbol("options"), ConditionalAccess = true });
        var body = new List<CsNode> { NodeVar("names", NodeCall($"EnumNamesFor_{index}", Cs.Symbol("options"),
                Cs.Value(null))), Loop(index, "Values", Cs.Tree(NodeIf(match,
            NodeIf(Index(Cs.Symbol($"EnumExact_{index}"), Cs.Symbol("i")),
                Return(Index(Cs.Symbol("names"), Cs.Symbol("i")))),
            Return(NodeCall("ApplyNamingPolicy", policy, Index(Cs.Symbol("names"), Cs.Symbol("i"))))))) };
        if (flags)
        {
            body.Add(NodeVar("text", NodeCall($"FormatFlags_{index}", Cs.Symbol("value"), Cs.Symbol("names"), policy)));
            body.Add(NodeIf(NullTest(Cs.Symbol("text"), true), Return(Cs.Symbol("text"))));
        }
        body.Add(Return(Cs.Symbol("ToString", new()
        {
            Target = Cast(underlying, Cs.Symbol("value")),
            AsCall = true,
            Arguments = Cs.Compound(NodeMember(Cs.Symbol("CultureInfo"), "InvariantCulture"))
        })));
        root.Method(Cs.Type("string"), $"EnumKey_{index}").Private().Static()
            .Parameter(NodeType(enumType), "value").Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Body(Cs.Tree(body.ToArray())).End();
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
        var exact = fields.Select(field => Cs.Literal(HasCustomName(field))).ToArray();
        root.Field(Cs.Bool.Array(), $"EnumExact_{index}").Private().Static().ReadOnly()
            .Initialize(ArrayInitializer(Cs.Bool, exact)).End();
    }

    private static void EmitWriter(TypeBuilder<FileBuilder> root, int index, string enumType, string underlying,
        bool flags)
    {
        var match = NodeBinary(CsOperator.Equal, Cs.Symbol("value"),
            Index(Cs.Symbol($"EnumValues_{index}"), Cs.Symbol("i")));
        var write = Call("WriteStringValue", Cs.Symbol("w"), Index(Cs.Symbol("names"), Cs.Symbol("i")));
        var loop = Loop(index, "Values", Cs.Tree(NodeIf(match, write, Cs.Instruction(InstructionKind.Return))));
        var stringBody = new List<CsNode> { loop };
        if (flags) stringBody.AddRange(FlagsEnumEmitter.Writer(index));
        stringBody.Add(NodeIf(Cs.Symbol(new()
        {
            Operator = CsOperator.Not,
            Parts = new CsNode[] {
            Cs.Symbol("allowInteger") }
        }), ThrowJson()));
        root.Method(Cs.Void, $"WriteEnum_{index}").Private().Static()
            .Parameter(Cs.Type("Utf8JsonWriter"), "w").Parameter(NodeType(enumType), "value")
            .Parameter(Cs.Bool, "stringEnum").Parameter(Cs.Bool, "allowInteger").Parameter(Cs.Type("string").Array(), "names")
            .Body(Cs.Tree(NodeIf(Cs.Symbol("stringEnum"), stringBody.ToArray()),
                Call("WriteNumberValue", Cs.Symbol("w"), Cast(underlying, Cs.Symbol("value"))))).End();
    }

    private static void EmitReader(TypeBuilder<FileBuilder> root, int index, string enumType,
        (string Type, string Reader) numeric, bool flags)
    {
        var equals = NameMatches(index, Cs.Symbol("name"));
        var exact = Call("Equals", TypeRef("global::System.String"), Cs.Symbol("name"),
            Index(Cs.Symbol("names"), Cs.Symbol("i")),
            NodeMember(TypeRef("global::System.StringComparison"), "Ordinal"));
        var exactLoop = Loop(index, "Names", Cs.Tree(NodeIf(exact,
            Return(Index(Cs.Symbol($"EnumValues_{index}"), Cs.Symbol("i"))))));
        var loop = Loop(index, "Names", Cs.Tree(NodeIf(equals,
            Return(Index(Cs.Symbol($"EnumValues_{index}"), Cs.Symbol("i"))))));
        CsNode parse = Call("TryParse", TypeRef(numeric.Type), Cs.Symbol("name"),
            NodeMember(TypeRef("global::System.Globalization.NumberStyles"), "Integer"),
            NodeMember(TypeRef("global::System.Globalization.CultureInfo"), "InvariantCulture"),
            OutVar("parsed"));
        root.Method(NodeType(enumType).Nullable(), $"LookupEnum_{index}").Private().Static()
            .Parameter(Cs.Type("string"), "name").Parameter(Cs.Type("string").Array(), "names")
            .Body(Cs.Tree(exactLoop, loop, Return(Cs.Literal(null)))).End();
        var body = new List<CsNode>
        {
            NodeAssign(Cs.Symbol("name"), Call("Trim", Cs.Symbol("name"))),
            NodeVar("lookup", NodeCall($"LookupEnum_{index}", Cs.Symbol("name"), Cs.Symbol("names"))),
            NodeIf(NodeMember(Cs.Symbol("lookup"), "HasValue"), Return(NodeMember(Cs.Symbol("lookup"), "Value")))
        };
        if (flags) body.AddRange(FlagsEnumEmitter.Reader(index));
        body.Add(NodeIf(NodeBinary(CsOperator.AndAlso, Cs.Symbol("allowInteger"), parse),
            Return(Cast(enumType, Cs.Symbol("parsed")))));
        body.Add(ThrowJson());
        root.Method(NodeType(enumType), $"ReadEnumString_{index}").Private().Static()
            .Parameter(Cs.Type("string"), "name").Parameter(Cs.Bool, "allowInteger")
            .Parameter(Cs.Type("string").Array(), "names")
            .Body(Cs.Tree(body.ToArray())).End();
        root.Method(NodeType(enumType), $"ReadEnum_{index}").Private().Static()
            .Parameter(Cs.Type("JsonElement"), "e").Parameter(Cs.Bool, "stringEnum").Parameter(Cs.Bool, "allowInteger")
            .Parameter(Cs.Type("string").Array(), "names")
            .Body(Cs.Tree(NodeIf(NodeBinary(CsOperator.AndAlso, Cs.Symbol("stringEnum"), Kind("String")),
                    Return(NodeCall($"ReadEnumString_{index}", NonNull(Call("GetString", Cs.Symbol("e"))),
                        Cs.Symbol("allowInteger"), Cs.Symbol("names")))),
                NodeIf(NodeBinary(CsOperator.AndAlso, NodeBinary(CsOperator.OrElse,
                    Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { Cs.Symbol("stringEnum") } }),
                    Cs.Symbol("allowInteger")), NodeBinary(CsOperator.AndAlso, Kind("Number"),
                        Call(numeric.Reader, Cs.Symbol("e"), OutVar("number")))), Return(Cast(enumType, Cs.Symbol("number")))),
                ThrowJson())).End();
    }

    private static void EmitAllowInteger(TypeBuilder<FileBuilder> root, int index)
    {
        var registered = NodeBinary(CsOperator.AndAlso, NullTest(Cs.Symbol("options"), true),
            NodeCall($"Table_{index}.TryGetValue", Cs.Symbol("options"), OutVar("entry")));
        root.Method(Cs.Bool, $"EnumAllowsInteger_{index}").Private().Static()
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Parameter(Cs.Bool.Nullable(), "enumOverride")
            .Body(Cs.Tree(NodeIf(NodeBinary(CsOperator.Equal, Cs.Symbol("enumOverride"), Cs.Value(true)),
                    Return(Cs.Value(true))),
                NodeIf(registered, Return(NodeMember(Cs.Symbol("entry"), "AllowEnumIntegers"))),
                Return(Cs.Value(true)))).End();
    }

    private static void EmitNamingPolicy(TypeBuilder<FileBuilder> root, int index)
    {
        var array = Cs.Symbol(new()
        {
            AsArray = true,
            Type = Cs.Type("string"),
            Arguments = Cs.Compound(
            NodeMember(Cs.Symbol($"EnumNames_{index}"), "Length"))
        });
        var isCustom = Index(Cs.Symbol($"EnumExact_{index}"), Cs.Symbol("i"));
        var original = Index(Cs.Symbol($"EnumNames_{index}"), Cs.Symbol("i"));
        var value = NodeCall("ApplyNamingPolicy", Cs.Symbol("policy"), original);
        var assign = NodeAssign(Index(Cs.Symbol("names"), Cs.Symbol("i")), value);
        var loop = Cs.Instruction(InstructionKind.For, new()
        {
            Initializer = NodeVar("i", Cs.Value(0)),
            Condition = NodeBinary(CsOperator.LessThan, Cs.Symbol("i"), NodeMember(Cs.Symbol("names"), "Length")),
            Iterators = Cs.Compound(Cs.PostIncrement(Cs.Id("i"))),
            Body = Cs.Tree(
                NodeIf(isCustom, NodeAssign(Index(Cs.Symbol("names"), Cs.Symbol("i")), original)),
                NodeIf(Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { isCustom } }), assign))
        });
        root.Method(Cs.Type("string").Array(), $"CreateEnumNames_{index}").Private().Static()
            .Parameter(Cs.Type("JsonNamingPolicy").Nullable(), "policy")
            .Body(Cs.Tree(NodeIf(NullTest(Cs.Symbol("policy")), Return(Cs.Symbol($"EnumNames_{index}"))),
                NodeVar("names", array), loop, Return(Cs.Symbol("names")))).End();
        var registered = NodeBinary(CsOperator.AndAlso, NullTest(Cs.Symbol("options"), true),
            NodeCall($"Table_{index}.TryGetValue", Cs.Symbol("options"), OutVar("entry")));
        root.Method(Cs.Type("string").Array(), $"EnumNamesFor_{index}").Private().Static()
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Parameter(Cs.Bool.Nullable(), "enumOverride")
            .Body(Cs.Tree(NodeIf(NodeBinary(CsOperator.AndAlso,
                    NodeBinary(CsOperator.NotEqual, Cs.Symbol("enumOverride"), Cs.Value(true)), registered),
                NodeIf(NullTest(NodeMember(Cs.Symbol("entry"), "EnumNames"), true),
                    Return(NonNull(NodeMember(Cs.Symbol("entry"), "EnumNames"))))),
                Return(Cs.Symbol($"EnumNames_{index}")))).End();
    }

    private static CsInstruction Loop(int index, string table, CsTree body) => Cs.Instruction(InstructionKind.For, new()
    {
        Initializer = Cs.Symbol("i", new() { AsVar = true, Value = Cs.Value(0) }),
        Condition = NodeBinary(CsOperator.LessThan, Cs.Symbol("i"),
            NodeMember(Cs.Symbol($"Enum{table}_{index}"), "Length")),
        Iterators = Cs.Compound(Cs.PostIncrement(Cs.Id("i"))),
        Body = body
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

    private static bool HasCustomName(IFieldSymbol field) => field.GetAttributes().Any(attribute =>
        attribute.AttributeClass?.ToDisplayString() ==
        "System.Text.Json.Serialization.JsonStringEnumMemberNameAttribute");

    internal static CsNode NameMatches(int index, CsNode name)
    {
        CsNode Compare(CsNode expected, string comparison) => Call("Equals", TypeRef("global::System.String"), name,
            expected,
            NodeMember(TypeRef("global::System.StringComparison"), comparison));
        var ordinary = NodeBinary(CsOperator.Equal, Index(Cs.Symbol($"EnumExact_{index}"), Cs.Symbol("i")),
            Cs.Value(false));
        var current = Index(Cs.Symbol("names"), Cs.Symbol("i"));
        var original = Index(Cs.Symbol($"EnumNames_{index}"), Cs.Symbol("i"));
        return NodeBinary(CsOperator.OrElse, Compare(current, "Ordinal"), NodeBinary(CsOperator.AndAlso, ordinary,
            NodeBinary(CsOperator.OrElse, Compare(current, "OrdinalIgnoreCase"), Compare(original, "OrdinalIgnoreCase"))));
    }

    private static CsExpression ArrayInitializer(CsType type, CsExpression[] values) =>
        new(ExpressionKind.Array, "", [])
        {
            Type = type,
            Initializer = new CsExpression(ExpressionKind.Initializer, "", values)
        };
}

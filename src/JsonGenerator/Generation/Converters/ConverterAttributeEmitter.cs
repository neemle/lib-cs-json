using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static Neemle.Json.Generator.CodecApiEmitter;

namespace Neemle.Json.Generator;

internal static class ConverterAttributeEmitter
{
    internal static bool IsSupported(ISymbol member, ITypeSymbol type) =>
        EnumHandlingEmitter.StringConverter(member, type) != false || Converter(member, type) is not null;

    internal static INamedTypeSymbol? Converter(ISymbol member, ITypeSymbol type)
    {
        var attribute = member.GetAttributes().FirstOrDefault(item =>
            CodecEmitter.IsJsonAttribute(item, "JsonConverterAttribute") || item.AttributeClass is { } candidate &&
                IsCustomAttribute(candidate));
        if (attribute?.AttributeClass is { } custom && IsCustomAttribute(custom) &&
            custom.DeclaringSyntaxReferences.Length != 0) return custom;
        if (attribute?.ConstructorArguments.FirstOrDefault().Value is not INamedTypeSymbol converter) return null;
        if (converter.IsUnboundGenericType && type is INamedTypeSymbol target && converter.Arity == target.TypeArguments.Length)
            converter = converter.ConstructedFrom.Construct(target.TypeArguments.ToArray());
        if (converter.DeclaredAccessibility is not (Accessibility.Internal or Accessibility.Public) ||
            converter.DeclaringSyntaxReferences.Length == 0 || converter.BaseType is not { } parent ||
            !IsFactory(converter) && (parent.OriginalDefinition.ToDisplayString() !=
                "System.Text.Json.Serialization.JsonConverter<T>" ||
                !SymbolEqualityComparer.Default.Equals(parent.TypeArguments[0], type)) ||
            !converter.InstanceConstructors.Any(ctor => ctor.Parameters.Length == 0 &&
                ctor.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or
                    Accessibility.ProtectedOrInternal)) return null;
        return converter;
    }

    private static bool IsFactory(INamedTypeSymbol converter) => converter.BaseType?.ToDisplayString() ==
        "System.Text.Json.Serialization.JsonConverterFactory";

    private static bool IsCustomAttribute(INamedTypeSymbol type) => type.BaseType?.ToDisplayString() ==
        "System.Text.Json.Serialization.JsonConverterAttribute";

    internal static void Emit(TypeBuilder<FileBuilder> root, ITypeSymbol type, INamedTypeSymbol converter, string id)
    {
        string name = TypeName(type);
        bool isAttribute = IsCustomAttribute(converter);
        bool isFactory = IsFactory(converter) || isAttribute;
        string converterName = isFactory ? $"Converter<{name}>" : TypeName(converter);
        string options = $"AttributeDefaults_{id}";
        string table = $"AttributeTable_{id}";
        root.Field(Cs.Type("JsonSerializerOptions"), options).Private().Static().ReadOnly()
            .Initialize(new CsExpression(ExpressionKind.New, "", [])).End();
        root.Field(Cs.Generic("ConditionalWeakTable", Cs.Type("JsonSerializerOptions"), NodeType(converterName)), table)
            .Private().Static().ReadOnly().Initialize(new CsExpression(ExpressionKind.New, "", [])).End();
        var create = isFactory ? NodeCall($"CreateAttribute_{id}", Cs.Symbol("_")) :
            NodeNew(converterName);
        var factory = Cs.Lambda(new CsBody { Tree = Cs.Tree(Return(create)) }, Cs.LambdaParameter("_"))
            with
        { IsStatic = true };
        if (isFactory) EmitFactory(root, name, TypeName(converter), converterName, id, isAttribute);
        CsNode effective = NodeBinary(CsOperator.Coalesce, Cs.Symbol("options"), Cs.Symbol(options));
        var setup = new CsNode[] { NodeVar("effective", effective),
            NodeVar("converter", NodeCall($"{table}.GetValue", Cs.Symbol("effective"), factory)),
            NodeIf(Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] {
                NodeCall("converter.CanConvert", TypeOf(name)) } }), NodeThrow(NodeNew("InvalidOperationException"))) };
        EmitWriter(root, type, name, id, setup);
        EmitReader(root, type, name, id, setup);
    }

    private static void EmitFactory(TypeBuilder<FileBuilder> root, string type, string factoryType,
        string converterType, string id, bool isAttribute)
    {
        var compatible = Cs.Symbol(new()
        {
            AsIs = true,
            Value = Cs.Symbol("created"),
            Pattern = new CsPattern(PatternKind.Declaration) { Type = NodeType(converterType), Name = "converter" }
        });
        var incompatible = Cs.Symbol(new()
        {
            Operator = CsOperator.Not,
            Parts = new CsNode[] { NodeCall("factory.CanConvert", TypeOf(type)) }
        });
        var body = new List<CsNode> { NodeVar("factory", NodeNew(factoryType)) };
        if (!isAttribute) body.Add(NodeIf(incompatible, NodeThrow(NodeNew("InvalidOperationException"))));
        body.Add(NodeVar("created", isAttribute ? NodeCall("factory.CreateConverter", TypeOf(type)) :
            NodeCall("factory.CreateConverter", TypeOf(type), Cs.Symbol("options"))));
        body.Add(NodeIf(compatible, Return(Cs.Symbol("converter"))));
        body.Add(NodeThrow(NodeNew("InvalidOperationException")));
        root.Method(NodeType(converterType), $"CreateAttribute_{id}").Private().Static()
            .Parameter(Cs.Type("JsonSerializerOptions"), "options")
            .Body(Cs.Tree(body.ToArray())).End();
    }

    private static void EmitWriter(TypeBuilder<FileBuilder> root, ITypeSymbol type, string name, string id,
        CsNode[] setup)
    {
        var body = new List<CsNode>(setup);
        if (type.IsReferenceType || IsNullable(type))
            body.Add(NodeIf(NodeBinary(CsOperator.AndAlso, NullTest(Cs.Symbol("value")), Cs.Symbol(new()
            { Operator = CsOperator.Not, Parts = new CsNode[] { NodeMember(Cs.Symbol("converter"), "HandleNull") } })),
                NodeCall("w.WriteNullValue"),
                Cs.Instruction(InstructionKind.Return)));
        body.Add(NodeCall("converter.Write", Cs.Symbol("w"), NonNull(Cs.Symbol("value")), Cs.Symbol("effective")));
        root.Method(Cs.Void, $"WriteAttribute_{id}").Private().Static()
            .Parameter(Cs.Type("Utf8JsonWriter"), "w").Parameter(InputType(name, type.IsReferenceType), "value")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Parameter(Cs.Type("string"), "path")
            .Body(Cs.Tree(body.ToArray())).End();
    }

    private static void EmitReader(TypeBuilder<FileBuilder> root, ITypeSymbol type, string name, string id,
        CsNode[] setup)
    {
        var body = new List<CsNode>(setup);
        if (type.IsReferenceType || IsNullable(type))
            body.Add(NodeIf(NodeBinary(CsOperator.AndAlso,
                NodeBinary(CsOperator.Equal, NodeMember(Cs.Symbol("e"), "ValueKind"), NodeMember(Cs.Symbol("JsonValueKind"), "Null")),
                Cs.Symbol(new()
                {
                    Operator = CsOperator.Not,
                    Parts = new CsNode[] {
                    NodeMember(Cs.Symbol("converter"), "HandleNull") }
                })), Return(NonNull(DefaultValue()))));
        body.Add(NodeVar("reader", NodeNew("Utf8JsonReader", NodeCall("Encoding.UTF8.GetBytes",
            NodeCall("e.GetRawText")))));
        body.Add(NodeIf(Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { NodeCall("reader.Read") } }),
            NodeThrow(NodeNew("JsonException"))));
        body.Add(Return(NonNull(NodeCall("converter.Read", Arg(ArgumentModifier.Ref, Cs.Symbol("reader")),
            TypeOf(name), Cs.Symbol("effective")))));
        root.Method(NodeType(name), $"ReadAttribute_{id}").Private().Static().Parameter(Cs.Type("JsonElement"), "e")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Parameter(Cs.Type("string"), "path")
            .Body(Cs.Tree(body.ToArray())).End();
    }

    private static bool IsNullable(ITypeSymbol type) => type is INamedTypeSymbol named &&
        named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
}

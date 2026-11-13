using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static CodecApiEmitter;

internal static class MapKeyEmitter
{
    internal static void Emit(TypeBuilder<FileBuilder> root, CodecEmitter owner, ITypeSymbol type, int index)
    {
        var attributeConverter = ConverterAttributeEmitter.Converter(type, type);
        EmitWriter(root, type, index, attributeConverter);
        EmitReader(root, type, index, attributeConverter);
        var body = new List<CsNode> { NodeIf(NodeCall($"TryReadMapKey_{index}", Cs.Symbol("name"),
            Cs.Symbol("options"), OutVar("customKey")), Return(Cs.Symbol("customKey"))) };
        if (FrameworkCollection.SupportsKey(type))
        {
            body.AddRange(FrameworkCollection.ReadMapKeyDefault(type, owner));
            body.Add(Return(NonNull(Cs.Symbol("key"))));
        }
        else body.Add(NodeThrow(NodeNew("NotSupportedException")));
        root.Method(NodeType(TypeName(type)), $"ReadMapKey_{index}").Private().Static()
            .Parameter(Cs.Type("string"), "name").Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Body(Cs.Tree(body.ToArray())).End();
    }

    private static void EmitWriter(TypeBuilder<FileBuilder> root, ITypeSymbol type, int index,
        INamedTypeSymbol? attributeConverter)
    {
        var condition = CodecEmitter.ConverterCondition(index);
        var body = new List<CsNode> { NodeIf(condition, NodeCall("converter.WriteAsPropertyName", Cs.Symbol("writer"),
            Cs.Symbol("key"), NonNull(Cs.Symbol("options"))), Return(Cs.Value(true))) };
        if (attributeConverter is not null)
        {
            body.AddRange(AttributeSetup(index, type, attributeConverter));
            body.Add(NodeCall("attributeConverter.WriteAsPropertyName", Cs.Symbol("writer"), Cs.Symbol("key"),
                Cs.Symbol("effective")));
            body.Add(Return(Cs.Value(true)));
        }
        else body.Add(Return(Cs.Value(false)));
        root.Method(Cs.Bool, $"TryWriteMapKey_{index}").Private().Static()
            .Parameter(Cs.Type("Utf8JsonWriter"), "writer").Parameter(NodeType(TypeName(type)), "key")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Body(Cs.Tree(body.ToArray())).End();
    }

    private static void EmitReader(TypeBuilder<FileBuilder> root, ITypeSymbol type, int index,
        INamedTypeSymbol? attributeConverter)
    {
        var escaped = Cs.Symbol("ToString", new() { Target = NodeCall("JsonEncodedText.Encode", Cs.Symbol("name")),
            AsCall = true });
        var json = NodeBinary(CsOperator.Add, NodeBinary(CsOperator.Add, Cs.Value("{\""), escaped), Cs.Value("\":null}"));
        var read = NodeCall("converter.ReadAsPropertyName", Arg(ArgumentModifier.Ref, Cs.Symbol("reader")),
            TypeOf(TypeName(type)), Cs.Symbol("options"));
        var readerBody = new List<CsNode> {
            NodeVar("bytes", NodeCall("Encoding.UTF8.GetBytes", json)),
            NodeVar("reader", NodeNew("Utf8JsonReader", Cs.Symbol("bytes"))),
            NodeCall("reader.Read"), NodeCall("reader.Read"), Return(read) };
        root.Method(NodeType(TypeName(type)), $"ReadMapKeyWithConverter_{index}").Private().Static()
            .Parameter(Cs.Type("string"), "name").Parameter(Cs.Generic("Converter", NodeType(TypeName(type))), "converter")
            .Parameter(Cs.Type("JsonSerializerOptions"), "options").Body(Cs.Tree(readerBody.ToArray())).End();
        var body = new List<CsNode> { NodeAssign(Cs.Symbol("key"), NonNull(DefaultValue())),
            NodeIf(CodecEmitter.ConverterCondition(index), NodeAssign(Cs.Symbol("key"),
                NodeCall($"ReadMapKeyWithConverter_{index}", Cs.Symbol("name"), Cs.Symbol("converter"),
                    NonNull(Cs.Symbol("options")))), Return(Cs.Value(true))) };
        if (attributeConverter is not null)
        {
            body.AddRange(AttributeSetup(index, type, attributeConverter));
            body.Add(NodeAssign(Cs.Symbol("key"), NodeCall($"ReadMapKeyWithConverter_{index}", Cs.Symbol("name"),
                Cs.Symbol("attributeConverter"), Cs.Symbol("effective"))));
            body.Add(Return(Cs.Value(true)));
        }
        else body.Add(Return(Cs.Value(false)));
        root.Method(Cs.Bool, $"TryReadMapKey_{index}").Private().Static().Parameter(Cs.Type("string"), "name")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Parameter(NodeType(TypeName(type)), "key", "out").Body(Cs.Tree(body.ToArray())).End();
    }

    private static CsNode[] AttributeSetup(int index, ITypeSymbol type, INamedTypeSymbol converter)
    {
        var factory = Cs.Lambda(new CsExpression(ExpressionKind.New, "", []) { Type = NodeType(TypeName(converter)) },
            Cs.LambdaParameter("_")) with { IsStatic = true };
        return [NodeVar("effective", NodeBinary(CsOperator.Coalesce, Cs.Symbol("options"),
                Cs.Symbol($"AttributeDefaults_type_{index}"))),
            NodeVar("attributeConverter", NodeCall($"AttributeTable_type_{index}.GetValue", Cs.Symbol("effective"), factory)),
            NodeIf(Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] {
                NodeCall("attributeConverter.CanConvert", TypeOf(TypeName(type))) } }),
                NodeThrow(NodeNew("InvalidOperationException")))];
    }
}

using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static Neemle.Json.Generator.CodecApiEmitter;

namespace Neemle.Json.Generator;

internal static class MapKeyEmitter
{
    internal static void EmitObjectWriter(TypeBuilder<FileBuilder> root, IReadOnlyList<ITypeSymbol> types,
        IReadOnlySet<int> mapKeys)
    {
        root.Field(Cs.Type("JsonSerializerOptions"), "ObjectMapKeyDefaults").Private().Static().ReadOnly()
            .Initialize(new CsExpression(ExpressionKind.New, "", [])).End();
        List<(string Type, string Converter)> primitives =
        [
            ("string", "String"), ("bool", "Boolean"), ("char", "Char"), ("byte", "Byte"),
            ("sbyte", "SByte"), ("short", "Int16"), ("ushort", "UInt16"), ("int", "Int32"),
            ("uint", "UInt32"), ("long", "Int64"), ("ulong", "UInt64"), ("float", "Single"),
            ("double", "Double"), ("decimal", "Decimal"), ("System.Half", "Half"),
            ("System.Int128", "Int128"), ("System.UInt128", "UInt128"), ("System.Guid", "Guid"),
            ("System.DateTime", "DateTime"), ("System.DateTimeOffset", "DateTimeOffset"),
            ("System.DateOnly", "DateOnly"), ("System.TimeOnly", "TimeOnly"),
            ("System.TimeSpan", "TimeSpan"), ("System.Uri", "Uri"), ("System.Version", "Version")
        ];
        foreach (string name in new[] { "BFloat16", "Decimal32", "Decimal64", "Decimal128" })
            if (types.Any(type => type.ToDisplayString() == "System.Numerics." + name))
                primitives.Add(("System.Numerics." + name, name));
        var handlers = new List<CsNode>();
        foreach (var (type, converter) in primitives)
        {
            string variable = "typed" + handlers.Count;
            var match = Cs.Symbol(new()
            {
                AsIs = true,
                Value = Cs.Symbol("key"),
                Pattern = new CsPattern(PatternKind.Declaration) { Type = NodeType(type), Name = variable }
            });
            var options = NodeBinary(CsOperator.Coalesce, Cs.Symbol("options"), Cs.Symbol("ObjectMapKeyDefaults"));
            int index = Enumerable.Range(0, types.Count).FirstOrDefault(i => mapKeys.Contains(i) &&
                types[i].ToDisplayString() == type, -1);
            var statements = new List<CsNode>();
            if (index >= 0) statements.Add(NodeIf(NodeCall($"TryWriteMapKey_{index}", Cs.Symbol("writer"),
                Cs.Symbol(variable), Cs.Symbol("options")), Return(Cs.Value(true))));
            statements.Add(NodeCall($"JsonMetadataServices.{converter}Converter.WriteAsPropertyName",
                Cs.Symbol("writer"), Cs.Symbol(variable), options));
            statements.Add(Return(Cs.Value(true)));
            handlers.Add(NodeIf(match, statements.ToArray()));
        }
        foreach (var (type, index) in types.Select((type, index) => (type, index)).Where(item =>
            item.type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Enum && EnumEmitter.CanEmit((INamedTypeSymbol)item.type)))
        {
            string variable = "typed" + handlers.Count;
            var match = Cs.Symbol(new()
            {
                AsIs = true,
                Value = Cs.Symbol("key"),
                Pattern = new CsPattern(PatternKind.Declaration) { Type = NodeType(TypeName(type)), Name = variable }
            });
            var statements = new List<CsNode>();
            if (mapKeys.Contains(index)) statements.Add(NodeIf(NodeCall($"TryWriteMapKey_{index}",
                Cs.Symbol("writer"), Cs.Symbol(variable), Cs.Symbol("options")), Return(Cs.Value(true))));
            statements.Add(NodeCall("writer.WritePropertyName", NodeCall($"EnumKey_{index}", Cs.Symbol(variable),
                Cs.Symbol("options"))));
            statements.Add(Return(Cs.Value(true)));
            handlers.Add(NodeIf(match, statements.ToArray()));
        }
        foreach (int index in mapKeys.Where(index => types[index].SpecialType != SpecialType.System_Object &&
            !FrameworkCollection.SupportsKey(types[index])))
        {
            string variable = "typed" + handlers.Count;
            var match = Cs.Symbol(new()
            {
                AsIs = true,
                Value = Cs.Symbol("key"),
                Pattern = new CsPattern(PatternKind.Declaration)
                { Type = NodeType(TypeName(types[index])), Name = variable }
            });
            handlers.Add(NodeIf(match, Return(NodeCall($"TryWriteMapKey_{index}", Cs.Symbol("writer"),
                Cs.Symbol(variable), Cs.Symbol("options")))));
        }
        var methods = new List<string>();
        foreach (CsNode[] group in handlers.Chunk(1))
        {
            string name = "TryWriteObjectMapKey_" + methods.Count;
            methods.Add(name);
            ObjectWriter(root, name).Body(Cs.Tree([.. group, Return(Cs.Value(false))])).End();
        }
        int level = 0;
        while (methods.Count > 3)
        {
            var next = new List<string>();
            foreach (string[] group in methods.Chunk(3))
            {
                string name = $"TryWriteObjectMapKeyLevel_{level}_{next.Count}";
                next.Add(name);
                ObjectWriter(root, name).Body(Cs.Tree([.. group.Select(method => (CsNode)NodeIf(
                    NodeCall(method, Cs.Symbol("writer"), Cs.Symbol("key"), Cs.Symbol("options")),
                    Return(Cs.Value(true)))), Return(Cs.Value(false))])).End();
            }
            methods = next;
            level++;
        }
        ObjectWriter(root, "TryWriteObjectMapKey").Body(Cs.Tree([.. methods.Select(method => (CsNode)NodeIf(
            NodeCall(method, Cs.Symbol("writer"), Cs.Symbol("key"), Cs.Symbol("options")),
            Return(Cs.Value(true)))), Return(Cs.Value(false))])).End();
    }

    private static MethodBuilder<TypeBuilder<FileBuilder>> ObjectWriter(TypeBuilder<FileBuilder> root, string name) =>
        root.Method(Cs.Bool, name).Private().Static().Parameter(Cs.Type("Utf8JsonWriter"), "writer")
            .Parameter(Cs.Type("object"), "key").Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options");

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
        var escaped = Cs.Symbol("ToString", new()
        {
            Target = NodeCall("JsonEncodedText.Encode", Cs.Symbol("name")),
            AsCall = true
        });
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
        bool factoryConverter = converter.BaseType?.ToDisplayString() is
            "System.Text.Json.Serialization.JsonConverterFactory" or
            "System.Text.Json.Serialization.JsonConverterAttribute";
        CsNode create = factoryConverter ? NodeCall($"CreateAttribute_type_{index}", Cs.Symbol("_")) :
            NodeNew(TypeName(converter));
        var factory = Cs.Lambda(new CsBody { Tree = Cs.Tree(Return(create)) }, Cs.LambdaParameter("_")) with
        { IsStatic = true };
        return [NodeVar("effective", NodeBinary(CsOperator.Coalesce, Cs.Symbol("options"),
                Cs.Symbol($"AttributeDefaults_type_{index}"))),
            NodeVar("attributeConverter", NodeCall($"AttributeTable_type_{index}.GetValue", Cs.Symbol("effective"), factory)),
            NodeIf(Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] {
                NodeCall("attributeConverter.CanConvert", TypeOf(TypeName(type))) } }),
                NodeThrow(NodeNew("InvalidOperationException")))];
    }
}

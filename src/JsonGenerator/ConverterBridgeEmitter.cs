using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static CodecApiEmitter;

internal static class ConverterBridgeEmitter
{
    private const int BatchSize = 5;

    internal static void Emit(TypeBuilder<FileBuilder> root, IReadOnlyList<ITypeSymbol> types)
    {
        EmitTypedAdapter(root);
        EmitFactoryAdapter(root);
        var leaves = new List<string>();
        for (int offset = 0; offset < types.Count; offset += BatchSize)
        {
            string name = "AdaptLeaf_" + leaves.Count;
            leaves.Add(name);
            var body = new List<CsNode>();
            foreach (int index in Enumerable.Range(offset, Math.Min(BatchSize, types.Count - offset)))
            {
                EmitTypedFactory(root, types[index], index);
                body.Add(NodeIf(NodeBinary(CsOperator.Equal, Cs.Symbol("type"), TypeOf(TypeName(types[index]))),
                    Return(NodeCall("AdaptTyped_" + index, Cs.Symbol("value")))));
            }
            body.Add(Return(Cs.Value(null)));
            AdapterMethod(root, name).Body(Cs.Tree(body.ToArray())).End();
        }
        int level = 0;
        while (leaves.Count > 1) leaves = EmitLevel(root, leaves, level++);
        var result = leaves.Count == 0 ? Cs.Value(null) : NodeCall(leaves[0], Cs.Symbol("type"), Cs.Symbol("value"));
        root.Method(Cs.Type("JsonConverter"), "AdaptConverter").Private().Static()
            .Parameter(Cs.Type("Type"), "type").Parameter(Cs.Type("object"), "value")
            .Body(Cs.Tree(NodeVar("adapted", result), NodeIf(NullTest(Cs.Symbol("adapted")),
                NodeThrow(NodeNew("InvalidOperationException"))), Return(Cs.Symbol("adapted")))).End();
    }

    private static void EmitTypedAdapter(TypeBuilder<FileBuilder> root)
    {
        var adapter = root.Class("ConverterAdapter").Private().Sealed().Generic("T")
            .Parameter(Cs.Generic("Converter", Cs.Type("T")), "inner").Base(Cs.Generic("JsonConverter", Cs.Type("T")));
        adapter.Property(Cs.Bool, "HandleNull").Public().Override().Expression(Cs.Member(Cs.Id("inner"), "HandleNull")).End();
        foreach (string method in new[] { "Read", "ReadAsPropertyName" })
            adapter.Method(method == "Read" ? Cs.Type("T").Nullable() : Cs.Type("T"), method).Public().Override()
                .Parameter(Cs.Type("Utf8JsonReader"), "reader", "ref").Parameter(Cs.Type("Type"), "typeToConvert")
                .Parameter(Cs.Type("JsonSerializerOptions"), "options")
                .Body(Cs.Tree(Return(NodeCall("inner." + method, Arg(ArgumentModifier.Ref, Cs.Symbol("reader")),
                    Cs.Symbol("typeToConvert"), Cs.Symbol("options"))))).End();
        foreach (string method in new[] { "Write", "WriteAsPropertyName" })
            adapter.Method(Cs.Void, method).Public().Override().Parameter(Cs.Type("Utf8JsonWriter"), "writer")
                .Parameter(Cs.Type("T"), "value").Parameter(Cs.Type("JsonSerializerOptions"), "options")
                .Body(Cs.Tree(NodeCall("inner." + method, Cs.Symbol("writer"), Cs.Symbol("value"),
                    Cs.Symbol("options")))).End();
        adapter.End();
    }

    private static void EmitFactoryAdapter(TypeBuilder<FileBuilder> root)
    {
        var adapter = root.Class("FactoryAdapter").Private().Sealed().Parameter(Cs.Type("ConverterFactory"), "inner")
            .Base(Cs.Type("JsonConverterFactory"));
        adapter.Method(Cs.Bool, "CanConvert").Public().Override().Parameter(Cs.Type("Type"), "typeToConvert")
            .Body(Cs.Tree(Return(NodeCall("inner.CanConvert", Cs.Symbol("typeToConvert"))))).End();
        adapter.Method(Cs.Type("JsonConverter"), "CreateConverter").Public().Override()
            .Parameter(Cs.Type("Type"), "typeToConvert").Parameter(Cs.Type("JsonSerializerOptions"), "options")
            .Body(Cs.Tree(Return(NodeCall("AdaptConverter", Cs.Symbol("typeToConvert"),
                NodeCall("inner.CreateConverter", Cs.Symbol("typeToConvert"), Cs.Symbol("options")))))).End();
        adapter.End();
        root.Method(Cs.Type("JsonConverter"), "BridgeWrapFactory").Internal().Static()
            .Parameter(Cs.Type("ConverterFactory"), "factory")
            .Body(Cs.Tree(Return(NodeNew("FactoryAdapter", Cs.Symbol("factory"))))).End();
    }

    private static void EmitTypedFactory(TypeBuilder<FileBuilder> root, ITypeSymbol type, int index)
    {
        var converter = Cs.Generic("Converter", NodeType(TypeName(type)));
        var pattern = Cs.Symbol(new()
        {
            AsIs = true,
            Value = Cs.Symbol("value"),
            Pattern = new CsPattern(PatternKind.Declaration) { Type = converter, Name = "typed" }
        });
        root.Method(Cs.Type("JsonConverter"), "AdaptTyped_" + index).Private().Static()
            .Parameter(Cs.Type("object"), "value").Body(Cs.Tree(
                NodeIf(pattern, Return(NodeNew("ConverterAdapter<" + TypeName(type) + ">", Cs.Symbol("typed")))),
                NodeThrow(NodeNew("InvalidOperationException")))).End();
    }

    private static List<string> EmitLevel(TypeBuilder<FileBuilder> root, List<string> input, int level)
    {
        var result = new List<string>();
        for (int offset = 0; offset < input.Count; offset += BatchSize)
        {
            string name = $"AdaptLevel_{level}_{result.Count}";
            result.Add(name);
            var body = new List<CsNode>();
            foreach (int index in Enumerable.Range(offset, Math.Min(BatchSize, input.Count - offset)))
            {
                string local = "candidate" + index;
                body.Add(NodeVar(local, NodeCall(input[index], Cs.Symbol("type"), Cs.Symbol("value"))));
                body.Add(NodeIf(NullTest(Cs.Symbol(local), true), Return(Cs.Symbol(local))));
            }
            body.Add(Return(Cs.Value(null)));
            AdapterMethod(root, name).Body(Cs.Tree(body.ToArray())).End();
        }
        return result;
    }

    private static MethodBuilder<TypeBuilder<FileBuilder>> AdapterMethod(TypeBuilder<FileBuilder> root, string name) =>
        root.Method(Cs.Type("JsonConverter").Nullable(), name).Private().Static()
            .Parameter(Cs.Type("Type"), "type").Parameter(Cs.Type("object"), "value");
}

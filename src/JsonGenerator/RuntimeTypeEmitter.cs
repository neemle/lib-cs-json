using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static CodecApiEmitter;

internal static class RuntimeTypeEmitter
{
    internal static void EmitPublic(TypeBuilder<FileBuilder> root)
    {
        foreach (bool encode in new[] { true, false })
        {
            string method = encode ? "EncodeRuntime" : "DecodeRuntime";
            string result = encode ? "string" : "object?";
            string argument = encode ? "value" : "json";
            string type = encode ? "inputType" : "returnType";
            root.Method(NodeType(result), method).Internal().Static()
                .Parameter(NodeType(encode ? "object?" : "string"), argument)
                .Parameter(Cs.Type("Type"), type)
                .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options", @default: Cs.Literal(null))
                .Body(Cs.Tree(NodeIf(NullTest(Cs.Symbol(type)),
                        NodeThrow(NodeNew("ArgumentNullException", Cs.Value(type)))),
                    NodeIf(NodeCall("Try" + method, Cs.Symbol(argument), Cs.Symbol(type), Cs.Symbol("options"),
                            Arg(ArgumentModifier.Out, Cs.Symbol("result", new()
                            { Operation = SymbolOperation.Declaration, Type = NodeType(result) }))),
                        Return(Cs.Symbol("result"))),
                    NodeThrow(NodeNew("NotSupportedException", Cs.Value("The runtime JSON type was not generated.")))))
                .End();
        }
        root.Method(Cs.Type("byte").Array(), "EncodeRuntimeBytes").Internal().Static()
            .Parameter(Cs.Type("object").Nullable(), "value").Parameter(Cs.Type("Type"), "inputType")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options", @default: Cs.Literal(null))
            .Body(Cs.Tree(Return(NodeCall("Encoding.UTF8.GetBytes", NodeCall("EncodeRuntime", Cs.Symbol("value"),
                Cs.Symbol("inputType"), Cs.Symbol("options")))))).End();
        root.Method(NodeType("object?"), "DecodeRuntimeBytes").Internal().Static()
            .Parameter(NodeType("ReadOnlySpan<byte>"), "utf8Json").Parameter(Cs.Type("Type"), "returnType")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options", @default: Cs.Literal(null))
            .Body(Cs.Tree(Cs.Symbol("document", new() { AsVar = true, Using = true,
                    Value = NodeCall("JsonDocument.Parse", NodeCall("utf8Json.ToArray"),
                        NodeCall("CreateDocumentOptions", Cs.Symbol("options"), Cs.Value(false))) }),
                Return(NodeCall("DecodeRuntime", NodeCall("document.RootElement.GetRawText"), Cs.Symbol("returnType"),
                    Cs.Symbol("options"))))).End();
        root.Method(NodeType("object?"), "DecodeRuntimeChars").Internal().Static()
            .Parameter(NodeType("ReadOnlySpan<char>"), "json").Parameter(Cs.Type("Type"), "returnType")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options", @default: Cs.Literal(null))
            .Body(Cs.Tree(Return(NodeCall("DecodeRuntime", NodeCall("json.ToString"), Cs.Symbol("returnType"),
                Cs.Symbol("options"))))).End();
        EmitStreamMethods(root);
        EmitDomMethods(root);
        EmitReaderWriter(root);
    }

    private static void EmitReaderWriter(TypeBuilder<FileBuilder> root)
    {
        var encoded = NodeCall("EncodeRuntime", Cs.Symbol("value"), Cs.Symbol("inputType"), Cs.Symbol("options"));
        root.Method(Cs.Void, "EncodeRuntimeWriter").Internal().Static().Parameter(Cs.Type("Utf8JsonWriter"), "writer")
            .Parameter(Cs.Type("object").Nullable(), "value").Parameter(Cs.Type("Type"), "inputType")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options", @default: Cs.Literal(null))
            .Body(Cs.Tree(Cs.Symbol("document", new() { AsVar = true, Using = true,
                Value = NodeCall("JsonDocument.Parse", encoded) }),
                NodeCall("document.RootElement.WriteTo", Cs.Symbol("writer")))).End();
        var reader = Arg(ArgumentModifier.Ref, Cs.Symbol("reader"));
        root.Method(NodeType("object?"), "DecodeRuntimeReader").Internal().Static()
            .Parameter(Cs.Type("Utf8JsonReader"), "reader", "ref").Parameter(Cs.Type("Type"), "returnType")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options", @default: Cs.Literal(null))
            .Body(Cs.Tree(Cs.Symbol("document", new() { AsVar = true, Using = true,
                Value = NodeCall("JsonDocument.ParseValue", reader) }),
                Return(NodeCall("DecodeRuntime", NodeCall("document.RootElement.GetRawText"), Cs.Symbol("returnType"),
                    Cs.Symbol("options"))))).End();
    }

    private static void EmitDomMethods(TypeBuilder<FileBuilder> root)
    {
        foreach (string kind in new[] { "Element", "Document", "Node" })
        {
            string output = kind == "Node" ? "JsonNode?" : "Json" + kind;
            var json = NodeCall("EncodeRuntime", Cs.Symbol("value"), Cs.Symbol("inputType"), Cs.Symbol("options"));
            var parse = NodeCall(kind == "Node" ? "JsonNode.Parse" : "JsonDocument.Parse", json);
            var body = new List<CsNode>();
            if (kind == "Element")
            {
                body.Add(Cs.Symbol("document", new() { AsVar = true, Using = true, Value = parse }));
                body.Add(Return(NodeCall("document.RootElement.Clone")));
            }
            else body.Add(Return(parse));
            root.Method(NodeType(output), "EncodeRuntime" + kind).Internal().Static()
                .Parameter(Cs.Type("object").Nullable(), "value").Parameter(Cs.Type("Type"), "inputType")
                .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options", @default: Cs.Literal(null))
                .Body(Cs.Tree(body.ToArray())).End();
        }
        foreach (string kind in new[] { "Element", "Document", "Node" })
        {
            string type = kind == "Node" ? "JsonNode?" : "Json" + kind;
            string argument = kind.ToLowerInvariant();
            CsNode json = kind switch
            {
                "Element" => NodeCall(argument + ".GetRawText"),
                "Document" => NodeCall(argument + ".RootElement.GetRawText"),
                _ => NodeBinary(CsOperator.Coalesce, Cs.Symbol("ToJsonString", new()
                    { Target = Cs.Symbol(argument), ConditionalAccess = true, AsCall = true }), Cs.Value("null"))
            };
            root.Method(NodeType("object?"), "DecodeRuntime" + kind).Internal().Static()
                .Parameter(NodeType(type), argument).Parameter(Cs.Type("Type"), "returnType")
                .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options", @default: Cs.Literal(null))
                .Body(Cs.Tree(Return(NodeCall("DecodeRuntime", json, Cs.Symbol("returnType"), Cs.Symbol("options")))))
                .End();
        }
    }

    private static void EmitStreamMethods(TypeBuilder<FileBuilder> root)
    {
        foreach (bool async in new[] { false, true })
        {
            string suffix = async ? "Async" : "";
            var write = root.Method(NodeType(async ? "Task" : "void"), "EncodeRuntimeStream" + suffix)
                .Internal().Static().Parameter(Cs.Type("Stream"), "utf8Json")
                .Parameter(Cs.Type("object").Nullable(), "value").Parameter(Cs.Type("Type"), "inputType")
                .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options", @default: Cs.Literal(null));
            if (async) write.Async().Parameter(Cs.Type("CancellationToken"), "cancellationToken",
                @default: new CsExpression(ExpressionKind.Default, "", []));
            var args = async ? new CsNode[] { Cs.Symbol("bytes"), Cs.Symbol("cancellationToken") } : [Cs.Symbol("bytes")];
            var call = Cs.Symbol(async ? "WriteAsync" : "Write", new() { Target = Cs.Symbol("utf8Json"),
                AsCall = true, Await = async, Arguments = Cs.Compound(args) });
            write.Body(Cs.Tree(NodeVar("bytes", NodeCall("EncodeRuntimeBytes", Cs.Symbol("value"),
                Cs.Symbol("inputType"), Cs.Symbol("options"))), call)).End();
            EmitStreamRead(root, async, suffix);
        }
    }

    private static void EmitStreamRead(TypeBuilder<FileBuilder> root, bool async, string suffix)
    {
        var read = root.Method(NodeType(async ? "ValueTask<object?>" : "object?"), "DecodeRuntimeStream" + suffix)
            .Internal().Static().Parameter(Cs.Type("Stream"), "utf8Json").Parameter(Cs.Type("Type"), "returnType")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options", @default: Cs.Literal(null));
        if (async) read.Async().Parameter(Cs.Type("CancellationToken"), "cancellationToken",
            @default: new CsExpression(ExpressionKind.Default, "", []));
        var args = async ? new CsNode[] { Cs.Symbol("stream"), Cs.Symbol("cancellationToken") } : [Cs.Symbol("stream")];
        var copy = Cs.Symbol(async ? "CopyToAsync" : "CopyTo", new() { Target = Cs.Symbol("utf8Json"),
            AsCall = true, Await = async, Arguments = Cs.Compound(args) });
        read.Body(Cs.Tree(Cs.Symbol("stream", new() { AsVar = true, Using = true, Value = NodeNew("MemoryStream") }),
            copy, Return(NodeCall("DecodeRuntimeBytes", NodeCall("stream.ToArray"), Cs.Symbol("returnType"),
                Cs.Symbol("options"))))).End();
    }

    internal static void Emit(TypeBuilder<FileBuilder> root, IReadOnlyList<ITypeSymbol> types)
    {
        foreach (bool encode in new[] { true, false }) EmitDispatch(root, types, encode);
    }

    private static void EmitDispatch(TypeBuilder<FileBuilder> root, IReadOnlyList<ITypeSymbol> types, bool encode)
    {
        var methods = new List<string>();
        for (int offset = 0; offset < types.Count; offset += 3)
        {
            string method = $"TryRuntime{(encode ? "Encode" : "Decode")}_0_{methods.Count}";
            methods.Add(method);
            var body = new List<CsNode> { NodeAssign(Cs.Symbol("result"), NonNull(DefaultValue())) };
            for (int index = offset; index < Math.Min(offset + 3, types.Count); index++)
            {
                var same = NodeBinary(CsOperator.Equal, Cs.Symbol("type"), TypeOf(TypeName(types[index])));
                var call = NodeCall((encode ? "Encode_" : "Decode_") + index, Cs.Symbol("input"),
                    Cs.Symbol("type"), Cs.Symbol("options"));
                body.Add(NodeIf(same, NodeAssign(Cs.Symbol("result"), call), Return(Cs.Value(true))));
            }
            body.Add(Return(Cs.Value(false)));
            Batch(root, method, encode).Body(Cs.Tree(body.ToArray())).End();
        }
        int level = 1;
        while (methods.Count > 1) methods = Collapse(root, methods, encode, level++);
        var final = Batch(root, "Try" + (encode ? "EncodeRuntime" : "DecodeRuntime"), encode);
        final.Body(Cs.Tree(Return(NodeCall(methods[0], Cs.Symbol("input"), Cs.Symbol("type"), Cs.Symbol("options"),
            Arg(ArgumentModifier.Out, Cs.Symbol("result")))))).End();
    }

    private static List<string> Collapse(TypeBuilder<FileBuilder> root, List<string> methods, bool encode, int level)
    {
        var next = new List<string>();
        foreach (var group in methods.Chunk(3))
        {
            string method = $"TryRuntime{(encode ? "Encode" : "Decode")}_{level}_{next.Count}";
            next.Add(method);
            var body = new List<CsNode> { NodeAssign(Cs.Symbol("result"), NonNull(DefaultValue())) };
            body.AddRange(group.Select(item => (CsNode)NodeIf(NodeCall(item, Cs.Symbol("input"), Cs.Symbol("type"),
                Cs.Symbol("options"), Arg(ArgumentModifier.Out, Cs.Symbol("result"))), Return(Cs.Value(true)))));
            body.Add(Return(Cs.Value(false)));
            Batch(root, method, encode).Body(Cs.Tree(body.ToArray())).End();
        }
        return next;
    }

    private static MethodBuilder<TypeBuilder<FileBuilder>> Batch(TypeBuilder<FileBuilder> root, string name, bool encode) =>
        root.Method(Cs.Bool, name).Private().Static().Parameter(NodeType(encode ? "object?" : "string"), "input")
            .Parameter(Cs.Type("Type"), "type").Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Parameter(NodeType(encode ? "string" : "object?"), "result", "out");
}

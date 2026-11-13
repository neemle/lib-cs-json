using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using Microsoft.CodeAnalysis;
using static CodecApiEmitter;

internal static class JsonDomApiEmitter
{
    internal static void EmitNodeValue(int index, string name, ITypeSymbol type, TypeBuilder<FileBuilder> root)
    {
        var value = NodeCall("node.AsValue");
        root.Method(NodeType(name), $"NodeGetValue_{index}").Internal().Static()
            .Parameter(Cs.Type("JsonNode"), "node")
            .Body(Cs.Tree(Return(NodeCall($"Decode_{index}", Cs.Symbol("ToJsonString", new()
            { Target = value, AsCall = true }))))).End();
        bool nullable = type is INamedTypeSymbol named &&
            named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
        var result = nullable ? NodeType(name) : NodeType(name).Nullable();
        root.Method(result, $"NullableNodeGetValue_{index}").Internal().Static()
            .Parameter(Cs.Type("JsonNode").Nullable(), "node")
            .Body(Cs.Tree(NodeIf(NullTest(Cs.Symbol("node")), Return(DefaultValue())),
                Return(NodeCall($"NodeGetValue_{index}", Cs.Symbol("node"))))).End();
    }

    internal static void Emit(int index, string type, bool allowNull, bool reference, TypeBuilder<FileBuilder> root)
    {
        EmitWriter(index, type, allowNull, reference, root);
        EmitDomWriters(index, type, reference, root);
        EmitDomReaders(index, type, root);
        EmitSpanReaders(index, type, root);
        EmitReader(index, type, root);
        EmitDeclaredStreams(index, type, allowNull, root);
    }

    private static void EmitWriter(int index, string type, bool allowNull, bool reference, TypeBuilder<FileBuilder> root)
    {
        foreach (bool declared in new[] { false, true })
        {
            var method = root.Method(Cs.Void, $"Encode_{index}").Internal().Static()
                .Parameter(Cs.Type("Utf8JsonWriter"), "writer")
                .Parameter(declared ? NodeType("object?") : InputType(type, reference), "value");
            if (declared) method.Parameter(Cs.Type("Type"), "inputType");
            method.Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options", @default: Cs.Literal(null));
            var body = new List<CsNode> { NodeCall("ArgumentNullException.ThrowIfNull", Cs.Symbol("writer")) };
            body.Add(NodeCall("ValidateRuntimeOptions", Cs.Symbol("options")));
            if (declared) body.Add(CheckType(type));
            CsNode value = declared ? CoerceValue(type, allowNull) : Cs.Symbol("value");
            body.Add(NodeCall($"Write_{index}", Cs.Symbol("writer"), value, Cs.Symbol("options"), Cs.Value("$")));
            method.Body(Cs.Tree(body.ToArray())).End();
        }
    }

    private static void EmitDomWriters(int index, string type, bool reference, TypeBuilder<FileBuilder> root)
    {
        foreach (string kind in new[] { "Element", "Document", "Node" })
        foreach (bool declared in new[] { false, true })
        {
            string result = kind switch
            {
                "Element" => "JsonElement",
                "Document" => "JsonDocument",
                _ => "System.Text.Json.Nodes.JsonNode?"
            };
            var method = root.Method(NodeType(result), $"Encode{kind}_{index}").Internal().Static()
                .Parameter(declared ? NodeType("object?") : InputType(type, reference), "value");
            if (declared) method.Parameter(Cs.Type("Type"), "inputType");
            method.Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options", @default: Cs.Literal(null));
            var encoded = declared ? NodeCall($"Encode_{index}", Cs.Symbol("value"), Cs.Symbol("inputType"),
                Cs.Symbol("options")) : NodeCall($"Encode_{index}", Cs.Symbol("value"), Cs.Symbol("options"));
            if (kind == "Element")
                method.Body(Cs.Tree(Cs.Symbol("document", new()
                { AsVar = true, Using = true, Value = NodeCall("JsonDocument.Parse", encoded) }),
                    Return(NodeCall("document.RootElement.Clone")))).End();
            else method.Body(Cs.Tree(Return(NodeCall(kind == "Document" ? "JsonDocument.Parse" : "JsonNode.Parse",
                encoded)))).End();
        }
    }

    private static void EmitDomReaders(int index, string type, TypeBuilder<FileBuilder> root)
    {
        foreach (string kind in new[] { "JsonElement", "JsonDocument", "System.Text.Json.Nodes.JsonNode?" })
        foreach (bool declared in new[] { false, true })
        {
            var method = Reader(root, declared ? "object?" : type, index).Parameter(NodeType(kind), "element");
            if (declared) method.Parameter(Cs.Type("Type"), "inputType");
            method.Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options", @default: Cs.Literal(null));
            CsNode value = kind switch
            {
                "JsonElement" => Cs.Symbol("element"),
                "JsonDocument" => NodeMember(Cs.Symbol("element"), "RootElement"),
                _ => NodeBinary(CsOperator.Coalesce, Cs.Symbol("ToJsonString", new()
                { Target = Cs.Symbol("element"), ConditionalAccess = true, AsCall = true }), Cs.Value("null"))
            };
            string target = kind.StartsWith("System.", StringComparison.Ordinal) ? "Decode" : "Read";
            CsNode call = target == "Read" ? NodeCall($"Read_{index}", value, Cs.Symbol("options"), Cs.Value("$")) :
                NodeCall($"Decode_{index}", value, Cs.Symbol("options"));
            var body = new List<CsNode>();
            if (kind == "JsonDocument") body.Add(NodeCall("ArgumentNullException.ThrowIfNull", Cs.Symbol("element")));
            body.Add(NodeCall("ValidateRuntimeOptions", Cs.Symbol("options")));
            if (declared) body.Add(CheckType(type));
            body.Add(Return(call));
            method.Body(Cs.Tree(body.ToArray())).End();
        }
    }

    private static void EmitSpanReaders(int index, string type, TypeBuilder<FileBuilder> root)
    {
        foreach (string item in new[] { "byte", "char" })
        foreach (bool declared in new[] { false, true })
        {
            var method = Reader(root, declared ? "object?" : type, index)
                .Parameter(NodeType($"ReadOnlySpan<{item}>"), "json");
            if (declared) method.Parameter(Cs.Type("Type"), "inputType");
            method.Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options", @default: Cs.Literal(null));
            var read = Return(NodeCall($"Decode_{index}", NodeCall(item == "byte" ? "json.ToArray" :
                "json.ToString"), Cs.Symbol("options")));
            method.Body(declared ? Cs.Tree(CheckType(type), read) : Cs.Tree(read)).End();
        }
    }

    private static void EmitReader(int index, string type, TypeBuilder<FileBuilder> root)
    {
        foreach (bool declared in new[] { false, true })
        {
            var method = Reader(root, declared ? "object?" : type, index)
                .Parameter(Cs.Type("Utf8JsonReader"), "reader", "ref");
            if (declared) method.Parameter(Cs.Type("Type"), "inputType");
            method.Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options", @default: Cs.Literal(null));
            var body = new List<CsNode>();
            body.Add(NodeCall("ValidateRuntimeOptions", Cs.Symbol("options")));
            if (declared) body.Add(CheckType(type));
            body.Add(Cs.Symbol("document", new()
            { AsVar = true, Using = true, Value = NodeCall("JsonDocument.ParseValue",
                Arg(ArgumentModifier.Ref, Cs.Symbol("reader"))) }));
            body.Add(Return(NodeCall($"Read_{index}", NodeMember(Cs.Symbol("document"), "RootElement"),
                Cs.Symbol("options"), Cs.Value("$"))));
            method.Body(Cs.Tree(body.ToArray())).End();
        }
    }

    private static CsNode CheckType(string type) => NodeIf(
        NodeBinary(CsOperator.NotEqual, Cs.Symbol("inputType"), TypeOf(type)), NodeThrow(NodeNew("NotSupportedException")));

    private static void EmitDeclaredStreams(int index, string type, bool allowNull, TypeBuilder<FileBuilder> root)
    {
        foreach (bool asynchronous in new[] { false, true })
        {
            string suffix = asynchronous ? "Async" : "";
            var writer = root.Method(NodeType(asynchronous ? "Task" : "void"), $"Encode{suffix}_{index}")
                .Internal().Static().Parameter(Cs.Type("Stream"), "utf8Json").Parameter(NodeType("object?"), "value")
                .Parameter(Cs.Type("Type"), "inputType")
                .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options", @default: Cs.Literal(null));
            var reader = ReaderNamed(root, asynchronous ? "ValueTask<object?>" : "object?", $"Decode{suffix}_{index}")
                .Parameter(Cs.Type("Stream"), "utf8Json").Parameter(Cs.Type("Type"), "returnType")
                .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options", @default: Cs.Literal(null));
            if (asynchronous) AddCancellation(writer, reader);
            CsNode[] writeArgs = asynchronous ? [Cs.Symbol("utf8Json"), CoerceValue(type, allowNull),
                Cs.Symbol("options"), Cs.Symbol("cancellationToken")] : [Cs.Symbol("utf8Json"),
                CoerceValue(type, allowNull), Cs.Symbol("options")];
            var write = NodeCall($"Encode{suffix}_{index}", writeArgs);
            writer.Body(Cs.Tree(CheckType(type), asynchronous ? Return(write) : write)).End();
            EmitDeclaredStreamReader(reader, index, type, asynchronous);
        }
    }

    private static void AddCancellation(MethodBuilder<TypeBuilder<FileBuilder>> writer,
        MethodBuilder<TypeBuilder<FileBuilder>> reader)
    {
        writer.Parameter(Cs.Type("CancellationToken"), "cancellationToken",
            @default: new CsExpression(ExpressionKind.Default, "", []));
        reader.Async().Parameter(Cs.Type("CancellationToken"), "cancellationToken",
            @default: new CsExpression(ExpressionKind.Default, "", []));
    }

    private static void EmitDeclaredStreamReader(MethodBuilder<TypeBuilder<FileBuilder>> reader, int index,
        string type, bool asynchronous)
    {
        CsNode[] arguments = asynchronous ? [Cs.Symbol("utf8Json"), Cs.Symbol("options"), Cs.Symbol("cancellationToken")]
            : [Cs.Symbol("utf8Json"), Cs.Symbol("options")];
        var read = Cs.Symbol($"Decode{(asynchronous ? "Async" : "")}_{index}", new()
        { AsCall = true, Await = asynchronous, Arguments = Cs.Compound(arguments) });
        var check = NodeIf(NodeBinary(CsOperator.NotEqual, Cs.Symbol("returnType"), TypeOf(type)),
            NodeThrow(NodeNew("NotSupportedException")));
        reader.Body(Cs.Tree(check, Return(read))).End();
    }

    private static MethodBuilder<TypeBuilder<FileBuilder>> ReaderNamed(TypeBuilder<FileBuilder> root,
        string type, string name) => root.Method(NodeType(type), name).Internal().Static();

    private static MethodBuilder<TypeBuilder<FileBuilder>> Reader(TypeBuilder<FileBuilder> root, string type, int index)
        => root.Method(NodeType(type), $"Decode_{index}").Internal().Static();
}

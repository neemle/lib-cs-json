using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static CodecApiEmitter;

internal static class JsonPositionEmitter
{
    private const string Frame = "JsonPathFrame";
    private static CsType Frames => Cs.Generic("System.Collections.Generic.Stack", Cs.Type(Frame));

    internal static void Emit(TypeBuilder<FileBuilder> root)
    {
        EmitError(root);
        EmitFrame(root);
        EmitNextPath(root);
        EmitToken(root);
        EmitPosition(root);
        EmitLookup(root);
    }

    private static void EmitError(TypeBuilder<FileBuilder> root)
    {
        var error = root.Class("ScalarJsonException").Internal().Sealed().Base(Cs.Type("JsonException"));
        error.Field(Cs.Type("string"), "Text").Internal().ReadOnly().End();
        error.Constructor().Internal().Parameter(Cs.Type("string"), "text").Parameter(Cs.Type("string"), "path")
            .Parameter(Cs.Type("Exception"), "inner").Base(Cs.Id("text"), Cs.Id("path"), Cs.Literal(null),
                Cs.Literal(null), Cs.Id("inner"))
            .Body(Cs.Statement(Cs.Assign(Cs.Id("Text"), Cs.Id("text")))).End();
        error.End();
        root.Method(Cs.Type("JsonException"), "PositionedScalarError").Private().Static()
            .Parameter(Cs.Type("ScalarJsonException"), "error").Parameter(Cs.Type("string"), "json")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Body(Cs.Tree(NodeCall("FindJsonPosition", Cs.Symbol("json"), NodeBinary(CsOperator.Coalesce,
                    NodeMember(Cs.Symbol("error"), "Path"), Cs.Value("$")), Cs.Symbol("options"),
                    OutVar("line"), OutVar("position")),
                Return(NodeNew("JsonException", NodeMember(Cs.Symbol("error"), "Text"),
                    NodeMember(Cs.Symbol("error"), "Path"), Cs.Symbol("line"), Cs.Symbol("position"),
                    NodeMember(Cs.Symbol("error"), "InnerException"))))).End();
    }

    private static void EmitFrame(TypeBuilder<FileBuilder> root)
    {
        var frame = root.Class(Frame).Private().Sealed().Parameter(Cs.Type("string"), "path")
            .Parameter(Cs.Bool, "isArray");
        frame.Field(Cs.Type("string"), "Path").Internal().ReadOnly().Initialize(Cs.Id("path")).End();
        frame.Field(Cs.Bool, "IsArray").Internal().ReadOnly().Initialize(Cs.Id("isArray")).End();
        frame.Field(Cs.Int, "Index").Internal().End();
        frame.Field(Cs.Type("string").Nullable(), "Name").Internal().End();
        frame.End();
    }

    private static void EmitNextPath(TypeBuilder<FileBuilder> root)
    {
        var value = NodeMember(Cs.Symbol("frame"), "Index");
        var number = Cs.Symbol("ToString", new()
        {
            Target = value,
            AsCall = true,
            Arguments = Cs.Compound(NodeMember(Cs.Symbol("CultureInfo"), "InvariantCulture"))
        });
        var arrayPath = NodeBinary(CsOperator.Add, NodeBinary(CsOperator.Add,
            NodeBinary(CsOperator.Add, NodeMember(Cs.Symbol("frame"), "Path"), Cs.Value("[")), number), Cs.Value("]"));
        root.Method(Cs.Type("string"), "NextJsonPath").Private().Static().Parameter(Cs.Type(Frame), "frame")
            .Body(Cs.Tree(NodeIf(NodeMember(Cs.Symbol("frame"), "IsArray"), NodeVar("result", arrayPath),
                    NodeAssign(value, NodeBinary(CsOperator.Add, value, Cs.Value(1))), Return(Cs.Symbol("result"))),
                Return(NodeBinary(CsOperator.Add, NodeBinary(CsOperator.Add, NodeMember(Cs.Symbol("frame"), "Path"),
                    Cs.Value(".")), NodeMember(Cs.Symbol("frame"), "Name"))))).End();
    }

    private static CsNode IsToken(string name) => NodeBinary(CsOperator.Equal,
        NodeMember(Cs.Symbol("reader"), "TokenType"), NodeMember(Cs.Symbol("JsonTokenType"), name));

    private static void EmitToken(TypeBuilder<FileBuilder> root)
    {
        var property = NodeMember(NodeCall("frames.Peek"), "Name");
        var end = NodeBinary(CsOperator.OrElse, IsToken("EndObject"), IsToken("EndArray"));
        var start = NodeBinary(CsOperator.OrElse, IsToken("StartObject"), IsToken("StartArray"));
        var current = Cs.Symbol(new()
        {
            AsConditional = true,
            Parts = new CsNode[] {
            NodeBinary(CsOperator.Equal, NodeMember(Cs.Symbol("frames"), "Count"), Cs.Value(0)), Cs.Value("$"),
            NodeCall("NextJsonPath", NodeCall("frames.Peek")) }
        });
        root.Method(Cs.Bool, "NextJsonTokenPath").Private().Static()
            .Parameter(Cs.Type("Utf8JsonReader"), "reader", "ref").Parameter(Frames, "frames")
            .Parameter(Cs.Type("string"), "path", "out")
            .Body(Cs.Tree(NodeAssign(Cs.Symbol("path"), Cs.Value("")),
                NodeIf(IsToken("PropertyName"), NodeAssign(property, NodeCall("reader.GetString")),
                    Return(Cs.Value(false))),
                NodeIf(end, NodeCall("frames.Pop"), Return(Cs.Value(false))),
                NodeAssign(Cs.Symbol("path"), current),
                NodeIf(start, NodeCall("frames.Push", NodeNew(Frame, Cs.Symbol("path"), IsToken("StartArray")))),
                Return(Cs.Value(true)))).End();
    }

    private static void EmitPosition(TypeBuilder<FileBuilder> root)
    {
        var reset = NodeIf(NodeBinary(CsOperator.Equal, Cs.Symbol("item"), Cs.Value((byte)10)),
            NodeAssign(Cs.Symbol("line"), NodeBinary(CsOperator.Add, Cs.Symbol("line"), Cs.Value(1))),
            NodeAssign(Cs.Symbol("position"), Cs.Value(0)), Cs.Instruction(InstructionKind.Continue));
        root.Method(Cs.Void, "CountJsonPosition").Private().Static().Parameter(Cs.Type("byte").Array(), "bytes")
            .Parameter(Cs.Type("long"), "offset").Parameter(Cs.Type("long"), "line", "out")
            .Parameter(Cs.Type("long"), "position", "out")
            .Body(Cs.Tree(NodeAssign(Cs.Symbol("line"), Cs.Value(0)), NodeAssign(Cs.Symbol("position"), Cs.Value(0)),
                NodeVar("seen", Cs.Value(0)), NodeEach("item", Cs.Symbol("bytes"),
                    NodeIf(NodeBinary(CsOperator.GreaterThanOrEqual, Cs.Symbol("seen"), Cs.Symbol("offset")),
                        Cs.Instruction(InstructionKind.Break)),
                    NodeAssign(Cs.Symbol("seen"), NodeBinary(CsOperator.Add, Cs.Symbol("seen"), Cs.Value(1))),
                    reset, NodeAssign(Cs.Symbol("position"), NodeBinary(CsOperator.Add, Cs.Symbol("position"), Cs.Value(1)))))).End();
    }

    private static void EmitLookup(TypeBuilder<FileBuilder> root)
    {
        CsNode option(string name, CsNode fallback) => NodeBinary(CsOperator.Coalesce,
            Cs.Symbol(name, new() { Target = Cs.Symbol("options"), ConditionalAccess = true }), fallback);
        var readerOptions = Cs.Symbol(new()
        {
            AsNew = true,
            Type = Cs.Type("JsonReaderOptions"),
            Initializer = Cs.Symbol(new()
            {
                AsInitializer = true,
                Parts = new CsNode[] {
                NodeAssign(Cs.Symbol("AllowTrailingCommas"), option("AllowTrailingCommas", Cs.Value(false))),
                NodeAssign(Cs.Symbol("CommentHandling"), option("ReadCommentHandling", NodeMember(Cs.Symbol("JsonCommentHandling"), "Disallow"))),
                NodeAssign(Cs.Symbol("MaxDepth"), option("MaxDepth", Cs.Value(0))) }
            })
        });
        var skip = Cs.Symbol(new()
        {
            Operator = CsOperator.Not,
            Parts = new CsNode[] {
            NodeCall("NextJsonTokenPath", Arg(ArgumentModifier.Ref, Cs.Symbol("reader")), Cs.Symbol("frames"),
                OutVar("current")) }
        });
        var loop = Cs.Instruction(InstructionKind.While, new()
        {
            Condition = NodeCall("reader.Read"),
            Body = Cs.Tree(
            NodeIf(skip, Cs.Instruction(InstructionKind.Continue)),
            NodeIf(NodeBinary(CsOperator.Equal, Cs.Symbol("current"), Cs.Symbol("path")),
                NodeCall("CountJsonPosition", Cs.Symbol("bytes"), NodeMember(Cs.Symbol("reader"), "BytesConsumed"),
                    Arg(ArgumentModifier.Out, Cs.Symbol("line")), Arg(ArgumentModifier.Out, Cs.Symbol("position"))),
                Cs.Instruction(InstructionKind.Return)))
        });
        root.Method(Cs.Void, "FindJsonPosition").Private().Static().Parameter(Cs.Type("string"), "json")
            .Parameter(Cs.Type("string"), "path").Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Parameter(Cs.Type("long"), "line", "out").Parameter(Cs.Type("long"), "position", "out")
            .Body(Cs.Tree(NodeAssign(Cs.Symbol("line"), Cs.Value(0)), NodeAssign(Cs.Symbol("position"), Cs.Value(0)),
                NodeVar("bytes", NodeCall("Encoding.UTF8.GetBytes", Cs.Symbol("json"))),
                NodeVar("reader", NodeNew("Utf8JsonReader", Cs.Symbol("bytes"), readerOptions)),
                NodeVar("frames", NodeNew("System.Collections.Generic.Stack<" + Frame + ">")), loop)).End();
    }
}

using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static Neemle.Json.Generator.CodecApiEmitter;

namespace Neemle.Json.Generator;

internal static class AsyncEnumerableEmitter
{
    internal static void Emit(TypeBuilder<FileBuilder> root, int index, string type)
    {
        EmitCursor(root, index, type);
        EmitReaderOptions(root, index);
        EmitBom(root, index);
        EmitStore(root, index);
        EmitStart(root, index);
        EmitEnd(root, index);
        EmitToken(root, index);
        EmitNextValue(root, index, type);
        EmitNext(root, index, type);
        EmitConsume(root, index);
        EmitAdvance(root, index, type);
        EmitFill(root, index);
        EmitMoveNext(root, index);
        EmitCursorFactory(root, index);
        EmitIterator(root, index, type);
    }

    internal static void EmitPublic(TypeBuilder<FileBuilder> root, int index, string type)
    {
        var result = Cs.Generic("System.Collections.Generic.IAsyncEnumerable", NodeType(type));
        foreach (bool topLevel in new[] { false, true })
        {
            string methodName = (topLevel ? "DecodeTopLevelSequenceAsync_" : "DecodeSequenceAsync_") + index;
            var method = root.Method(result, methodName).Internal().Static().Parameter(Cs.Type("Stream"), "utf8Json");
            if (topLevel) method.Parameter(Cs.Bool, "topLevelValues");
            method.Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options", @default: Cs.Literal(null))
                .Parameter(Cs.Type("CancellationToken"), "cancellationToken",
                    @default: new CsExpression(ExpressionKind.Default, "", []))
                .Body(Cs.Tree(Return(NodeCall("AsyncIterator_" + index, Cs.Symbol("utf8Json"),
                    topLevel ? Cs.Symbol("topLevelValues") : Cs.Value(false), Cs.Symbol("options"),
                    Cs.Symbol("cancellationToken"))))).End();
        }
    }

    private static void EmitCursor(TypeBuilder<FileBuilder> root, int index, string type)
    {
        var cursor = root.Class("AsyncCursor_" + index).Private().Sealed();
        cursor.Field(Cs.Type("JsonReaderState"), "State").Public().End();
        cursor.Field(Cs.Type("byte").Array(), "Buffer").Public()
            .Initialize(new CsExpression(ExpressionKind.NullForgiving, "", new[] { Cs.Literal(null) })).End();
        cursor.Field(NodeType(type), "Value").Public().Initialize(new CsExpression(ExpressionKind.NullForgiving, "",
            new[] { new CsExpression(ExpressionKind.Default, "", []) })).End();
        cursor.Field(Cs.Generic("System.Collections.Generic.Dictionary", Cs.Type("string"), Cs.Type("object"))
            .Nullable(), "References").Public().End();
        cursor.Field(Cs.Type("ReferenceResolver").Nullable(), "Resolver").Public().End();
        foreach (string name in new[] { "Count", "Consumed", "Index" }) cursor.Field(Cs.Int, name).Public().End();
        foreach (string name in new[] { "Started", "Complete", "TopLevel", "Final", "BomChecked" })
            cursor.Field(Cs.Bool, name).Public().End();
        cursor.End();
    }

    private static void EmitBom(TypeBuilder<FileBuilder> root, int index)
    {
        var first = Index(Member("Buffer"), Cs.Value(0));
        var second = Index(Member("Buffer"), Cs.Value(1));
        var third = Index(Member("Buffer"), Cs.Value(2));
        var enough = NodeBinary(CsOperator.GreaterThanOrEqual, Member("Count"), Cs.Value(3));
        var matches = NodeBinary(CsOperator.AndAlso, enough, NodeBinary(CsOperator.AndAlso,
            NodeBinary(CsOperator.Equal, first, Cs.Value(0xef)), NodeBinary(CsOperator.AndAlso,
                NodeBinary(CsOperator.Equal, second, Cs.Value(0xbb)), NodeBinary(CsOperator.Equal, third, Cs.Value(0xbf)))));
        var partial = NodeBinary(CsOperator.AndAlso, Not(Member("Final")), NodeBinary(CsOperator.AndAlso,
            NodeBinary(CsOperator.LessThan, Member("Count"), Cs.Value(3)),
            NodeBinary(CsOperator.OrElse, NodeBinary(CsOperator.Equal, Member("Count"), Cs.Value(0)),
                NodeBinary(CsOperator.Equal, first, Cs.Value(0xef)))));
        root.Method(Cs.Int, "AsyncBom_" + index).Private().Static().Parameter(Cursor(index), "cursor")
            .Body(Cs.Tree(NodeIf(partial, Return(Cs.Value(0))), NodeIf(matches, NodeAssign(Member("Consumed"), Cs.Value(3))),
                NodeAssign(Member("BomChecked"), Cs.Value(true)), Return(Cs.Value(2)))).End();
    }

    private static void EmitReaderOptions(TypeBuilder<FileBuilder> root, int index)
    {
        CsNode Option(string name, CsNode fallback) => NodeBinary(CsOperator.Coalesce, Cs.Symbol(name, new()
        { Target = Cs.Symbol("options"), ConditionalAccess = true }), fallback);
        var settings = Cs.Symbol(new()
        {
            AsNew = true,
            Type = Cs.Type("JsonReaderOptions"),
            Initializer = Cs.Symbol(new()
            {
                AsInitializer = true,
                Parts = new CsNode[] {
                NodeAssign(Cs.Symbol("AllowTrailingCommas"), Option("AllowTrailingCommas", Cs.Value(false))),
                NodeAssign(Cs.Symbol("CommentHandling"), Option("ReadCommentHandling",
                    NodeMember(Cs.Symbol("JsonCommentHandling"), "Disallow"))),
                NodeAssign(Cs.Symbol("MaxDepth"), Option("MaxDepth", Cs.Value(0))),
                NodeAssign(Cs.Symbol("AllowMultipleValues"), Cs.Symbol("topLevelValues")) }
            })
        });
        root.Method(Cs.Type("JsonReaderState"), "AsyncReaderState_" + index).Private().Static()
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Parameter(Cs.Bool, "topLevelValues")
            .Body(Cs.Tree(Return(NodeNew("JsonReaderState", settings)))).End();
    }

    private static void EmitStore(TypeBuilder<FileBuilder> root, int index)
    {
        root.Method(Cs.Void, "AsyncStore_" + index).Private().Static()
            .Parameter(Cs.Type("Utf8JsonReader"), "reader", "ref").Parameter(Cursor(index), "cursor")
            .Body(Cs.Tree(NodeAssign(Member("Consumed"), Cast("int", NodeMember(Cs.Symbol("reader"), "BytesConsumed"))),
                NodeAssign(Member("State"), NodeMember(Cs.Symbol("reader"), "CurrentState")))).End();
    }

    private static void EmitStart(TypeBuilder<FileBuilder> root, int index)
    {
        var read = NodeCall("reader.Read");
        var notRead = Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { read } });
        var kind = NodeBinary(CsOperator.NotEqual, NodeMember(Cs.Symbol("reader"), "TokenType"),
            NodeMember(Cs.Symbol("JsonTokenType"), "StartArray"));
        root.Method(Cs.Int, "AsyncStart_" + index).Private().Static()
            .Parameter(Cs.Type("Utf8JsonReader"), "reader", "ref").Parameter(Cursor(index), "cursor")
            .Body(Cs.Tree(NodeIf(notRead, Store(index), NodeIf(Member("Final"), NodeThrow(NodeNew("JsonException"))),
                    Return(Cs.Value(0))), NodeIf(kind, NodeThrow(NodeNew("JsonException"))),
                NodeAssign(Member("Started"), Cs.Value(true)), Store(index), Return(Cs.Value(2)))).End();
    }

    private static void EmitEnd(TypeBuilder<FileBuilder> root, int index)
    {
        root.Method(Cs.Int, "AsyncEnd_" + index).Private().Static()
            .Parameter(Cs.Type("Utf8JsonReader"), "reader", "ref").Parameter(Cursor(index), "cursor")
            .Body(Cs.Tree(NodeIf(NodeCall("reader.Read"), NodeThrow(NodeNew("JsonException"))), Store(index),
                NodeIf(Member("Final"), Return(Cs.Value(3))), Return(Cs.Value(0)))).End();
    }

    private static void EmitNextValue(TypeBuilder<FileBuilder> root, int index, string type)
    {
        var parse = NodeCall("JsonDocument.TryParseValue", Arg(ArgumentModifier.Ref, Cs.Symbol("reader")), OutVar("document"));
        var body = new List<CsNode> { NodeAssign(Cs.Symbol("value"), NonNull(DefaultValue())),
            NodeVar("before", NodeMember(Cs.Symbol("reader"), "CurrentState")),
            NodeVar("offset", Cast("int", NodeMember(Cs.Symbol("reader"), "BytesConsumed"))) };
        body.Add(NodeVar("status", NodeCall("AsyncToken_" + index, Arg(ArgumentModifier.Ref, Cs.Symbol("reader")),
            Cs.Symbol("cursor"))));
        body.Add(NodeIf(NodeBinary(CsOperator.NotEqual, Cs.Symbol("status"), Cs.Value(1)), Return(Cs.Symbol("status"))));
        body.Add(NodeIf(Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { parse } }),
            NodeAssign(Member("State"), Cs.Symbol("before")), NodeAssign(Member("Consumed"), Cs.Symbol("offset")),
            Return(Cs.Value(0))));
        body.Add(Cs.Symbol("owned", new() { AsVar = true, Using = true, Value = NonNull(Cs.Symbol("document")) }));
        var path = Cs.Symbol(new()
        {
            AsConditional = true,
            Parts = new CsNode[] { Member("TopLevel"), Cs.Value("$"),
            NodeBinary(CsOperator.Add, NodeBinary(CsOperator.Add, Cs.Value("$["), Member("Index")), Cs.Value("]")) }
        });
        body.Add(NodeAssign(Cs.Symbol("value"), NodeCall($"Read_{index}", NodeMember(Cs.Symbol("owned"), "RootElement"),
            Cs.Symbol("options"), path)));
        body.Add(NodeAssign(Member("Index"), NodeBinary(CsOperator.Add, Member("Index"), Cs.Value(1))));
        body.Add(Store(index));
        body.Add(Return(Cs.Value(1)));
        root.Method(Cs.Int, "AsyncValue_" + index).Private().Static()
            .Parameter(Cs.Type("Utf8JsonReader"), "reader", "ref").Parameter(Cursor(index), "cursor")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Parameter(NodeType(type), "value", "out")
            .Body(Cs.Tree(body.ToArray())).End();
    }

    private static void EmitToken(TypeBuilder<FileBuilder> root, int index)
    {
        var notRead = Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { NodeCall("reader.Read") } });
        var arrayEnd = NodeBinary(CsOperator.AndAlso, Not(Member("TopLevel")), NodeBinary(CsOperator.Equal,
            NodeMember(Cs.Symbol("reader"), "TokenType"), NodeMember(Cs.Symbol("JsonTokenType"), "EndArray")));
        root.Method(Cs.Int, "AsyncToken_" + index).Private().Static()
            .Parameter(Cs.Type("Utf8JsonReader"), "reader", "ref").Parameter(Cursor(index), "cursor")
            .Body(Cs.Tree(NodeIf(notRead, Store(index), NodeIf(Member("Final"),
                    NodeIf(Member("TopLevel"), Return(Cs.Value(3))), NodeThrow(NodeNew("JsonException"))),
                    Return(Cs.Value(0))),
                NodeIf(arrayEnd, NodeAssign(Member("Complete"), Cs.Value(true)), Store(index), Return(Cs.Value(2))),
                Return(Cs.Value(1)))).End();
    }

    private static void EmitNext(TypeBuilder<FileBuilder> root, int index, string type)
    {
        var span = Cs.Symbol("AsSpan", new()
        {
            Target = Member("Buffer"),
            AsCall = true,
            Arguments = Cs.Compound(Cs.Value(0), Member("Count"))
        });
        root.Method(Cs.Int, "AsyncNext_" + index).Private().Static().Parameter(Cursor(index), "cursor")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Parameter(NodeType(type), "value", "out")
            .Body(Cs.Tree(NodeAssign(Cs.Symbol("value"), NonNull(DefaultValue())),
                NodeIf(Not(Member("BomChecked")), Return(NodeCall("AsyncBom_" + index, Cs.Symbol("cursor")))),
                NodeVar("reader", NodeNew("Utf8JsonReader", span, Member("Final"), Member("State"))),
                NodeIf(Member("Complete"), Return(NodeCall("AsyncEnd_" + index,
                    Arg(ArgumentModifier.Ref, Cs.Symbol("reader")), Cs.Symbol("cursor")))),
                NodeIf(NodeBinary(CsOperator.AndAlso, Not(Member("Started")), Not(Member("TopLevel"))),
                    Return(NodeCall("AsyncStart_" + index, Arg(ArgumentModifier.Ref, Cs.Symbol("reader")),
                        Cs.Symbol("cursor")))),
                NodeAssign(Member("Started"), Cs.Value(true)), Return(NodeCall("AsyncValue_" + index,
                    Arg(ArgumentModifier.Ref, Cs.Symbol("reader")), Cs.Symbol("cursor"), Cs.Symbol("options"),
                    Arg(ArgumentModifier.Out, Cs.Symbol("value")))))).End();
    }

    private static void EmitConsume(TypeBuilder<FileBuilder> root, int index)
    {
        var remaining = NodeBinary(CsOperator.Subtract, Member("Count"), Member("Consumed"));
        root.Method(Cs.Void, "AsyncConsume_" + index).Private().Static().Parameter(Cursor(index), "cursor")
            .Body(Cs.Tree(NodeIf(NodeBinary(CsOperator.Equal, Member("Consumed"), Cs.Value(0)),
                Cs.Instruction(InstructionKind.Return)), NodeCall("Buffer.BlockCopy", Member("Buffer"),
                Member("Consumed"), Member("Buffer"), Cs.Value(0), remaining),
                NodeAssign(Member("Count"), remaining), NodeAssign(Member("Consumed"), Cs.Value(0)))).End();
    }

    private static void EmitFill(TypeBuilder<FileBuilder> root, int index)
    {
        var resize = NodeCall("Array.Resize", Arg(ArgumentModifier.Ref, Member("Buffer")),
            NodeBinary(CsOperator.Multiply, NodeMember(Member("Buffer"), "Length"), Cs.Value(2)));
        var memory = Cs.Symbol("AsMemory", new()
        {
            Target = Member("Buffer"),
            AsCall = true,
            Arguments = Cs.Compound(Member("Count"))
        });
        var read = Cs.Symbol("ReadAsync", new()
        {
            Target = Cs.Symbol("utf8Json"),
            AsCall = true,
            Await = true,
            Arguments = Cs.Compound(memory, Cs.Symbol("cancellationToken"))
        });
        root.Method(NodeType("Task"), "AsyncFill_" + index).Private().Static().Async()
            .Parameter(Cs.Type("Stream"), "utf8Json").Parameter(Cursor(index), "cursor")
            .Parameter(Cs.Type("CancellationToken"), "cancellationToken")
            .Body(Cs.Tree(NodeIf(Member("Final"), NodeThrow(NodeNew("JsonException"))),
                NodeIf(NodeBinary(CsOperator.Equal, Member("Count"), NodeMember(Member("Buffer"), "Length")),
                    resize), NodeVar("read", read), NodeIf(NodeBinary(CsOperator.Equal, Cs.Symbol("read"), Cs.Value(0)),
                    NodeAssign(Member("Final"), Cs.Value(true)), Cs.Instruction(InstructionKind.Return)),
                NodeAssign(Member("Count"), NodeBinary(CsOperator.Add, Member("Count"), Cs.Symbol("read"))))).End();
    }

    private static void EmitAdvance(TypeBuilder<FileBuilder> root, int index, string type)
    {
        var work = new CsBody
        {
            Tree = Cs.Tree(NodeVar("status", NodeCall("AsyncNext_" + index,
                Cs.Symbol("cursor"), Cs.Symbol("options"), Arg(ArgumentModifier.Out, Cs.Symbol("value")))),
            NodeCall("AsyncConsume_" + index, Cs.Symbol("cursor")), Return(Cs.Symbol("status")))
        };
        var cleanup = new CsBody
        {
            Tree = Cs.Tree(NodeCall("RestoreReferenceRead", Cs.Symbol("previous")),
            NodeCall("SwapReferenceResolver", Cs.Symbol("previousResolver")))
        };
        root.Method(Cs.Int, "AsyncAdvance_" + index).Private().Static().Parameter(Cursor(index), "cursor")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Parameter(Cs.Type("CancellationToken"), "cancellationToken").Parameter(NodeType(type), "value", "out")
            .Body(Cs.Tree(NodeCall("cancellationToken.ThrowIfCancellationRequested"),
                NodeVar("previous", NodeCall("SwapReferenceRead", Member("References"))),
                NodeVar("previousResolver", NodeCall("SwapReferenceResolver", Member("Resolver"))),
                Cs.Try(work, finallyBody: cleanup))).End();
    }

    private static void EmitCursorFactory(TypeBuilder<FileBuilder> root, int index)
    {
        var size = NodeBinary(CsOperator.Coalesce, Cs.Symbol("DefaultBufferSize", new()
        { Target = Cs.Symbol("options"), ConditionalAccess = true }), Cs.Value(16384));
        var array = Cs.Symbol(new() { AsArray = true, Type = Cs.Type("byte"), Arguments = Cs.Compound(size) });
        root.Method(Cursor(index), "CreateAsyncCursor_" + index).Private().Static()
            .Parameter(Cs.Type("Stream"), "utf8Json").Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Parameter(Cs.Bool, "topLevelValues")
            .Body(Cs.Tree(NodeCall("ArgumentNullException.ThrowIfNull", Cs.Symbol("utf8Json")),
                NodeCall("ValidateRuntimeOptions", Cs.Symbol("options")), NodeVar("cursor", NodeNew("AsyncCursor_" + index)),
                NodeAssign(Member("TopLevel"), Cs.Symbol("topLevelValues")), NodeAssign(Member("Buffer"), array),
                NodeIf(ReferenceCycleEmitter.IsPreserve(), NodeAssign(Member("References"),
                    NodeNew("System.Collections.Generic.Dictionary<string, object>"))),
                NodeIf(ReferenceCycleEmitter.IsCustom(), NodeAssign(Member("Resolver"), Cs.Symbol("CreateResolver", new()
                { Target = NonNull(NodeMember(NonNull(Cs.Symbol("options")), "ReferenceHandler")), AsCall = true }))),
                NodeAssign(Member("State"), NodeCall("AsyncReaderState_" + index, Cs.Symbol("options"),
                    Cs.Symbol("topLevelValues"))), Return(Cs.Symbol("cursor")))).End();
    }

    private static void EmitIterator(TypeBuilder<FileBuilder> root, int index, string type)
    {
        var result = Cs.Generic("System.Collections.Generic.IAsyncEnumerable", NodeType(type));
        EmitIteratorBody(root, index, type, result);
    }

    private static void EmitMoveNext(TypeBuilder<FileBuilder> root, int index)
    {
        var next = NodeCall("AsyncAdvance_" + index, Cs.Symbol("cursor"), Cs.Symbol("options"),
            Cs.Symbol("cancellationToken"), Arg(ArgumentModifier.Out, Member("Value")));
        var fill = Cs.Symbol("AsyncFill_" + index, new()
        {
            AsCall = true,
            Await = true,
            Arguments = Cs.Compound(Cs.Symbol("utf8Json"), Cs.Symbol("cursor"), Cs.Symbol("cancellationToken"))
        });
        var loop = Cs.Instruction(InstructionKind.While, new()
        {
            Condition = Cs.Value(true),
            Body = Cs.Tree(
            NodeVar("status", next), NodeIf(NodeBinary(CsOperator.Equal, Cs.Symbol("status"), Cs.Value(1)),
                Return(Cs.Value(true))), NodeIf(NodeBinary(CsOperator.Equal, Cs.Symbol("status"), Cs.Value(3)),
                Return(Cs.Value(false))), NodeIf(NodeBinary(CsOperator.Equal, Cs.Symbol("status"), Cs.Value(0)), fill))
        });
        root.Method(NodeType("Task<bool>"), "AsyncMoveNext_" + index).Private().Static().Async()
            .Parameter(Cs.Type("Stream"), "utf8Json").Parameter(Cursor(index), "cursor")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Parameter(Cs.Type("CancellationToken"), "cancellationToken").Body(Cs.Tree(loop)).End();
    }

    private static void EmitIteratorBody(TypeBuilder<FileBuilder> root, int index, string type, CsType result)
    {
        var next = Cs.Symbol("AsyncMoveNext_" + index, new()
        {
            AsCall = true,
            Await = true,
            Arguments = Cs.Compound(Cs.Symbol("utf8Json"), Cs.Symbol("cursor"), Cs.Symbol("options"),
                Cs.Symbol("cancellationToken"))
        });
        var loop = Cs.Instruction(InstructionKind.While, new()
        {
            Condition = next,
            Body = Cs.Tree(Cs.Instruction(InstructionKind.YieldReturn, new() { Value = Member("Value") }))
        });
        root.Method(result, "AsyncIterator_" + index).Private().Static().Async()
            .Parameter(Cs.Type("Stream"), "utf8Json").Parameter(Cs.Bool, "topLevelValues")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options", @default: Cs.Literal(null))
            .Parameter(new CsParameter(Cs.Type("CancellationToken"), "cancellationToken")
            {
                Default = new CsExpression(ExpressionKind.Default, "", []),
                Attributes = new[] { Cs.Attribute(Cs.Type("EnumeratorCancellation")) }
            })
            .Body(Cs.Tree(NodeVar("cursor", NodeCall("CreateAsyncCursor_" + index, Cs.Symbol("utf8Json"),
                    Cs.Symbol("options"), Cs.Symbol("topLevelValues"))),
                loop)).End();
    }

    private static CsType Cursor(int index) => Cs.Type("AsyncCursor_" + index);
    private static CsSymbol Member(string name) => NodeMember(Cs.Symbol("cursor"), name);
    private static CsSymbol Store(int index) => NodeCall("AsyncStore_" + index,
        Arg(ArgumentModifier.Ref, Cs.Symbol("reader")), Cs.Symbol("cursor"));
    private static CsSymbol Not(CsNode value) => Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { value } });
}

using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static Neemle.Json.Generator.CodecApiEmitter;

namespace Neemle.Json.Generator;

internal static class ReferenceCycleEmitter
{
    internal static void Emit(TypeBuilder<FileBuilder> root)
    {
        var list = Cs.Generic("System.Collections.Generic.List", Cs.Type("object"));
        var table = Cs.Generic("ConditionalWeakTable", Cs.Type("Utf8JsonWriter"), list);
        foreach (string field in new[] { "CyclePaths", "ReferenceIds" })
            root.Field(table, field).Private().Static().ReadOnly()
                .Initialize(new CsExpression(ExpressionKind.New, "", [])).End();
        var resolverTable = Cs.Generic("ConditionalWeakTable", Cs.Type("Utf8JsonWriter"), Cs.Type("ReferenceResolver"));
        var customIds = Cs.Generic("System.Collections.Generic.Dictionary", Cs.Type("object"), Cs.Type("string"));
        root.Field(resolverTable, "CustomResolvers").Private().Static().ReadOnly()
            .Initialize(new CsExpression(ExpressionKind.New, "", [])).End();
        root.Field(Cs.Generic("ConditionalWeakTable", Cs.Type("Utf8JsonWriter"), customIds), "CustomReferenceIds")
            .Private().Static().ReadOnly().Initialize(new CsExpression(ExpressionKind.New, "", [])).End();
        EmitIndex(root, list);
        EmitReferenceId(root);
        EmitPreservedEntry(root, list);
        EmitCustomEntry(root);
        EmitEnter(root, list);
        EmitExit(root);
        EmitWriteId(root);
    }

    private static void EmitIndex(TypeBuilder<FileBuilder> root, CsType list)
    {
        var same = NodeCall("Object.ReferenceEquals", Cs.Symbol("item"), Cs.Symbol("value"));
        root.Method(Cs.Int, "ReferenceIndex").Private().Static().Parameter(list, "items")
            .Parameter(Cs.Type("object"), "value").Body(Cs.Tree(NodeVar("index", Cs.Value(0)),
                NodeEach("item", Cs.Symbol("items"), NodeIf(same, Return(Cs.Symbol("index"))),
                    NodeAssign(Cs.Symbol("index"), NodeBinary(CsOperator.Add, Cs.Symbol("index"), Cs.Value(1)))),
                Return(Cs.Value(-1)))).End();
    }

    private static void EmitReferenceId(TypeBuilder<FileBuilder> root)
    {
        var number = Cs.Symbol(new()
        {
            AsParenthesized = true,
            Value = NodeBinary(CsOperator.Add, Cs.Symbol("index"), Cs.Value(1))
        });
        root.Method(Cs.Type("string"), "ReferenceId").Private().Static().Parameter(Cs.Int, "index")
            .Body(Cs.Tree(Return(Cs.Symbol("ToString", new()
            {
                Target = number,
                AsCall = true,
                Arguments = Cs.Compound(NodeMember(Cs.Symbol("CultureInfo"), "InvariantCulture"))
            })))).End();
    }

    private static void EmitEnter(TypeBuilder<FileBuilder> root, CsType list)
    {
        var factory = Cs.Lambda(new CsExpression(ExpressionKind.New, "", []) { Type = list },
            Cs.LambdaParameter("_")) with
        { IsStatic = true };
        var preserve = NodeBinary(CsOperator.AndAlso, Cs.Symbol("allowPreserve"), IsPreserve());
        var ignore = NodeBinary(CsOperator.Equal, Handler(), IgnoreCycles());
        var inactive = Cs.Symbol(new()
        {
            Operator = CsOperator.Not,
            Parts = new CsNode[] { NodeBinary(CsOperator.OrElse, IsPreserve(), ignore) }
        });
        var remember = NodeIf(NodeBinary(CsOperator.AndAlso, Cs.Symbol("preserve"), Cs.Symbol(new()
        {
            Operator = CsOperator.Not,
            Parts = new CsNode[] { NodeCall("EnterReference", Cs.Symbol("writer"),
            Cs.Symbol("value"), Cs.Symbol("options")) }
        })), Return(Cs.Value(false)));
        var cycle = NodeBinary(CsOperator.AndAlso, ignore, NodeBinary(CsOperator.GreaterThanOrEqual,
            NodeCall("ReferenceIndex", Cs.Symbol("items"), Cs.Symbol("value")), Cs.Value(0)));
        root.Method(Cs.Bool, "EnterCycle").Private().Static().Parameter(Cs.Type("Utf8JsonWriter"), "writer")
            .Parameter(Cs.Type("object"), "value").Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Parameter(Cs.Bool, "allowPreserve").Body(Cs.Tree(NodeVar("preserve", preserve),
                NodeIf(inactive, Return(Cs.Value(true))),
                NodeVar("items", NodeCall("CyclePaths.GetValue", Cs.Symbol("writer"), factory)), remember,
                NodeIf(cycle, NodeCall("writer.WriteNullValue"), Return(Cs.Value(false))),
                NodeCall("items.Add", Cs.Symbol("value")), Return(Cs.Value(true)))).End();
    }

    private static void EmitCustomEntry(TypeBuilder<FileBuilder> root)
    {
        var factory = Cs.Lambda(new CsBody
        {
            Tree = Cs.Tree(Return(NodeNew(
            "System.Collections.Generic.Dictionary<object, string>", NodeMember(
                TypeRef("global::System.Collections.Generic.ReferenceEqualityComparer"), "Instance"))))
        },
            Cs.LambdaParameter("_")) with
        { IsStatic = true };
        var getResolver = NodeCall("CustomResolvers.TryGetValue", Cs.Symbol("writer"), OutVar("resolver"));
        var missing = Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { getResolver } });
        root.Method(Cs.Bool, "EnterReference").Private().Static().Parameter(Cs.Type("Utf8JsonWriter"), "writer")
            .Parameter(Cs.Type("object"), "value").Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Body(Cs.Tree(NodeIf(Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { IsCustom() } }),
                    Return(NodeCall("EnterPreserved", Cs.Symbol("writer"), Cs.Symbol("value")))),
                NodeIf(missing, NodeAssign(Cs.Symbol("resolver"), Cs.Symbol("CreateResolver", new()
                { Target = NonNull(NodeMember(NonNull(Cs.Symbol("options")), "ReferenceHandler")), AsCall = true })),
                    NodeCall("CustomResolvers.Add", Cs.Symbol("writer"), Cs.Symbol("resolver"))),
                NodeVar("id", NodeCall("resolver.GetReference", Cs.Symbol("value"), OutVar("exists"))),
                NodeIf(Cs.Symbol("exists"), NodeCall("writer.WriteStartObject"),
                    NodeCall("writer.WriteString", Cs.Value("$ref"), Cs.Symbol("id")), NodeCall("writer.WriteEndObject"),
                    NodeCall("ClearCustomIfIdle", Cs.Symbol("writer")), Return(Cs.Value(false))),
                NodeVar("ids", NodeCall("CustomReferenceIds.GetValue", Cs.Symbol("writer"), factory)),
                NodeCall("ids.Add", Cs.Symbol("value"), Cs.Symbol("id")), Return(Cs.Value(true)))).End();
        var found = NodeCall("CyclePaths.TryGetValue", Cs.Symbol("writer"), OutVar("items"));
        root.Method(Cs.Void, "ClearCustomIfIdle").Private().Static().Parameter(Cs.Type("Utf8JsonWriter"), "writer")
            .Body(Cs.Tree(NodeIf(NodeBinary(CsOperator.AndAlso, found, NodeBinary(CsOperator.Equal,
                    NodeMember(Cs.Symbol("items"), "Count"), Cs.Value(0))),
                NodeCall("CustomResolvers.Remove", Cs.Symbol("writer")),
                NodeCall("CustomReferenceIds.Remove", Cs.Symbol("writer"))))).End();
    }

    private static void EmitPreservedEntry(TypeBuilder<FileBuilder> root, CsType list)
    {
        var factory = Cs.Lambda(new CsExpression(ExpressionKind.New, "", []) { Type = list },
            Cs.LambdaParameter("_")) with
        { IsStatic = true };
        var existing = NodeBinary(CsOperator.GreaterThanOrEqual, Cs.Symbol("index"), Cs.Value(0));
        root.Method(Cs.Bool, "EnterPreserved").Private().Static().Parameter(Cs.Type("Utf8JsonWriter"), "writer")
            .Parameter(Cs.Type("object"), "value").Body(Cs.Tree(
                NodeVar("ids", NodeCall("ReferenceIds.GetValue", Cs.Symbol("writer"), factory)),
                NodeVar("index", NodeCall("ReferenceIndex", Cs.Symbol("ids"), Cs.Symbol("value"))),
                NodeIf(existing, NodeCall("writer.WriteStartObject"), NodeCall("writer.WriteString", Cs.Value("$ref"),
                    NodeCall("ReferenceId", Cs.Symbol("index"))), NodeCall("writer.WriteEndObject"),
                    Return(Cs.Value(false))), NodeCall("ids.Add", Cs.Symbol("value")), Return(Cs.Value(true)))).End();
    }

    private static void EmitExit(TypeBuilder<FileBuilder> root)
    {
        var active = NodeBinary(CsOperator.OrElse, IsPreserve(), NodeBinary(CsOperator.Equal, Handler(), IgnoreCycles()));
        var found = NodeBinary(CsOperator.AndAlso, active,
            NodeCall("CyclePaths.TryGetValue", Cs.Symbol("writer"), OutVar("items")));
        var clear = NodeBinary(CsOperator.AndAlso, NodeBinary(CsOperator.Equal, NodeMember(Cs.Symbol("items"), "Count"),
            Cs.Value(0)), NodeCall("ReferenceIds.TryGetValue", Cs.Symbol("writer"), OutVar("ids")));
        var empty = NodeBinary(CsOperator.Equal, NodeMember(Cs.Symbol("items"), "Count"), Cs.Value(0));
        root.Method(Cs.Void, "ExitCycle").Private().Static().Parameter(Cs.Type("Utf8JsonWriter"), "writer")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Parameter(Cs.Bool, "allowPreserve")
            .Body(Cs.Tree(NodeIf(found, NodeCall("items.RemoveAt", NodeBinary(CsOperator.Subtract,
                NodeMember(Cs.Symbol("items"), "Count"), Cs.Value(1))), NodeIf(clear, NodeCall("ids.Clear")),
                NodeIf(empty, NodeCall("CustomResolvers.Remove", Cs.Symbol("writer")),
                    NodeCall("CustomReferenceIds.Remove", Cs.Symbol("writer")))))).End();
    }

    private static void EmitWriteId(TypeBuilder<FileBuilder> root)
    {
        var found = NodeCall("ReferenceIds.TryGetValue", Cs.Symbol("writer"), OutVar("ids"));
        var missing = Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { found } });
        root.Method(Cs.Void, "WriteReferenceId").Private().Static().Parameter(Cs.Type("Utf8JsonWriter"), "writer")
            .Parameter(Cs.Type("object"), "value").Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Body(Cs.Tree(NodeIf(Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { IsPreserve() } }),
                    Cs.Instruction(InstructionKind.Return)),
                NodeIf(IsCustom(), NodeCall("WriteCustomReferenceId", Cs.Symbol("writer"), Cs.Symbol("value")),
                    Cs.Instruction(InstructionKind.Return)), NodeIf(missing, NodeThrow(NodeNew("JsonException"))),
                NodeCall("writer.WriteString", Cs.Value("$id"), NodeCall("ReferenceId",
                    NodeCall("ReferenceIndex", Cs.Symbol("ids"), Cs.Symbol("value")))))).End();
        root.Method(Cs.Void, "WriteCustomReferenceId").Private().Static().Parameter(Cs.Type("Utf8JsonWriter"), "writer")
            .Parameter(Cs.Type("object"), "value").Body(Cs.Tree(
                NodeIf(Cs.Symbol(new()
                {
                    Operator = CsOperator.Not,
                    Parts = new CsNode[] {
                    NodeCall("CustomReferenceIds.TryGetValue", Cs.Symbol("writer"), OutVar("customIds")) }
                }),
                    NodeThrow(NodeNew("JsonException"))),
                NodeCall("writer.WriteString", Cs.Value("$id"), Index(Cs.Symbol("customIds"), Cs.Symbol("value"))))).End();
    }

    internal static CsNode IsPreserve() => NodeBinary(CsOperator.AndAlso, NullTest(Handler(), true),
        NodeBinary(CsOperator.NotEqual, Handler(), IgnoreCycles()));

    internal static CsNode IsCustom() => NodeBinary(CsOperator.AndAlso, IsPreserve(),
        NodeBinary(CsOperator.NotEqual, Handler(), NodeMember(Cs.Symbol("ReferenceHandler"), "Preserve")));

    private static CsNode Handler() => Cs.Symbol("ReferenceHandler", new()
    { Target = Cs.Symbol("options"), ConditionalAccess = true });

    private static CsNode IgnoreCycles() => NodeMember(Cs.Symbol("ReferenceHandler"), "IgnoreCycles");

    internal static CsNode[] Wrap(int index, List<CsNode> body, bool numeric, bool allowPreserve)
    {
        var statements = new List<CsNode> { body[0], body[1] };
        var enter = NodeCall("EnterCycle", Cs.Symbol("w"), Cs.Symbol("value"), Cs.Symbol("options"),
            Cs.Value(allowPreserve));
        statements.Add(NodeIf(Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { enter } }),
            Cs.Instruction(InstructionKind.Return)));
        var arguments = new List<CsNode> { Cs.Symbol("w"), Cs.Symbol("value"), Cs.Symbol("options"), Cs.Symbol("path") };
        if (numeric) arguments.Add(Cs.Symbol("handlingOverride"));
        var work = new CsBody { Tree = Cs.Tree(NodeCall($"WriteCore_{index}", arguments.ToArray())) };
        var cleanup = new CsBody
        {
            Tree = Cs.Tree(NodeCall("ExitCycle", Cs.Symbol("w"), Cs.Symbol("options"),
            Cs.Value(allowPreserve)))
        };
        statements.Add(Cs.Try(work, finallyBody: cleanup));
        return statements.ToArray();
    }
}

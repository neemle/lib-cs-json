using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static CodecApiEmitter;

internal static class ReferenceReadEmitter
{
    internal static void Emit(TypeBuilder<FileBuilder> root)
    {
        var map = Cs.Generic("System.Collections.Generic.Dictionary", Cs.Type("string"), Cs.Type("object"));
        root.Field(Cs.Generic("AsyncLocal", map.Nullable()), "ReadReferences").Private().Static().ReadOnly()
            .Initialize(new CsExpression(ExpressionKind.New, "", [])).End();
        root.Field(Cs.Generic("AsyncLocal", Cs.Type("ReferenceResolver").Nullable()), "ReadResolver").Private().Static()
            .ReadOnly().Initialize(new CsExpression(ExpressionKind.New, "", [])).End();
        EmitScope(root, map);
        EmitSwap(root, map);
        EmitRegister(root);
        EmitResolve(root);
        EmitCollectionValues(root);
        root.Method(Cs.Bool, "IsReferenceId").Private().Static().Parameter(Cs.Type("string"), "name")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Body(Cs.Tree(Return(
                NodeBinary(CsOperator.AndAlso, ReferenceCycleEmitter.IsPreserve(),
                    NodeBinary(CsOperator.Equal, Cs.Symbol("name"), Cs.Value("$id")))))).End();
    }

    private static void EmitSwap(TypeBuilder<FileBuilder> root, CsType map)
    {
        root.Method(map.Nullable(), "SwapReferenceRead").Private().Static().Parameter(map.Nullable(), "value")
            .Body(Cs.Tree(NodeVar("previous", NodeMember(Cs.Symbol("ReadReferences"), "Value")),
                NodeIf(NullTest(Cs.Symbol("value"), true), NodeAssign(NodeMember(Cs.Symbol("ReadReferences"), "Value"),
                    Cs.Symbol("value"))), Return(Cs.Symbol("previous")))).End();
        root.Method(Cs.Void, "RestoreReferenceRead").Private().Static().Parameter(map.Nullable(), "previous")
            .Body(Cs.Tree(NodeAssign(NodeMember(Cs.Symbol("ReadReferences"), "Value"), Cs.Symbol("previous")))).End();
        root.Method(Cs.Type("ReferenceResolver").Nullable(), "SwapReferenceResolver").Private().Static()
            .Parameter(Cs.Type("ReferenceResolver").Nullable(), "value")
            .Body(Cs.Tree(NodeVar("previous", NodeMember(Cs.Symbol("ReadResolver"), "Value")),
                NodeAssign(NodeMember(Cs.Symbol("ReadResolver"), "Value"), Cs.Symbol("value")),
                Return(Cs.Symbol("previous")))).End();
    }

    private static void EmitScope(TypeBuilder<FileBuilder> root, CsType map)
    {
        var inactive = Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[]
            { ReferenceCycleEmitter.IsPreserve() } });
        var existing = NullTest(NodeMember(Cs.Symbol("ReadReferences"), "Value"), true);
        root.Method(Cs.Bool, "BeginReferenceRead").Private().Static()
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Body(Cs.Tree(NodeIf(NodeBinary(CsOperator.OrElse, inactive, existing), Return(Cs.Value(false))),
                NodeAssign(NodeMember(Cs.Symbol("ReadReferences"), "Value"), Cs.Symbol(new()
                { Type = map, AsNew = true })),
                NodeIf(ReferenceCycleEmitter.IsCustom(), NodeAssign(NodeMember(Cs.Symbol("ReadResolver"), "Value"),
                    Cs.Symbol("CreateResolver", new() { Target = NonNull(NodeMember(NonNull(Cs.Symbol("options")),
                        "ReferenceHandler")), AsCall = true }))), Return(Cs.Value(true)))).End();
        root.Method(Cs.Void, "EndReferenceRead").Private().Static().Parameter(Cs.Bool, "owner")
            .Body(Cs.Tree(NodeIf(Cs.Symbol("owner"), NodeAssign(NodeMember(Cs.Symbol("ReadReferences"), "Value"),
                Cs.Value(null)), NodeAssign(NodeMember(Cs.Symbol("ReadResolver"), "Value"), Cs.Value(null))))).End();
    }

    private static void EmitRegister(TypeBuilder<FileBuilder> root)
    {
        var found = NodeCall("e.TryGetProperty", Cs.Value("$id"), OutVar("id"));
        var validObject = NodeBinary(CsOperator.Equal, NodeMember(Cs.Symbol("e"), "ValueKind"),
            NodeMember(Cs.Symbol("JsonValueKind"), "Object"));
        var relevant = NodeBinary(CsOperator.AndAlso, ReferenceCycleEmitter.IsPreserve(),
            NodeBinary(CsOperator.AndAlso, validObject, found));
        var add = Cs.Symbol("TryAdd", new() { Target = NonNull(NodeMember(Cs.Symbol("ReadReferences"), "Value")),
            AsCall = true, Arguments = Cs.Compound(NonNull(NodeCall("id.GetString")), Cs.Symbol("value")) });
        root.Method(Cs.Void, "RegisterReference").Private().Static().Parameter(Cs.Type("JsonElement"), "e")
            .Parameter(Cs.Type("object"), "value").Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Body(Cs.Tree(NodeIf(Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { relevant } }),
                    Cs.Instruction(InstructionKind.Return)),
                NodeIf(NodeBinary(CsOperator.NotEqual, NodeMember(Cs.Symbol("id"), "ValueKind"),
                    NodeMember(Cs.Symbol("JsonValueKind"), "String")), NodeThrow(NodeNew("JsonException"))),
                NodeCall("RejectDuplicateMetadata", Cs.Symbol("e"), Cs.Value("$id")),
                NodeCall("RequireFirstMetadata", Cs.Symbol("e"), Cs.Value("$id"), Cs.Symbol("options")),
                NodeIf(NullTest(NodeMember(Cs.Symbol("ReadResolver"), "Value"), true),
                    Cs.Symbol("AddReference", new() { Target = NonNull(NodeMember(Cs.Symbol("ReadResolver"), "Value")),
                        AsCall = true, Arguments = Cs.Compound(NonNull(NodeCall("id.GetString")), Cs.Symbol("value")) }),
                    Cs.Instruction(InstructionKind.Return)),
                NodeIf(Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { add } }),
                    NodeThrow(NodeNew("JsonException"))))).End();
    }

    private static void EmitResolve(TypeBuilder<FileBuilder> root)
    {
        var foundTag = NodeCall("e.TryGetProperty", Cs.Value("$ref"), OutVar("id"));
        var relevant = NodeBinary(CsOperator.AndAlso, ReferenceCycleEmitter.IsPreserve(),
            NodeBinary(CsOperator.AndAlso, NodeBinary(CsOperator.Equal, NodeMember(Cs.Symbol("e"), "ValueKind"),
                NodeMember(Cs.Symbol("JsonValueKind"), "Object")), foundTag));
        var typed = Cs.Symbol(new() { AsIs = true, Value = Cs.Symbol("found"),
            Pattern = new CsPattern(PatternKind.Declaration) { Type = Cs.Type("T"), Name = "typed" } });
        root.Method(Cs.Bool, "TryResolveReference").Private().Static().Generic("T")
            .Parameter(Cs.Type("JsonElement"), "e").Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Parameter(Cs.Type("T"), "result", "out").Body(Cs.Tree(
                NodeAssign(Cs.Symbol("result"), NonNull(DefaultValue())),
                NodeIf(Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { relevant } }),
                    Return(Cs.Value(false))), NodeCall("ValidateReference", Cs.Symbol("e"), Cs.Symbol("id")),
                NodeVar("found", NodeCall("ResolveReferenceValue", NonNull(NodeCall("id.GetString")))),
                NodeIf(Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { typed } }),
                    NodeThrow(NodeNew("JsonException"))), NodeAssign(Cs.Symbol("result"), Cs.Symbol("typed")),
                Return(Cs.Value(true)))).End();
        EmitReferenceValidation(root);
        EmitResolveValue(root);
    }

    private static void EmitResolveValue(TypeBuilder<FileBuilder> root)
    {
        var resolver = NodeMember(Cs.Symbol("ReadResolver"), "Value");
        var lookup = Cs.Symbol("TryGetValue", new() { Target = NonNull(NodeMember(Cs.Symbol("ReadReferences"), "Value")),
            AsCall = true, Arguments = Cs.Compound(Cs.Symbol("id"), OutVar("found")) });
        root.Method(Cs.Type("object"), "ResolveReferenceValue").Private().Static().Parameter(Cs.Type("string"), "id")
            .Body(Cs.Tree(NodeIf(NullTest(resolver, true), Return(Cs.Symbol("ResolveReference", new()
                    { Target = NonNull(resolver), AsCall = true, Arguments = Cs.Compound(Cs.Symbol("id")) }))),
                NodeIf(Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { lookup } }),
                    NodeThrow(NodeNew("JsonException"))), Return(Cs.Symbol("found")))).End();
    }

    private static void EmitReferenceValidation(TypeBuilder<FileBuilder> root)
    {
        root.Method(Cs.Void, "ValidateReference").Private().Static().Parameter(Cs.Type("JsonElement"), "e")
            .Parameter(Cs.Type("JsonElement"), "id").Body(Cs.Tree(
                NodeIf(NodeBinary(CsOperator.NotEqual, NodeMember(Cs.Symbol("id"), "ValueKind"),
                    NodeMember(Cs.Symbol("JsonValueKind"), "String")), NodeThrow(NodeNew("JsonException"))),
                NodeVar("properties", NodeCall("e.EnumerateObject")), NodeCall("properties.MoveNext"),
                NodeIf(NodeCall("properties.MoveNext"), NodeThrow(NodeNew("JsonException"))))).End();
    }

    private static void EmitCollectionValues(TypeBuilder<FileBuilder> root)
    {
        var wrapped = NodeBinary(CsOperator.AndAlso, ReferenceCycleEmitter.IsPreserve(),
            NodeBinary(CsOperator.Equal, NodeMember(Cs.Symbol("e"), "ValueKind"),
                NodeMember(Cs.Symbol("JsonValueKind"), "Object")));
        root.Method(Cs.Type("JsonElement"), "ReferenceValues").Private().Static().Parameter(Cs.Type("JsonElement"), "e")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Body(Cs.Tree(NodeIf(wrapped,
                NodeIf(Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[]
                    { NodeCall("e.TryGetProperty", Cs.Value("$values"), OutVar("values")) } }),
                    NodeThrow(NodeNew("JsonException"))), Return(Cs.Symbol("values"))), Return(Cs.Symbol("e")))).End();
    }

    internal static CsNode Resolve(string type) => Cs.Symbol(new()
    {
        AsCall = true, Target = Cs.Symbol("TryResolveReference", new() { TypeArguments = new[] { NodeType(type) } }),
        Arguments = Cs.Compound(Cs.Symbol("e"), Cs.Symbol("options"), OutVar("reference"))
    });

    internal static CsNode[] Wrap(int index, bool numeric)
    {
        var arguments = new List<CsNode> { Cs.Symbol("e"), Cs.Symbol("options"), Cs.Symbol("path") };
        if (numeric) arguments.Add(Cs.Symbol("handlingOverride"));
        var work = new CsBody { Tree = Cs.Tree(Return(NodeCall($"ReadCore_{index}", arguments.ToArray()))) };
        var cleanup = new CsBody { Tree = Cs.Tree(NodeCall("EndReferenceRead", Cs.Symbol("owner"))) };
        return [NodeVar("owner", NodeCall("BeginReferenceRead", Cs.Symbol("options"))),
            Cs.Try(work, finallyBody: cleanup)];
    }
}

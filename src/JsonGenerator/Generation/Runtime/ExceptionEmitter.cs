using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static Neemle.Json.Generator.CodecApiEmitter;

namespace Neemle.Json.Generator;

internal static class ExceptionEmitter
{
    internal static void Emit(TypeBuilder<FileBuilder> root)
    {
        JsonPositionEmitter.Emit(root);
        EmitNamesEqual(root);
        EmitNamingPolicy(root);
        EmitPropertyNames(root);
        EmitValidateOptions(root);
        DuplicatePropertyEmitter.Emit(root);
        EmitDocumentOptions(root);
        EmitWriteDepthCheck(root);
        EmitCoerceValue(root);
        EmitConverterErrors(root);
        root.Method(Cs.Type("void"), "RequireObject").Private().Static()
            .Parameter(Cs.Type("JsonElement"), "element")
            .Body(Cs.Tree(Cs.Instruction(InstructionKind.If, new()
            {
                Condition = NodeBinary(CsOperator.NotEqual, NodeMember(Cs.Symbol("element"), "ValueKind"),
                    NodeMember(Cs.Symbol("JsonValueKind"), "Object")),
                Body = Cs.Tree(Cs.Instruction(InstructionKind.Throw, new() { Value = NodeNew("JsonException") }))
            })))
            .End();
        root.Method(Cs.Type("T"), "GetSlot").Private().Static().Generic("T")
            .Parameter(Cs.Type("object").Nullable().Array(), "state").Parameter(Cs.Type("int"), "index")
            .Body(Cs.Tree(NodeIf(NullTest(Index(Cs.Symbol("state"), Cs.Symbol("index"))),
                Return(NonNull(DefaultValue()))), Return(Cast("T", NonNull(Index(Cs.Symbol("state"),
                    Cs.Symbol("index")))))))
            .End();
        EmitOptionalSlot(root);
        root.Method(Cs.Type("JsonException"), "InvalidScalar").Private().Static()
            .Parameter(Cs.Type("Exception"), "error").Parameter(Cs.Type("string"), "path")
            .Parameter(Cs.Type("string"), "typeName", @default: Cs.Literal("JSON value"))
            .Body(Cs.Tree(Cs.Instruction(InstructionKind.Return, new()
            {
                Value = NodeNew("ScalarJsonException", NodeBinary(CsOperator.Add,
                    NodeBinary(CsOperator.Add, NodeBinary(CsOperator.Add,
                        Cs.Value("The JSON value could not be converted to "), Cs.Symbol("typeName")),
                        Cs.Value(". Path: ")), Cs.Symbol("path")), Cs.Symbol("path"), Cs.Symbol("error"))
            })))
            .End();
    }

    private static void EmitConverterErrors(TypeBuilder<FileBuilder> root)
    {
        var custom = Cs.Symbol(new()
        {
            AsIs = true,
            Value = Cs.Symbol("error"),
            Pattern = new CsPattern(PatternKind.Declaration) { Type = Cs.Type("UserNotSupportedException"), Name = "user" }
        });
        var message = Cs.Symbol(new()
        {
            AsConditional = true,
            Parts = new CsNode[] { custom,
            NodeBinary(CsOperator.Coalesce, NodeMember(Cs.Symbol("user"), "Text"), Cs.Value("Converter is not supported.")),
            Cs.Value("Converter is not supported.") }
        });
        root.Method(Cs.Type("NotSupportedException"), "ConverterNotSupported").Private().Static()
            .Parameter(Cs.Type("NotSupportedException"), "error").Parameter(Cs.Type("string"), "path")
            .Body(Cs.Tree(Return(NodeNew("NotSupportedException", NodeBinary(CsOperator.Add,
                NodeBinary(CsOperator.Add, message, Cs.Value(" Path: ")), Cs.Symbol("path")), Cs.Symbol("error"))))).End();
        var jsonCustom = Cs.Symbol(new()
        {
            AsIs = true,
            Value = Cs.Symbol("error"),
            Pattern = new CsPattern(PatternKind.Declaration) { Type = Cs.Type("UserJsonException"), Name = "user" }
        });
        var text = Cs.Symbol(new()
        {
            AsConditional = true,
            Parts = new CsNode[] { jsonCustom,
            NodeMember(Cs.Symbol("user"), "Text"), Cs.Value("JSON converter failed.") }
        });
        root.Method(Cs.Type("JsonException"), "ConverterJsonError").Private().Static()
            .Parameter(Cs.Type("JsonException"), "error").Parameter(Cs.Type("string"), "path")
            .Body(Cs.Tree(Return(NodeNew("JsonException", text, Cs.Symbol("path"), Cs.Value(null), Cs.Value(null),
                NodeMember(Cs.Symbol("error"), "InnerException"))))).End();
    }

    private static void EmitValidateOptions(TypeBuilder<FileBuilder> root)
    {
        var nonempty = NodeBinary(CsOperator.NotEqual,
            NodeMember(NodeMember(Cs.Symbol("options"), "Converters"), "Count"), Cs.Value(0));
        var resolver = NullTest(NodeMember(Cs.Symbol("options"), "TypeInfoResolver"), true);
        var populate = NodeBinary(CsOperator.Equal, NodeMember(Cs.Symbol("options"), "PreferredObjectCreationHandling"),
            NodeMember(Cs.Symbol("JsonObjectCreationHandling"), "Populate"));
        var references = NullTest(NodeMember(Cs.Symbol("options"), "ReferenceHandler"), true);
        root.Method(Cs.Void, "ValidateRuntimeOptions").Private().Static()
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Body(Cs.Tree(NodeIf(NullTest(Cs.Symbol("options")), Cs.Instruction(InstructionKind.Return)),
                Cs.Symbol("Lock", new() { Target = NodeCall("Converters", Cs.Symbol("options")), AsCall = true }),
                NodeIf(NodeBinary(CsOperator.AndAlso, populate, references),
                    NodeThrow(NodeNew("InvalidOperationException"))),
                NodeIf(NodeBinary(CsOperator.OrElse, nonempty, resolver),
                    NodeThrow(NodeNew("NotSupportedException"))))).End();
    }

    private static void EmitNamingPolicy(TypeBuilder<FileBuilder> root)
    {
        root.Method(Cs.Type("string"), "ApplyNamingPolicy").Private().Static()
            .Parameter(Cs.Type("JsonNamingPolicy").Nullable(), "policy").Parameter(Cs.Type("string"), "name")
            .Body(Cs.Tree(NodeIf(NullTest(Cs.Symbol("policy")), Return(Cs.Symbol("name"))),
                NodeVar("result", NodeCall("policy.ConvertName", Cs.Symbol("name"))),
                NodeIf(NullTest(Cs.Symbol("result")), NodeThrow(NodeNew("InvalidOperationException"))),
                Return(Cs.Symbol("result")))).End();
    }

    private static void EmitPropertyNames(TypeBuilder<FileBuilder> root)
    {
        var map = Cs.Generic("System.Collections.Generic.Dictionary", Cs.Type("string"), Cs.Type("string"));
        var table = Cs.Generic("ConditionalWeakTable", Cs.Type("JsonSerializerOptions"), map);
        root.Field(table, "PropertyNames").Private().Static().ReadOnly()
            .Initialize(new CsExpression(ExpressionKind.New, "", [])).End();
        var factory = Cs.Lambda(new CsExpression(ExpressionKind.New, "", []) { Type = map },
            Cs.LambdaParameter("_")) with
        { IsStatic = true };
        var locked = Cs.Instruction(InstructionKind.Lock, new()
        {
            Value = Cs.Symbol("items"),
            Body = Cs.Tree(
            NodeIf(NodeCall("items.TryGetValue", Cs.Symbol("key"), OutVar("cached")), Return(Cs.Symbol("cached"))),
            NodeVar("converted", NodeCall("ApplyNamingPolicy", Cs.Symbol("policy"), Cs.Symbol("name"))),
            NodeCall("items.Add", Cs.Symbol("key"), Cs.Symbol("converted")), Return(Cs.Symbol("converted")))
        });
        var policy = Cs.Symbol("PropertyNamingPolicy", new() { Target = Cs.Symbol("options"), ConditionalAccess = true });
        root.Method(Cs.Type("string"), "PropertyName").Private().Static()
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Parameter(Cs.Type("string"), "key")
            .Parameter(Cs.Type("string"), "name").Body(Cs.Tree(NodeVar("policy", policy),
                NodeIf(NullTest(Cs.Symbol("policy")), Return(Cs.Symbol("name"))),
                NodeVar("items", NodeCall("PropertyNames.GetValue", NonNull(Cs.Symbol("options")), factory)), locked)).End();
    }

    private static void EmitOptionalSlot(TypeBuilder<FileBuilder> root)
    {
        var value = Cs.Symbol(new()
        {
            AsCall = true,
            Target = Cs.Symbol("GetSlot", new() { TypeArguments = new[] { Cs.Type("T") } }),
            Arguments = Cs.Compound(Cs.Symbol("state"), Cs.Symbol("index"))
        });
        root.Method(Cs.Type("T"), "GetSlotOrDefault").Private().Static().Generic("T")
            .Parameter(Cs.Type("object").Nullable().Array(), "state").Parameter(Cs.Int, "index")
            .Parameter(Cs.Int, "offset").Parameter(Cs.Type("T"), "fallback")
            .Body(Cs.Tree(NodeIf(NullTest(Index(Cs.Symbol("state"), NodeBinary(CsOperator.Add,
                Cs.Symbol("index"), Cs.Symbol("offset")))), Return(Cs.Symbol("fallback"))), Return(value))).End();
    }

    private static void EmitCoerceValue(TypeBuilder<FileBuilder> root)
    {
        var typed = Cs.Symbol(new()
        {
            AsIs = true,
            Value = Cs.Symbol("value"),
            Pattern = new CsPattern(PatternKind.Declaration) { Type = Cs.Type("T"), Name = "typed" }
        });
        root.Method(Cs.Type("T"), "CoerceValue").Private().Static().Generic("T")
            .Parameter(Cs.Type("object").Nullable(), "value").Parameter(Cs.Bool, "allowNull")
            .Body(Cs.Tree(NodeIf(NullTest(Cs.Symbol("value")),
                    NodeIf(Cs.Symbol("allowNull"), Return(NonNull(DefaultValue()))),
                    NodeThrow(NodeNew("JsonException"))),
                NodeIf(typed, Return(Cs.Symbol("typed"))), NodeThrow(NodeNew("ArgumentException")))).End();
    }

    private static void EmitNamesEqual(TypeBuilder<FileBuilder> root)
    {
        var insensitive = Cs.Symbol("PropertyNameCaseInsensitive", new()
        { Target = Cs.Symbol("options"), ConditionalAccess = true });
        var matches = NodeCall("String.Equals", Cs.Symbol("actual"), Cs.Symbol("expected"),
            NodeMember(Cs.Symbol("StringComparison"), "OrdinalIgnoreCase"));
        var compare = NodeBinary(CsOperator.OrElse,
            NodeBinary(CsOperator.Equal, Cs.Symbol("actual"), Cs.Symbol("expected")),
            NodeBinary(CsOperator.AndAlso, NodeBinary(CsOperator.Equal, insensitive, Cs.Value(true)), matches));
        root.Method(Cs.Bool, "NamesEqual").Private().Static()
            .Parameter(Cs.Type("string"), "actual").Parameter(Cs.Type("string"), "expected")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Body(Cs.Tree(Return(compare))).End();
    }

    private static void EmitDocumentOptions(TypeBuilder<FileBuilder> root)
    {
        CsNode Option(string name) => Cs.Symbol(name, new()
        { Target = Cs.Symbol("options"), ConditionalAccess = true });
        CsNode AssignOption(string name, CsNode fallback) => NodeAssign(Cs.Symbol(name),
            NodeBinary(CsOperator.Coalesce, Option(name == "CommentHandling" ? "ReadCommentHandling" : name), fallback));
        var value = Cs.Symbol(new()
        {
            AsNew = true,
            Type = Cs.Type("JsonDocumentOptions"),
            Initializer = Cs.Symbol(new()
            {
                AsInitializer = true,
                Parts = new CsNode[] { AssignOption("AllowTrailingCommas", Cs.Value(false)),
                    AssignOption("CommentHandling", NodeMember(Cs.Symbol("JsonCommentHandling"), "Disallow")),
                    AssignOption("MaxDepth", Cs.Value(0)) }
            })
        });
        root.Method(Cs.Type("JsonDocumentOptions"), "CreateDocumentOptions").Private().Static()
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Parameter(Cs.Bool, "dom")
            .Body(Cs.Tree(NodeVar("result", value), NodeIf(NodeBinary(CsOperator.AndAlso, Cs.Symbol("dom"),
                DuplicatePropertyEmitter.Disabled()),
                NodeAssign(NodeMember(Cs.Symbol("result"), "AllowDuplicateProperties"), Cs.Value(false))),
                Return(Cs.Symbol("result")))).End();
    }

    private static void EmitWriteDepthCheck(TypeBuilder<FileBuilder> root)
    {
        var depth = Cs.Symbol("MaxDepth", new() { Target = Cs.Symbol("options"), ConditionalAccess = true });
        var error = NodeNew("JsonException", Cs.Value("The object graph exceeds the configured maximum depth."),
            Cs.Symbol("path"), Cs.Value(null), Cs.Value(null));
        root.Method(Cs.Void, "CheckWriteDepth").Private().Static()
            .Parameter(Cs.Type("Utf8JsonWriter"), "writer")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Parameter(Cs.Type("string"), "path")
            .Body(Cs.Tree(NodeVar("maxDepth", NodeBinary(CsOperator.Coalesce, depth, Cs.Value(0))),
                NodeIf(NodeBinary(CsOperator.Equal, Cs.Symbol("maxDepth"), Cs.Value(0)),
                    NodeAssign(Cs.Symbol("maxDepth"), Cs.Value(64))),
                NodeIf(NodeBinary(CsOperator.GreaterThanOrEqual, NodeMember(Cs.Symbol("writer"), "CurrentDepth"),
                    Cs.Symbol("maxDepth")), NodeThrow(error)))).End();
    }
}

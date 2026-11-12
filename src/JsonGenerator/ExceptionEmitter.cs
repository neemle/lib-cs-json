using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static CodecApiEmitter;

internal static class ExceptionEmitter
{
    internal static void Emit(TypeBuilder<FileBuilder> root)
    {
        root.Method(Cs.Type("NotSupportedException"), "ConverterNotSupported").Private().Static()
            .Parameter(Cs.Type("NotSupportedException"), "error").Parameter(Cs.Type("string"), "path")
            .Body(Cs.Tree(Cs.Instruction(InstructionKind.Return, new()
            {
                Value = NodeNew("NotSupportedException",
                NodeBinary(CsOperator.Add, NodeBinary(CsOperator.Add,
                    NodeBinary(CsOperator.Add, NodeMember(Cs.Symbol("error"), "Message"), Cs.Value(" Path: ")),
                    Cs.Symbol("path")), Cs.Value(".")), Cs.Symbol("error"))
            })))
            .End();
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
        root.Method(Cs.Type("JsonException"), "InvalidScalar").Private().Static()
            .Parameter(Cs.Type("Exception"), "error").Parameter(Cs.Type("string"), "path")
            .Body(Cs.Tree(Cs.Instruction(InstructionKind.Return, new()
            {
                Value = NodeNew("JsonException",
                NodeMember(Cs.Symbol("error"), "Message"), Cs.Symbol("path"), Cs.Value(null), Cs.Value(null),
                Cs.Symbol("error"))
            })))
            .End();
        var catches = new[] { "FormatException", "OverflowException" }.Select(type =>
            Cs.Catch(Cs.Type(type), Cs.Body(Cs.Throw(Cs.Call(Cs.Id("InvalidScalar"),
                Cs.Id("error"), Cs.Id("path")))), "error"));
        root.Method(Cs.Type("int"), "ParseInt32").Private().Static()
            .Parameter(Cs.Type("JsonElement"), "element").Parameter(Cs.Type("string"), "path")
            .Body(Cs.Tree(NodeVar("text", NodeCall("element.GetString")),
                Cs.Try(Cs.Body(Cs.Return(Cs.Call(Cs.Member(Cs.Id("Int32"), "Parse"),
                    new CsExpression(ExpressionKind.NullForgiving, "", new[] { Cs.Id("text") }),
                    Cs.Member(Cs.Id("CultureInfo"), "InvariantCulture")))),
                    new Values<CsCatch>(catches))))
            .End();
    }
}

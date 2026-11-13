using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Model;
using static CodecApiEmitter;

internal static class WideNumberEmitter
{
    internal static bool IsWide(ITypeSymbol type) => type.ToDisplayString() is
        "System.Half" or "System.Int128" or "System.UInt128";

    internal static bool IsHalf(ITypeSymbol type) => type.ToDisplayString() == "System.Half";

    internal static void Write(List<CsNode> body, ITypeSymbol type, int index)
    {
        var text = Cs.Symbol("ToString", new() { Target = Cs.Symbol("value"), AsCall = true,
            Arguments = Cs.Compound(Cs.Value(null), NodeMember(Cs.Symbol("CultureInfo"), "InvariantCulture")) });
        body.Add(NodeVar("text", text));
        body.Add(NodeIf(NodeCall($"ShouldWriteNumber_{index}", Cs.Symbol("value"), Cs.Symbol("options"),
            Cs.Symbol("handlingOverride")), NodeCall("w.WriteStringValue", Cs.Symbol("text")),
            Cs.Instruction(InstructionKind.Return)));
        if (IsHalf(type)) body.Add(NodeIf(Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[]
            { NodeCall("Half.IsFinite", Cs.Symbol("value")) } }), NodeThrow(NodeNew("ArgumentException"))));
        body.Add(NodeCall("w.WriteRawValue", Cs.Symbol("text")));
    }

    internal static void Read(List<CsNode> body, ITypeSymbol type, int index)
    {
        var kind = NodeMember(Cs.Symbol("e"), "ValueKind");
        var number = NodeBinary(CsOperator.Equal, kind, NodeMember(Cs.Symbol("JsonValueKind"), "Number"));
        var allowed = NodeCall($"ShouldReadNumber_{index}", Cs.Symbol("e"), Cs.Symbol("options"),
            Cs.Symbol("handlingOverride"));
        body.Add(NodeVar("text", NodeCall("e.GetRawText")));
        body.Add(NodeIf(allowed, NodeAssign(Cs.Symbol("text"), NonNull(NodeCall("e.GetString")))));
        body.Add(NodeIf(Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[]
            { NodeBinary(CsOperator.OrElse, number, allowed) } }), Invalid()));
        var parse = Cs.Symbol("TryParse", new() { Target = TypeRef(TypeName(type)), AsCall = true,
            Arguments = Cs.Compound(Cs.Symbol("text"), NodeMember(Cs.Symbol("CultureInfo"), "InvariantCulture"),
                OutVar("parsed")) });
        var invalid = NodeBinary(CsOperator.OrElse,
            NodeBinary(CsOperator.NotEqual, Cs.Symbol("text"), NodeCall("text.Trim")),
            Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { parse } }));
        body.Add(NodeIf(invalid, Invalid()));
        if (IsHalf(type))
        {
            var finite = NodeCall("Half.IsFinite", Cs.Symbol("parsed"));
            var known = new[] { "NaN", "Infinity", "-Infinity" }.Select(name => (CsNode)NodeBinary(
                CsOperator.Equal, Cs.Symbol("text"), Cs.Value(name))).Aggregate((left, right) =>
                    NodeBinary(CsOperator.OrElse, left, right));
            body.Add(NodeIf(NodeBinary(CsOperator.AndAlso,
                Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { finite } }),
                Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { known } })), Invalid()));
        }
        body.Add(Return(Cs.Symbol("parsed")));
    }

    private static CsNode Invalid() => NodeThrow(NodeCall("InvalidScalar", NodeNew("FormatException"),
        Cs.Symbol("path")));
}

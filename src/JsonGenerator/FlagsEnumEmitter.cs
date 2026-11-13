using System.Numerics;
using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static CodecApiEmitter;

internal static class FlagsEnumEmitter
{
    internal static void Emit(TypeBuilder<FileBuilder> root, int index, string type, IFieldSymbol[] fields)
    {
        var ordered = fields.Select((field, position) => (field, position))
            .Where(item => Bits(item.field.ConstantValue!) != 0)
            .OrderByDescending(item => BitOperations.PopCount(Bits(item.field.ConstantValue!)))
            .ThenBy(item => Bits(item.field.ConstantValue!)).Select(item => Cs.Literal(item.position)).ToArray();
        var array = new CsExpression(ExpressionKind.Array, "", [])
        {
            Type = Cs.Int,
            Initializer = new CsExpression(ExpressionKind.Initializer, "", ordered)
        };
        root.Field(Cs.Int.Array(), $"FlagOrder_{index}").Private().Static().ReadOnly().Initialize(array).End();
        EmitFlagName(root, index);
        EmitFormat(root, index, type);
        EmitParse(root, index, type);
    }

    internal static CsNode[] Writer(int index) =>
    [
        NodeVar("flagsText", NodeCall($"FormatFlags_{index}", Cs.Symbol("value"), Cs.Symbol("names"), Cs.Value(null))),
        NodeIf(NullTest(Cs.Symbol("flagsText"), true),
            NodeCall("w.WriteStringValue", Cs.Symbol("flagsText")), Cs.Instruction(InstructionKind.Return))
    ];

    internal static CsNode[] Reader(int index) =>
    [
        NodeVar("flagsValue", NodeCall($"ParseFlags_{index}", Cs.Symbol("name"), Cs.Symbol("names"))),
        NodeIf(NodeMember(Cs.Symbol("flagsValue"), "HasValue"),
            Return(NodeMember(Cs.Symbol("flagsValue"), "Value")))
    ];

    private static void EmitFormat(TypeBuilder<FileBuilder> root, int index, string type)
    {
        var zero = Cast(type, Cs.Value(0));
        var body = new List<CsNode>
        {
            NodeIf(NodeBinary(CsOperator.Equal, Cs.Symbol("value"), zero), Return(Cs.Literal(null))),
            NodeVar("remaining", Cs.Symbol("value")), NodeVar("text", Cs.Literal(""))
        };
        var flag = Cs.Symbol("flag");
        var contains = NodeBinary(CsOperator.Equal,
            NodeBinary(CsOperator.BitwiseAnd, Cs.Symbol("remaining"), flag), flag);
        var separator = NodeIf(NodeBinary(CsOperator.NotEqual, Cs.Symbol("text"), Cs.Literal("")),
            NodeAssign(Cs.Symbol("text"), NodeBinary(CsOperator.Add, Cs.Symbol("text"), Cs.Literal(", "))));
        var found = NodeIf(contains, separator,
            NodeAssign(Cs.Symbol("text"), NodeBinary(CsOperator.Add, Cs.Symbol("text"),
                NodeCall($"FlagName_{index}", Cs.Symbol("position"), Cs.Symbol("names"), Cs.Symbol("keyPolicy")))),
            NodeAssign(Cs.Symbol("remaining"), NodeBinary(CsOperator.ExclusiveOr, Cs.Symbol("remaining"), flag)));
        body.Add(NodeEach("position", Cs.Symbol($"FlagOrder_{index}"),
            NodeVar("flag", Index(Cs.Symbol($"EnumValues_{index}"), Cs.Symbol("position"))), found));
        body.Add(NodeIf(NodeBinary(CsOperator.Equal, Cs.Symbol("remaining"), zero), Return(Cs.Symbol("text"))));
        body.Add(Return(Cs.Literal(null)));
        root.Method(Cs.Type("string").Nullable(), $"FormatFlags_{index}").Private().Static()
            .Parameter(NodeType(type), "value").Parameter(Cs.Type("string").Array(), "names")
            .Parameter(Cs.Type("JsonNamingPolicy").Nullable(), "keyPolicy")
            .Body(Cs.Tree(body.ToArray())).End();
    }

    private static void EmitFlagName(TypeBuilder<FileBuilder> root, int index)
    {
        var name = Index(Cs.Symbol("names"), Cs.Symbol("position"));
        root.Method(Cs.Type("string"), $"FlagName_{index}").Private().Static()
            .Parameter(Cs.Int, "position").Parameter(Cs.Type("string").Array(), "names")
            .Parameter(Cs.Type("JsonNamingPolicy").Nullable(), "keyPolicy")
            .Body(Cs.Tree(NodeIf(Index(Cs.Symbol($"EnumExact_{index}"), Cs.Symbol("position")), Return(name)),
                Return(NodeCall("ApplyNamingPolicy", Cs.Symbol("keyPolicy"), name)))).End();
    }

    private static void EmitParse(TypeBuilder<FileBuilder> root, int index, string type)
    {
        var equals = EnumEmitter.NameMatches(index, Cs.Symbol("part"));
        var match = NodeIf(equals, NodeAssign(Cs.Symbol("result"), NodeBinary(CsOperator.BitwiseOr, Cs.Symbol("result"),
            Index(Cs.Symbol($"EnumValues_{index}"), Cs.Symbol("i")))),
            NodeAssign(Cs.Symbol("found"), Cs.Value(true)), Cs.Instruction(InstructionKind.Break));
        var loop = Cs.Instruction(InstructionKind.For, new()
        {
            Initializer = NodeVar("i", Cs.Value(0)),
            Condition = NodeBinary(CsOperator.LessThan, Cs.Symbol("i"),
                NodeMember(Cs.Symbol($"EnumNames_{index}"), "Length")),
            Iterators = Cs.Compound(Cs.PostIncrement(Cs.Id("i"))), Body = Cs.Tree(match)
        });
        var parts = NodeCall("name.Split", Cs.Literal(','));
        var each = NodeEach("item", parts, NodeVar("part", NodeCall("item.Trim")),
            NodeVar("found", Cs.Value(false)), loop,
            NodeIf(NodeBinary(CsOperator.Equal, Cs.Symbol("found"), Cs.Value(false)), Return(Cs.Literal(null))));
        root.Method(NodeType(type).Nullable(), $"ParseFlags_{index}").Private().Static()
            .Parameter(Cs.Type("string"), "name").Parameter(Cs.Type("string").Array(), "names")
            .Body(Cs.Tree(NodeVar("result", Cast(type, Cs.Value(0))), each,
                Return(Cs.Symbol("result")))).End();
    }

    internal static ulong Bits(object value) => value switch
    {
        sbyte number => unchecked((byte)number),
        short number => unchecked((ushort)number),
        int number => unchecked((uint)number),
        long number => unchecked((ulong)number),
        _ => Convert.ToUInt64(value)
    };

}

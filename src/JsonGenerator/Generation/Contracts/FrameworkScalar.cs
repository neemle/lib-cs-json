using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static Neemle.Json.Generator.CodecApiEmitter;

namespace Neemle.Json.Generator;

internal static class FrameworkScalar
{
    internal static string? Getter(ITypeSymbol type) => type.SpecialType == SpecialType.System_Char ? "Char" :
        type.ToDisplayString() switch
        {
            "System.Guid" => "GetGuid",
            "System.DateTime" => "GetDateTime",
            "System.DateTimeOffset" => "GetDateTimeOffset",
            "System.DateOnly" => "DateOnly",
            "System.TimeOnly" => "TimeOnly",
            "System.TimeSpan" => "TimeSpan",
            "System.Uri" => "Uri",
            "System.Version" => "Version",
            _ => null
        };

    internal static CsNode WriteValue(ITypeSymbol type)
    {
        string? kind = Getter(type);
        if (kind == "Char") return NodeCall("value.ToString");
        if (kind == "Uri") return NodeMember(Cs.Symbol("value"), "OriginalString");
        if (kind == "Version") return NodeCall("value.ToString");
        CsNode value = kind == "TimeOnly" ? NodeCall("value.ToTimeSpan") : Cs.Symbol("value");
        if (kind is not ("DateOnly" or "TimeOnly" or "TimeSpan")) return value;
        return Cs.Symbol("ToString", new()
        {
            Target = value,
            AsCall = true,
            Arguments = Cs.Compound(Cs.Value(kind == "DateOnly" ? "O" : "c"),
                NodeMember(Cs.Symbol("CultureInfo"), "InvariantCulture"))
        });
    }

    internal static void EmitFrameworkReader(List<CsNode> body, ITypeSymbol type, int index)
    {
        switch (Getter(type))
        {
            case "DateOnly": EmitDateOnlyReader(body); break;
            case "TimeOnly":
            case "TimeSpan":
                body.Add(Return(NodeCall($"ReadTimeValue_{index}", Cs.Symbol("e"), Cs.Symbol("path"))));
                break;
            case "Char":
                body.Add(Return(NodeCall($"ReadCharacter_{index}", Cs.Symbol("e"), Cs.Symbol("path"))));
                break;
            case "Uri":
            case "Version":
                body.Add(Return(NodeCall($"ReadReferenceScalar_{index}", Cs.Symbol("e"), Cs.Symbol("path"))));
                break;
            default: EmitReader(body, Getter(type)!); break;
        }
    }

    internal static void EmitHelper(ITypeSymbol type, int index, TypeBuilder<FileBuilder> root)
    {
        if (CodecEmitter.NumericGetter(type.SpecialType) is not null) EmitNumberHelper(type, index, root);
        string? kind = Getter(type);
        if (kind == "Char")
        {
            EmitCharacterHelper(index, root);
            return;
        }
        if (kind is "Uri" or "Version")
        {
            EmitReferenceHelper(index, kind, root);
            return;
        }
        if (kind is not ("TimeOnly" or "TimeSpan")) return;
        var body = new List<CsNode>();
        EmitTimeReader(body, kind == "TimeOnly");
        root.Method(NodeType(kind), $"ReadTimeValue_{index}").Private().Static()
            .Parameter(Cs.Type("JsonElement"), "e").Parameter(Cs.Type("string"), "path")
            .Body(Cs.Tree(body.ToArray())).End();
    }

    private static void EmitReferenceHelper(int index, string kind, TypeBuilder<FileBuilder> root)
    {
        var arguments = new List<CsNode> { NodeCall("e.GetString") };
        if (kind == "Uri") arguments.Add(NodeMember(Cs.Symbol("UriKind"), "RelativeOrAbsolute"));
        arguments.Add(OutVar("parsed"));
        var parse = NodeCall(kind + ".TryCreate", arguments.ToArray());
        if (kind == "Version") parse = NodeCall("Version.TryParse", arguments.ToArray());
        var failed = Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { parse } });
        root.Method(NodeType(kind), $"ReadReferenceScalar_{index}").Private().Static()
            .Parameter(Cs.Type("JsonElement"), "e").Parameter(Cs.Type("string"), "path")
            .Body(Cs.Tree(RequireKind("String"), NodeIf(failed, Invalid()), Return(NonNull(Cs.Symbol("parsed")))))
            .End();
    }

    private static void EmitNumberHelper(ITypeSymbol type, int index, TypeBuilder<FileBuilder> root)
    {
        var parse = Cs.Symbol("TryParse", new()
        {
            Target = TypeRef("System.Buffers.Text.Utf8Parser"),
            AsCall = true,
            Arguments = Cs.Compound(Cs.Symbol("bytes"), Arg(ArgumentModifier.Out,
                Cs.Symbol("value", new() { Operation = SymbolOperation.Declaration, Type = NodeType(TypeName(type)) })),
                OutVar("consumed"))
        });
        var invalid = NodeBinary(CsOperator.OrElse,
            Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { parse } }),
            NodeBinary(CsOperator.NotEqual, Cs.Symbol("consumed"), NodeMember(Cs.Symbol("bytes"), "Length")));
        root.Method(NodeType(TypeName(type)), $"ParseNumber_{index}").Private().Static()
            .Parameter(Cs.Type("JsonElement"), "e").Parameter(Cs.Type("string"), "path")
            .Body(Cs.Tree(NodeVar("bytes", NodeCall("Encoding.UTF8.GetBytes", NonNull(NodeCall("e.GetString")))),
                NodeIf(invalid, NodeThrow(NodeCall("InvalidScalar", NodeNew("FormatException"), Cs.Symbol("path")))),
                Return(Cs.Symbol("value")))).End();
    }

    private static void EmitCharacterHelper(int index, TypeBuilder<FileBuilder> root)
    {
        var kind = NodeMember(Cs.Symbol("e"), "ValueKind");
        root.Method(Cs.Type("char"), $"ReadCharacter_{index}").Private().Static()
            .Parameter(Cs.Type("JsonElement"), "e").Parameter(Cs.Type("string"), "path")
            .Body(Cs.Tree(
                NodeIf(NodeBinary(CsOperator.NotEqual, kind, NodeMember(Cs.Symbol("JsonValueKind"), "String")),
                    Invalid()),
                NodeVar("text", NonNull(NodeCall("e.GetString"))),
                NodeIf(NodeBinary(CsOperator.NotEqual, NodeMember(Cs.Symbol("text"), "Length"), Cs.Value(1)),
                    Invalid()),
                Return(Index(Cs.Symbol("text"), Cs.Value(0))))).End();
    }

    internal static void EmitReader(List<CsNode> body, string getter, string? typeName = null) =>
        EmitScalarReader(body, Invoke($"e.{getter}"), typeName);

    internal static void EmitStringReader(List<CsNode> body) => EmitScalarReader(body,
        new CsExpression(ExpressionKind.NullForgiving, "", new[] { Invoke("e.GetString") }));

    internal static CsNode RequireKind(string kind) => NodeIf(NodeBinary(CsOperator.NotEqual,
        NodeMember(Cs.Symbol("e"), "ValueKind"), NodeMember(Cs.Symbol("JsonValueKind"), kind)), Invalid());

    private static void EmitDateOnlyReader(List<CsNode> body)
    {
        var value = new CsExpression(ExpressionKind.NullForgiving, "", new[] { Invoke("e.GetString") });
        EmitScalarReader(body, Invoke("DateOnly.ParseExact", value, Cs.Literal("O"),
            Cs.Member(Cs.Id("CultureInfo"), "InvariantCulture")));
    }

    private static void EmitTimeReader(List<CsNode> body, bool timeOnly)
    {
        var kind = NodeMember(Cs.Symbol("e"), "ValueKind");
        body.Add(NodeIf(NodeBinary(CsOperator.NotEqual, kind, NodeMember(Cs.Symbol("JsonValueKind"), "String")),
            Invalid()));
        body.Add(NodeVar("text", NonNull(NodeCall("e.GetString"))));
        body.Add(NodeVar("bytes", NodeCall("Encoding.UTF8.GetBytes", Cs.Symbol("text"))));
        body.Add(NodeIf(NodeBinary(CsOperator.OrElse,
            NodeBinary(CsOperator.Equal, NodeMember(Cs.Symbol("bytes"), "Length"), Cs.Value(0)),
            InvalidFirstByte(timeOnly)), Invalid()));
        if (timeOnly) body.Add(NodeIf(InvalidTimeOnlySeparator(), Invalid()));
        body.Add(NodeIf(InvalidTimeParse(), Invalid()));
        if (timeOnly) body.Add(NodeIf(InvalidTimeOnlyRange(), Invalid()));
        body.Add(Return(timeOnly ? NodeCall("TimeOnly.FromTimeSpan", Cs.Symbol("parsed")) : Cs.Symbol("parsed")));
    }

    private static CsNode Invalid() => NodeThrow(NodeCall("InvalidScalar", NodeNew("FormatException"),
        Cs.Symbol("path")));

    private static CsNode InvalidFirstByte(bool timeOnly)
    {
        var first = Index(Cs.Symbol("bytes"), Cs.Value(0));
        var nonDigit = NodeBinary(CsOperator.OrElse, NodeBinary(CsOperator.LessThan, first, Cs.Value('0')),
            NodeBinary(CsOperator.GreaterThan, Index(Cs.Symbol("bytes"), Cs.Value(0)), Cs.Value('9')));
        return timeOnly ? nonDigit : NodeBinary(CsOperator.AndAlso, nonDigit,
            NodeBinary(CsOperator.NotEqual, Index(Cs.Symbol("bytes"), Cs.Value(0)), Cs.Value('-')));
    }

    private static CsNode InvalidTimeOnlySeparator()
    {
        var colon = NodeCall("text.IndexOf", Cs.Value(':'));
        var dot = NodeCall("text.IndexOf", Cs.Value('.'));
        return NodeBinary(CsOperator.OrElse, NodeBinary(CsOperator.LessThan, colon, Cs.Value(0)),
            NodeBinary(CsOperator.AndAlso, NodeBinary(CsOperator.GreaterThan, dot, Cs.Value(-1)),
                NodeBinary(CsOperator.LessThan, NodeCall("text.IndexOf", Cs.Value('.')),
                    NodeCall("text.IndexOf", Cs.Value(':')))));
    }

    private static CsNode InvalidTimeParse()
    {
        var parse = Cs.Symbol("TryParse", new()
        {
            Target = TypeRef("System.Buffers.Text.Utf8Parser"),
            AsCall = true,
            Arguments = Cs.Compound(Cs.Symbol("bytes"), Arg(ArgumentModifier.Out,
                    Cs.Symbol("parsed", new()
                    { Operation = SymbolOperation.Declaration, Type = NodeType("TimeSpan") })),
                OutVar("consumed"), Cs.Value('c'))
        });
        var failed = Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { parse } });
        return NodeBinary(CsOperator.OrElse, failed, NodeBinary(CsOperator.NotEqual, Cs.Symbol("consumed"),
            NodeMember(Cs.Symbol("bytes"), "Length")));
    }

    private static CsNode InvalidTimeOnlyRange() => NodeBinary(CsOperator.OrElse,
        NodeBinary(CsOperator.LessThan, Cs.Symbol("parsed"), NodeMember(Cs.Symbol("TimeSpan"), "Zero")),
        NodeBinary(CsOperator.GreaterThanOrEqual, Cs.Symbol("parsed"), NodeCall("TimeSpan.FromDays", Cs.Value(1))));

    private static void EmitScalarReader(List<CsNode> body, CsExpression read, string? typeName = null)
    {
        CsExpression[] arguments = typeName is null ? [Cs.Id("error"), Cs.Id("path")] :
            [Cs.Id("error"), Cs.Id("path"), Cs.Literal(typeName)];
        var catches = new[] { "FormatException", "InvalidOperationException", "OverflowException" }
            .Select(type => Cs.Catch(NodeType(type),
                Cs.Body(Cs.Throw(Invoke("InvalidScalar", arguments))), "error"));
        body.Add(Cs.Try(Cs.Body(Cs.Return(read)), new Values<CsCatch>(catches)));
    }
}

using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static CodecApiEmitter;

internal static class NumberHandlingEmitter
{
    internal static CsNode Override(int value) => Cast("JsonNumberHandling", Cs.Value(value));

    internal static CsNode[] Arguments(ISymbol member, int? typeHandling)
    {
        var type = member switch
        { IPropertySymbol property => property.Type, IFieldSymbol field => field.Type, _ => null };
        if (type is null || !CanApply(type)) return [];
        int? value = CodecEmitter.NumberHandling(member) ?? typeHandling;
        return value.HasValue ? [Override(value.Value)] : [];
    }

    internal static bool CanApply(ITypeSymbol type)
    {
        if (CodecEmitter.NumericGetter(type.SpecialType) is not null || WideNumberEmitter.IsWide(type)) return true;
        if (type is IArrayTypeSymbol array)
            return array.ElementType.SpecialType != SpecialType.System_Byte && CanApply(array.ElementType);
        if (type is not INamedTypeSymbol named) return false;
        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return CanApply(named.TypeArguments[0]);
        if (CodecEmitter.IsMemory(named))
            return named.TypeArguments[0].SpecialType != SpecialType.System_Byte && CanApply(named.TypeArguments[0]);
        if (FrameworkCollection.IsMap(named)) return CanApply(FrameworkCollection.MapValueType(named));
        var element = FrameworkCollection.GenericElement(named) ?? named.TypeArguments.FirstOrDefault();
        return element is not null && (FrameworkCollection.AddMethod(named) is not null ||
            FrameworkCollection.ImmutableSequenceFactory(named) is not null) && CanApply(element);
    }

    internal static CsNode[] Forward(ITypeSymbol type, params CsNode[] arguments) => CanApply(type) ?
        [.. arguments, Cs.Symbol("handlingOverride")] : arguments;

    internal static void EmitReader(List<CsNode> body, int index)
    {
        body.Add(NodeIf(NodeCall($"ShouldReadNumber_{index}", Cs.Symbol("e"), Cs.Symbol("options"),
                Cs.Symbol("handlingOverride")),
            Return(NodeCall($"ParseNumber_{index}", Cs.Symbol("e"), Cs.Symbol("path")))));
    }

    internal static void EmitWriter(List<CsNode> body, int index)
    {
        var text = Cs.Symbol("ToString", new()
        {
            Target = Cs.Symbol("value"), AsCall = true,
            Arguments = Cs.Compound(Cs.Value(null), NodeMember(Cs.Symbol("CultureInfo"), "InvariantCulture"))
        });
        body.Add(NodeIf(NodeCall($"ShouldWriteNumber_{index}", Cs.Symbol("value"), Cs.Symbol("options"),
            Cs.Symbol("handlingOverride")), NodeCall("w.WriteStringValue", text),
            Cs.Instruction(InstructionKind.Return)));
        body.Add(NodeCall("w.WriteNumberValue", Cs.Symbol("value")));
    }

    internal static void EmitHelpers(TypeBuilder<FileBuilder> root, ITypeSymbol type, int index)
    {
        bool floating = type.SpecialType is SpecialType.System_Single or SpecialType.System_Double ||
            WideNumberEmitter.IsHalf(type);
        var read = Flag("AllowReadingFromString");
        var write = Flag("WriteAsString");
        if (floating)
        {
            read = NodeBinary(CsOperator.OrElse, read,
                NodeBinary(CsOperator.AndAlso, Flag("AllowNamedFloatingPointLiterals"), NamedLiteral()));
            var finite = Cs.Symbol("IsFinite", new()
            { Target = TypeRef(TypeName(type)), AsCall = true, Arguments = Cs.Compound(Cs.Symbol("value")) });
            var notFinite = Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { finite } });
            write = NodeBinary(CsOperator.OrElse, write,
                NodeBinary(CsOperator.AndAlso, Flag("AllowNamedFloatingPointLiterals"), notFinite));
        }
        var isString = NodeBinary(CsOperator.Equal, NodeMember(Cs.Symbol("e"), "ValueKind"),
            NodeMember(Cs.Symbol("JsonValueKind"), "String"));
        root.Method(Cs.Bool, $"ShouldReadNumber_{index}").Private().Static()
            .Parameter(Cs.Type("JsonElement"), "e").Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Parameter(Cs.Type("JsonNumberHandling").Nullable(), "handlingOverride")
            .Body(Cs.Tree(Return(NodeBinary(CsOperator.AndAlso, isString, read)))).End();
        root.Method(Cs.Bool, $"ShouldWriteNumber_{index}").Private().Static()
            .Parameter(NodeType(TypeName(type)), "value")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Parameter(Cs.Type("JsonNumberHandling").Nullable(), "handlingOverride")
            .Body(Cs.Tree(Return(write))).End();
    }

    private static CsNode Flag(string name)
    {
        var flag = NodeMember(Cs.Symbol("JsonNumberHandling"), name);
        var option = Cs.Symbol("NumberHandling", new() { Target = Cs.Symbol("options"), ConditionalAccess = true });
        var effective = NodeBinary(CsOperator.Coalesce, Cs.Symbol("handlingOverride"),
            NodeBinary(CsOperator.Coalesce, option, NodeMember(Cs.Symbol("JsonNumberHandling"), "Strict")));
        return NodeBinary(CsOperator.Equal, NodeBinary(CsOperator.BitwiseAnd, effective, flag), flag);
    }

    private static CsNode NamedLiteral()
    {
        var text = NodeCall("e.GetString");
        return new[] { "NaN", "Infinity", "-Infinity" }.Select(name => (CsNode)NodeBinary(CsOperator.Equal,
            text, Cs.Value(name))).Aggregate((left, right) => NodeBinary(CsOperator.OrElse, left, right));
    }
}

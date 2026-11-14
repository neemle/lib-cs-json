using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static Neemle.Json.Generator.CodecApiEmitter;

namespace Neemle.Json.Generator;

internal static class DuplicatePropertyEmitter
{
    internal static CsNode Disabled() => NodeBinary(CsOperator.Equal, Cs.Symbol("AllowDuplicateProperties", new()
    { Target = Cs.Symbol("options"), ConditionalAccess = true }), Cs.Value(false));

    internal static CsNode Check(CsNode present) => NodeCall("CheckDuplicate", present, Cs.Symbol("options"));

    internal static void Emit(TypeBuilder<FileBuilder> root) =>
        root.Method(Cs.Void, "CheckDuplicate").Private().Static().Parameter(Cs.Bool, "present")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Body(Cs.Tree(NodeIf(NodeBinary(CsOperator.AndAlso, Disabled(), Cs.Symbol("present")),
                NodeThrow(NodeNew("JsonException"))))).End();

    internal static CsNode[] Track(int slot)
    {
        var state = Index(Cs.Symbol("state"), Cs.Value(slot));
        return [Check(NullTest(state, true)), NodeAssign(state, Cs.Value(true))];
    }
}

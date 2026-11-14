using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static Neemle.Json.Generator.CodecApiEmitter;

namespace Neemle.Json.Generator;

internal static class DynamicEmitter
{
    private const int BatchSize = 3;

    internal static void Emit(TypeBuilder<FileBuilder> root, IReadOnlyList<ITypeSymbol> types)
    {
        var candidates = types.Select((type, index) => (type, index)).Where(item => IsCandidate(item.type))
            .OrderByDescending(item => Depth(item.type)).ToArray();
        var methods = new List<string>();
        for (int offset = 0; offset < candidates.Length; offset += BatchSize)
        {
            string method = "TryDynamic_0_" + methods.Count;
            methods.Add(method);
            var body = new List<CsNode>();
            foreach (var (type, index) in candidates.Skip(offset).Take(BatchSize))
            {
                string local = "typed_" + index;
                var test = Cs.Symbol(new()
                {
                    AsIs = true,
                    Value = Cs.Symbol("value"),
                    Pattern = new CsPattern(PatternKind.Declaration) { Type = NodeType(TypeName(type)), Name = local }
                });
                CsNode matches = test;
                if (type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Class &&
                    type.ToDisplayString() != "System.Text.Json.Nodes.JsonNode")
                    matches = NodeBinary(CsOperator.AndAlso, test, NodeBinary(CsOperator.Equal,
                        NodeCall(local + ".GetType"), TypeOf(TypeName(type))));
                body.Add(NodeIf(matches, NodeCall($"Write_{index}", Cs.Symbol("w"), Cs.Symbol(local),
                    Cs.Symbol("options"), Cs.Symbol("path")), Return(Cs.Value(true))));
            }
            body.Add(Return(Cs.Value(false)));
            Writer(root, method).Body(Cs.Tree(body.ToArray())).End();
        }
        int level = 1;
        while (methods.Count > 1) methods = DispatchLevel(root, methods, level++);
        EmitWriter(root, methods.SingleOrDefault());
        EmitReader(root);
    }

    private static List<string> DispatchLevel(TypeBuilder<FileBuilder> root, List<string> methods, int level)
    {
        var next = new List<string>();
        for (int offset = 0; offset < methods.Count; offset += BatchSize)
        {
            string method = $"TryDynamic_{level}_{next.Count}";
            next.Add(method);
            var body = methods.Skip(offset).Take(BatchSize).Select(name => (CsNode)NodeIf(
                NodeCall(name, Cs.Symbol("w"), Cs.Symbol("value"), Cs.Symbol("options"), Cs.Symbol("path")),
                Return(Cs.Value(true)))).ToList();
            body.Add(Return(Cs.Value(false)));
            Writer(root, method).Body(Cs.Tree(body.ToArray())).End();
        }
        return next;
    }

    private static MethodBuilder<TypeBuilder<FileBuilder>> Writer(TypeBuilder<FileBuilder> root, string name) =>
        root.Method(Cs.Bool, name).Private().Static().Parameter(Cs.Type("Utf8JsonWriter"), "w")
            .Parameter(Cs.Type("object"), "value").Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Parameter(Cs.Type("string"), "path");

    private static void EmitWriter(TypeBuilder<FileBuilder> root, string? dispatch)
    {
        var body = new List<CsNode>();
        if (dispatch is not null) body.Add(NodeIf(NodeCall(dispatch, Cs.Symbol("w"), Cs.Symbol("value"),
            Cs.Symbol("options"), Cs.Symbol("path")), Cs.Instruction(InstructionKind.Return)));
        body.Add(NodeIf(NodeBinary(CsOperator.Equal, NodeCall("value.GetType"), TypeOf("object")),
            NodeCall("w.WriteStartObject"), NodeCall("w.WriteEndObject"), Cs.Instruction(InstructionKind.Return)));
        body.Add(NodeThrow(NodeNew("NotSupportedException", Cs.Value("The runtime JSON type was not generated."))));
        root.Method(Cs.Void, "WriteDynamic").Private().Static().Parameter(Cs.Type("Utf8JsonWriter"), "w")
            .Parameter(Cs.Type("object"), "value").Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Parameter(Cs.Type("string"), "path").Body(Cs.Tree(body.ToArray())).End();
    }

    private static void EmitReader(TypeBuilder<FileBuilder> root)
    {
        var setting = Cs.Symbol("UnknownTypeHandling", new() { Target = Cs.Symbol("options"), ConditionalAccess = true });
        var node = NodeBinary(CsOperator.Equal, setting, NodeMember(Cs.Symbol("JsonUnknownTypeHandling"), "JsonNode"));
        root.Method(Cs.Type("object"), "ReadDynamic").Private().Static().Parameter(Cs.Type("JsonElement"), "e")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Body(Cs.Tree(NodeIf(node, Return(NonNull(NodeCall("JsonNode.Parse", NodeCall("e.GetRawText"))))),
                Return(NodeCall("e.Clone")))).End();
    }

    private static bool IsCandidate(ITypeSymbol type) => type.SpecialType != SpecialType.System_Object &&
        type is not INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } &&
        !type.IsRefLikeType;

    private static int Depth(ITypeSymbol type)
    {
        if (type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Interface) return -1;
        int depth = 0;
        for (var current = type.BaseType; current is not null; current = current.BaseType) depth++;
        return depth;
    }
}

using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static CodecApiEmitter;

internal static class EnumFactoryEmitter
{
    internal static void EmitPublic(TypeBuilder<FileBuilder> root)
    {
        root.Method(Cs.Void, "RegisterAllEnums").Internal().Static()
            .Parameter(Cs.Type("JsonSerializerOptions"), "options")
            .Parameter(Cs.Type("JsonNamingPolicy").Nullable(), "namingPolicy", @default: Cs.Literal(null))
            .Parameter(Cs.Bool, "allowIntegerValues", @default: Cs.Literal(true))
            .Body(Cs.Tree(NodeCall("RegisterEnumFactory", Cs.Symbol("options"), Cs.Symbol("namingPolicy"),
                Cs.Symbol("allowIntegerValues")))).End();
        root.Method(Cs.Type("JsonSerializerOptions"), "ConfigureAllEnums").Internal().Static()
            .Parameter(Cs.Type("JsonSerializerOptions"), "options")
            .Body(Cs.Tree(NodeCall("RegisterEnumFactory", Cs.Symbol("options"), Cs.Value(null), Cs.Value(true)),
                Return(Cs.Symbol("options")))).End();
    }

    internal static void Emit(TypeBuilder<FileBuilder> root, IReadOnlyList<ITypeSymbol> types)
    {
        var indices = types.Select((type, index) => (type, index)).Where(item => item.type.TypeKind ==
            Microsoft.CodeAnalysis.TypeKind.Enum && ConverterAttributeEmitter.Converter(item.type, item.type) is null)
            .Select(item => item.index).ToArray();
        var methods = new List<string>();
        foreach (int[] group in indices.Chunk(3))
        {
            string name = "RegisterEnumFactory_0_" + methods.Count;
            methods.Add(name);
            Batch(root, name).Body(Cs.Tree(group.Select(index => (CsNode)NodeCall($"RegisterEnum_{index}",
                Cs.Symbol("options"), Cs.Symbol("namingPolicy"), Cs.Symbol("allowIntegerValues"))).ToArray())).End();
        }
        int level = 1;
        while (methods.Count > 3) methods = Collapse(root, methods, level++);
        Batch(root, "RegisterEnumFactory").Body(Cs.Tree(methods.Select(name => (CsNode)NodeCall(name,
            Cs.Symbol("options"), Cs.Symbol("namingPolicy"), Cs.Symbol("allowIntegerValues"))).ToArray())).End();
    }

    private static List<string> Collapse(TypeBuilder<FileBuilder> root, List<string> methods, int level)
    {
        var next = new List<string>();
        foreach (string[] group in methods.Chunk(3))
        {
            string name = $"RegisterEnumFactory_{level}_{next.Count}";
            next.Add(name);
            Batch(root, name).Body(Cs.Tree(group.Select(item => (CsNode)NodeCall(item, Cs.Symbol("options"),
                Cs.Symbol("namingPolicy"), Cs.Symbol("allowIntegerValues"))).ToArray())).End();
        }
        return next;
    }

    private static MethodBuilder<TypeBuilder<FileBuilder>> Batch(TypeBuilder<FileBuilder> root, string name) =>
        root.Method(Cs.Void, name).Private().Static().Parameter(Cs.Type("JsonSerializerOptions"), "options")
            .Parameter(Cs.Type("JsonNamingPolicy").Nullable(), "namingPolicy").Parameter(Cs.Bool, "allowIntegerValues");
}

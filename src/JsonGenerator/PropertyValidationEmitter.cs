using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static CodecApiEmitter;

internal static class PropertyValidationEmitter
{
    private const int BatchSize = 8;
    private static CsType NamesType => Cs.Generic("System.Collections.Generic.HashSet", Cs.Type("string"));

    internal static void Emit(TypeBuilder<FileBuilder> root, IPropertySymbol[] properties, IFieldSymbol[] fields, int index)
    {
        var methods = new List<string>();
        foreach (var member in properties.Cast<ISymbol>().Concat(fields))
        {
            string name = $"ValidatePropertyName_{index}_{methods.Count}";
            methods.Add(name);
            CsNode add = NodeCall("names.Add", CodecEmitter.JsonNameExpression(member));
            CsNode check = NodeIf(Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { add } }),
                NodeThrow(NodeNew("InvalidOperationException")));
            if (member is IFieldSymbol field && !CodecEmitter.HasJsonInclude(field))
                check = NodeIf(NodeBinary(CsOperator.Equal, Cs.Symbol("IncludeFields", new()
                { Target = Cs.Symbol("options"), ConditionalAccess = true }), Cs.Value(true)), check);
            Batch(root, name).Body(Cs.Tree(check)).End();
        }
        string dispatch = "ValidatePropertyNames_" + index;
        EmitBatches(root, dispatch, methods);
        var insensitive = NodeBinary(CsOperator.Equal, Cs.Symbol("PropertyNameCaseInsensitive", new()
        { Target = Cs.Symbol("options"), ConditionalAccess = true }), Cs.Value(true));
        var comparer = Cs.Symbol(new()
        {
            AsConditional = true,
            Parts = new CsNode[] { insensitive,
            NodeMember(Cs.Symbol("StringComparer"), "OrdinalIgnoreCase"),
            NodeMember(Cs.Symbol("StringComparer"), "Ordinal") }
        });
        root.Method(Cs.Void, "ValidateNames_" + index).Private().Static()
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Body(Cs.Tree(NodeVar("names", NodeNew("System.Collections.Generic.HashSet<string>", comparer)),
                NodeCall(dispatch, Cs.Symbol("names"), Cs.Symbol("options")))).End();
    }

    private static void EmitBatches(TypeBuilder<FileBuilder> root, string name, List<string> methods)
    {
        int level = 0;
        while (methods.Count > BatchSize)
        {
            var next = new List<string>();
            foreach (var group in methods.Chunk(BatchSize))
            {
                string part = $"{name}_{level}_{next.Count}";
                next.Add(part);
                EmitBatch(root, part, group);
            }
            methods = next;
            level++;
        }
        EmitBatch(root, name, methods);
    }

    private static void EmitBatch(TypeBuilder<FileBuilder> root, string name, IEnumerable<string> methods) =>
        Batch(root, name).Body(Cs.Tree(methods.Select(method => NodeCall(method, Cs.Symbol("names"),
            Cs.Symbol("options"))).ToArray())).End();

    private static MethodBuilder<TypeBuilder<FileBuilder>> Batch(TypeBuilder<FileBuilder> root, string name) =>
        root.Method(Cs.Void, name).Private().Static().Parameter(NamesType, "names")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options");
}

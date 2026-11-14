using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static CodecApiEmitter;

internal static class ConverterFactoryEmitter
{
    internal static void EmitBase(TypeBuilder<FileBuilder> root)
    {
        var factory = root.Class("ConverterFactory").Public().Abstract();
        factory.Method(Cs.Bool, "CanConvert").Public().Abstract()
            .Parameter(Cs.Type("Type"), "typeToConvert").End();
        factory.Method(Cs.Type("object"), "CreateConverter").Public().Abstract()
            .Parameter(Cs.Type("Type"), "typeToConvert")
            .Parameter(Cs.Type("JsonSerializerOptions"), "options").End();
        factory.End();
        var attribute = root.Class("ConverterAttribute").Public().Abstract().Base(Cs.Type("Attribute"));
        attribute.Method(Cs.Type("object"), "CreateConverter").Public().Abstract()
            .Parameter(Cs.Type("Type"), "typeToConvert").End();
        attribute.End();
        foreach (string exception in new[] { "JsonException", "NotSupportedException" })
        {
            var wrapper = root.Class("User" + exception).Public().Sealed().Base(Cs.Type(exception));
            wrapper.Field(Cs.Type("string").Nullable(), "Text").Public().ReadOnly().End();
            foreach (bool hasInner in new[] { false, true })
            {
                var ctor = wrapper.Constructor().Public().Parameter(Cs.Type("string").Nullable(), "text");
                if (hasInner) ctor.Parameter(Cs.Type("Exception").Nullable(), "inner");
                ctor.Base(hasInner ? [Cs.Id("text"), Cs.Id("inner")] : [Cs.Id("text")])
                    .Body(Cs.Statement(Cs.Assign(Cs.Id("Text"), Cs.Id("text")))).End();
            }
            wrapper.End();
        }
    }

    internal static void EmitCollection(TypeBuilder<FileBuilder> root)
    {
        var collection = root.Class("ConverterCollection").Internal().Sealed()
            .Base(Cs.Generic("System.Collections.ObjectModel.Collection", Cs.Type("object")));
        collection.Field(Cs.Bool, "locked").Private().End();
        collection.Property(Cs.Bool, "IsLocked").Internal().Expression(Cs.Id("locked")).End();
        collection.Method(Cs.Void, "Lock").Internal().Body(Cs.Tree(NodeAssign(Cs.Symbol("locked"), Cs.Value(true)))).End();
        collection.Method(Cs.Void, "EnsureMutable").Internal().Body(Cs.Tree(
            NodeIf(Cs.Symbol("locked"), NodeThrow(NodeNew("InvalidOperationException"))))).End();
        foreach (string method in new[] { "InsertItem", "SetItem", "RemoveItem", "ClearItems" })
        {
            var builder = collection.Method(Cs.Void, method).Protected().Override();
            if (method != "ClearItems") builder.Parameter(Cs.Type("int"), "index");
            if (method is "InsertItem" or "SetItem") builder.Parameter(Cs.Type("object"), "item");
            CsExpression[] args = method switch
            {
                "InsertItem" or "SetItem" => [Cs.Id("index"), Cs.Id("item")],
                "RemoveItem" => [Cs.Id("index")],
                _ => []
            };
            var baseTarget = new CsExpression(ExpressionKind.Base, "", []);
            builder.Body(Cs.Tree(NodeCall("EnsureMutable"),
                Cs.Statement(Cs.Call(Cs.Member(baseTarget, method), args)))).End();
        }
        collection.End();
        var table = Cs.Generic("ConditionalWeakTable", Cs.Type("JsonSerializerOptions"), Cs.Type("ConverterCollection"));
        root.Field(table, "ConverterCollections").Private().Static().ReadOnly()
            .Initialize(new CsExpression(ExpressionKind.New, "", [])).End();
        var create = Cs.Lambda(new CsExpression(ExpressionKind.New, "", []) { Type = Cs.Type("ConverterCollection") },
            Cs.LambdaParameter("_")) with
        { IsStatic = true };
        root.Method(Cs.Type("ConverterCollection"), "Converters").Internal().Static()
            .Parameter(Cs.Type("JsonSerializerOptions"), "options")
            .Body(Cs.Tree(Return(NodeCall("ConverterCollections.GetValue", Cs.Symbol("options"), create)))).End();
        root.Method(Cs.Type("JsonSerializerOptions"), "WithConverters").Internal().Static()
            .Parameter(Cs.Type("JsonSerializerOptions"), "options")
            .Parameter(Cs.Type("object").Array(), "items", "params")
            .Body(Cs.Tree(NodeEach("item", Cs.Symbol("items"),
                    Cs.Symbol("Add", new()
                    {
                        Target = NodeCall("Converters", Cs.Symbol("options")),
                        AsCall = true,
                        Arguments = Cs.Compound(Cs.Symbol("item"))
                    })),
                Return(Cs.Symbol("options")))).End();
        root.Method(Cs.Bool, "OptionsAreReadOnly").Internal().Static()
            .Parameter(Cs.Type("JsonSerializerOptions"), "options")
            .Body(Cs.Tree(Return(NodeBinary(CsOperator.OrElse, NodeMember(Cs.Symbol("options"), "IsReadOnly"),
                NodeMember(NodeCall("Converters", Cs.Symbol("options")), "IsLocked"))))).End();
        root.Method(Cs.Type("JsonSerializerOptions"), "CopyOptions").Internal().Static()
            .Parameter(Cs.Type("JsonSerializerOptions"), "source").Parameter(Cs.Type("JsonSerializerOptions"), "copy")
            .Body(Cs.Tree(NodeEach("item", NodeCall("Converters", Cs.Symbol("source")),
                Cs.Symbol("Add", new()
                {
                    Target = NodeCall("Converters", Cs.Symbol("copy")),
                    AsCall = true,
                    Arguments = Cs.Compound(Cs.Symbol("item"))
                })), Return(Cs.Symbol("copy")))).End();
    }

    internal static void EmitLookup(TypeBuilder<FileBuilder> root, int index, string name, CsType converterType,
        bool supportsEnum)
    {
        var typed = Cs.Symbol(new()
        {
            AsIs = true,
            Value = Cs.Symbol("item"),
            Pattern = new CsPattern(PatternKind.Declaration) { Type = converterType, Name = "typed" }
        });
        var factory = Cs.Symbol(new()
        {
            AsIs = true,
            Value = Cs.Symbol("item"),
            Pattern = new CsPattern(PatternKind.Declaration) { Type = Cs.Type("ConverterFactory"), Name = "factory" }
        });
        var compatible = NodeBinary(CsOperator.AndAlso, typed, NodeCall("typed.CanConvert", TypeOf(name)));
        var factoryCompatible = NodeBinary(CsOperator.AndAlso, factory, NodeCall("factory.CanConvert", TypeOf(name)));
        var registered = NodeBinary(CsOperator.AndAlso,
            NodeCall($"Table_{index}.TryGetValue", Cs.Symbol("options"), OutVar("holder")),
            NodeCall($"ResolveFactory_{index}", Cs.Symbol("holder"), Cs.Symbol("options")));
        var hasConverter = NullTest(NodeMember(Cs.Symbol("holder"), "Converter"), true);
        var items = new List<CsNode>();
        if (supportsEnum)
        {
            var registeredEnum = NodeBinary(CsOperator.AndAlso,
                NodeCall($"Table_{index}.TryGetValue", Cs.Symbol("options"), OutVar("enumEntry")),
                NodeBinary(CsOperator.AndAlso, NodeMember(Cs.Symbol("enumEntry"), "StringEnum"),
                    NodeBinary(CsOperator.GreaterThanOrEqual, Cs.Symbol("position"),
                        NodeMember(Cs.Symbol("enumEntry"), "EnumPosition"))));
            items.Add(NodeIf(registeredEnum, Cs.Instruction(InstructionKind.Break)));
            items.Add(Cs.Statement(Cs.PostIncrement(Cs.Id("position"))));
        }
        items.Add(NodeIf(compatible, NodeAssign(Cs.Symbol("converter"), Cs.Symbol("typed")), Return(Cs.Value(true))));
        items.Add(NodeIf(factoryCompatible, NodeCall($"RegisterFactory_{index}", Cs.Symbol("options"),
            Cs.Symbol("factory")), Cs.Instruction(InstructionKind.Break)));
        var body = new List<CsNode>();
        if (supportsEnum) body.Add(NodeVar("position", Cs.Value(0)));
        body.Add(NodeEach("item", NodeCall("Converters", Cs.Symbol("options")), items.ToArray()));
        body.Add(NodeAssign(Cs.Symbol("converter"), NonNull(DefaultValue())));
        body.Add(Return(Cs.Value(false)));
        root.Method(Cs.Bool, $"FindInConverterCollection_{index}").Private().Static()
            .Parameter(Cs.Type("JsonSerializerOptions"), "options").Parameter(converterType, "converter", "out")
            .Body(Cs.Tree(body.ToArray())).End();
        body = [NodeIf(NodeCall($"FindInConverterCollection_{index}", Cs.Symbol("options"),
            Arg(ArgumentModifier.Out, Cs.Symbol("converter"))), Return(Cs.Value(true)))];
        body.Add(NodeIf(NodeBinary(CsOperator.AndAlso, registered, hasConverter),
            NodeAssign(Cs.Symbol("converter"), NonNull(NodeMember(Cs.Symbol("holder"), "Converter"))),
            Return(Cs.Value(true))));
        body.Add(NodeAssign(Cs.Symbol("converter"), NonNull(DefaultValue())));
        body.Add(Return(Cs.Value(false)));
        root.Method(Cs.Bool, $"TryFindConverter_{index}").Private().Static()
            .Parameter(Cs.Type("JsonSerializerOptions"), "options").Parameter(converterType, "converter", "out")
            .Body(Cs.Tree(body.ToArray())).End();
    }

    internal static void EmitRegistration(TypeBuilder<FileBuilder> root, int index, string name, CsType converterType)
    {
        var createHolder = Cs.Lambda(new CsExpression(ExpressionKind.New, "", [])
        { Type = Cs.Type($"Holder_{index}") }, Cs.LambdaParameter("_")) with
        { IsStatic = true };
        var notConvertible = Cs.Symbol(new()
        {
            Operator = CsOperator.Not,
            Parts = new CsNode[] { NodeCall("factory.CanConvert", TypeOf(name)) }
        });
        var notRegistered = Cs.Symbol(new()
        {
            Operator = CsOperator.Not,
            Parts = new CsNode[] { NodeMember(Cs.Symbol("holder"), "Registered") }
        });
        root.Method(Cs.Void, $"RegisterFactory_{index}").Internal().Static()
            .Parameter(Cs.Type("JsonSerializerOptions"), "options").Parameter(Cs.Type("ConverterFactory"), "factory")
            .Body(Cs.Tree(NodeIf(notConvertible, Cs.Instruction(InstructionKind.Return)),
                NodeVar("holder", NodeCall($"Table_{index}.GetValue", Cs.Symbol("options"), createHolder)),
                NodeIf(notRegistered, NodeAssign(NodeMember(Cs.Symbol("holder"), "Factory"), Cs.Symbol("factory")),
                    NodeAssign(NodeMember(Cs.Symbol("holder"), "Registered"), Cs.Value(true))))).End();
        EmitResolve(root, index, name, converterType);
    }

    private static void EmitResolve(TypeBuilder<FileBuilder> root, int index, string name, CsType converterType)
    {
        var factory = NodeMember(Cs.Symbol("holder"), "Factory");
        var current = NodeMember(Cs.Symbol("holder"), "Converter");
        var condition = NodeBinary(CsOperator.AndAlso, NullTest(factory, true), NullTest(current));
        var isConverter = Cs.Symbol(new()
        {
            AsIs = true,
            Value = Cs.Symbol("created"),
            Pattern = new CsPattern(PatternKind.Declaration) { Type = converterType, Name = "converter" }
        });
        root.Method(Cs.Bool, $"ResolveFactory_{index}").Private().Static()
            .Parameter(Cs.Type($"Holder_{index}"), "holder").Parameter(Cs.Type("JsonSerializerOptions"), "options")
            .Body(Cs.Tree(NodeIf(condition,
                NodeVar("created", NodeCall("holder.Factory.CreateConverter", TypeOf(name), Cs.Symbol("options"))),
                NodeIf(isConverter, NodeAssign(current, Cs.Symbol("converter")), Return(Cs.Value(true))),
                NodeThrow(NodeNew("InvalidOperationException"))), Return(Cs.Value(true)))).End();
    }

    internal static void EmitDispatch(TypeBuilder<FileBuilder> root, IReadOnlyList<ITypeSymbol> types)
    {
        const int batchSize = 10;
        int batches = (types.Count + batchSize - 1) / batchSize;
        for (int batch = 0; batch < batches; batch++)
        {
            var calls = Enumerable.Range(batch * batchSize, Math.Min(batchSize, types.Count - batch * batchSize))
                .Select(index => (CsNode)NodeCall($"RegisterFactory_{index}", Cs.Symbol("options"),
                    Cs.Symbol("factory"))).ToArray();
            root.Method(Cs.Void, $"RegisterFactoryBatch_{batch}").Private().Static()
                .Parameter(Cs.Type("JsonSerializerOptions"), "options")
                .Parameter(Cs.Type("ConverterFactory"), "factory").Body(Cs.Tree(calls)).End();
        }
        var methods = Enumerable.Range(0, batches).Select(batch => $"RegisterFactoryBatch_{batch}").ToList();
        int level = 0;
        while (methods.Count > batchSize)
        {
            var next = new List<string>();
            foreach (string[] group in methods.Chunk(batchSize))
            {
                string name = $"RegisterFactoryLevel_{level}_{next.Count}";
                next.Add(name);
                var calls = group.Select(method => (CsNode)NodeCall(method, Cs.Symbol("options"),
                    Cs.Symbol("factory"))).ToArray();
                root.Method(Cs.Void, name).Private().Static()
                    .Parameter(Cs.Type("JsonSerializerOptions"), "options")
                    .Parameter(Cs.Type("ConverterFactory"), "factory").Body(Cs.Tree(calls)).End();
            }
            methods = next;
            level++;
        }
        var dispatch = methods.Select(method => (CsNode)NodeCall(method, Cs.Symbol("options"),
            Cs.Symbol("factory"))).ToArray();
        root.Method(Cs.Void, "RegisterFactory").Internal().Static()
            .Parameter(Cs.Type("JsonSerializerOptions"), "options").Parameter(Cs.Type("ConverterFactory"), "factory")
            .Body(Cs.Tree(dispatch)).End();
    }
}

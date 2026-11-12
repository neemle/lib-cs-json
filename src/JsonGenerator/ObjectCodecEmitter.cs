using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static CodecApiEmitter;
using static CodecEmitter;

sealed class ObjectCodecEmitter(CodecEmitter owner)
{
    private const int CallsPerBatch = 8;

    internal void EmitObjectWriter(List<CsNode> body, INamedTypeSymbol type, int index)
    {
        body.Add(NodeCall("w.WriteStartObject"));
        body.Add(NodeCall($"WriteMembers_{index}", Cs.Symbol("w"), Cs.Symbol("value"),
            Cs.Symbol("options"), Cs.Symbol("path")));
        var extension = ExtensionProperty(type);
        if (extension is not null) body.Add(NodeCall($"WriteExtra_{index}", Cs.Symbol("w"), Cs.Symbol("value")));
        body.Add(NodeCall("w.WriteEndObject"));
    }

    internal void EmitObjectReader(List<CsNode> body, INamedTypeSymbol type, int index)
    {
        var properties = Properties(type);
        string name = TypeName(type);
        bool parameterless = UsesParameterless(type, properties);
        body.Add(NodeCall("RequireObject", Cs.Symbol("e")));
        PopulateEmitter.EmitValidation(body, properties);
        body.Add(Cs.Symbol("result", new() { Type = NodeType(name),
            Value = parameterless ? NodeNew(name) : NonNull(DefaultValue()) }));
        body.Add(Cs.Symbol("state", new() { Type = Cs.Type("object").Nullable().Array(), Value = Cs.Symbol(new()
            { AsArray = true, Type = Cs.Type("object").Nullable(),
                Arguments = Cs.Compound(Cs.Value(properties.Length * 2)) }) }));
        string read = parameterless ? "ReadEntry_" : "ReadMembers_";
        body.Add(ReadEntries(read + index));
        body.Add(NodeCall($"ValidateMembers_{index}", Cs.Symbol("state")));
        if (!parameterless)
        {
            body.Add(Cs.Symbol("result", new() { AsAssignment = true, Value = Construction(properties, name) }));
            body.Add(ReadEntries("ReadRemaining_" + index));
        }
        body.Add(Return(Cs.Symbol("result")));
    }

    private static CsInstruction ReadEntries(string method) => NodeEach("entry", NodeCall("e.EnumerateObject"),
        NodeCall(method, Cs.Symbol("entry"), Arg(ArgumentModifier.Ref, Cs.Symbol("result")), Cs.Symbol("state"),
            Cs.Symbol("options"), Cs.Symbol("path")));

    private static bool UsesParameterless(INamedTypeSymbol type, IPropertySymbol[] properties) =>
        !type.InstanceConstructors.Any(c => IsJsonConstructor(c) && c.Parameters.Length > 0) &&
        type.InstanceConstructors.Any(c => c.DeclaredAccessibility == Accessibility.Public &&
            c.Parameters.Length == 0) &&
        properties.All(p => p.SetMethod is { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false } ||
            PopulateEmitter.CanHandleReadonly(p));

    private static CsSymbol Construction(IPropertySymbol[] properties, string name) => NodeNew(name,
        properties.Select((property, index) => Cs.Symbol(new() { AsCall = true,
            Target = Cs.Symbol("GetSlot", new() { TypeArguments = new[] { NodeType(TypeName(property.Type)) } }),
            Arguments = Cs.Compound(Cs.Symbol("state"), Cs.Value(index)) })).ToArray());

    internal void EmitObjectHelpers(TypeBuilder<FileBuilder> root, INamedTypeSymbol type, int index)
    {
        var properties = Properties(type);
        var fields = Fields(type);
        bool parameterless = UsesParameterless(type, properties);
        string typeName = TypeName(type);
        var writers = EmitWriters(root, properties, fields, typeName, index);
        var readers = EmitReaders(root, properties, typeName, index, parameterless);
        var fieldReaders = EmitFieldReaders(root, fields, typeName, index);
        var validators = EmitValidators(root, properties, index);
        EmitDispatch(root, "WriteMembers_" + index, typeName, writers, "write");
        EmitDispatch(root, "ReadMembers_" + index, typeName, readers, "read");
        EmitDispatch(root, "ReadFields_" + index, typeName, fieldReaders, "read");
        EmitDispatch(root, "ValidateMembers_" + index, typeName, validators, "validate");
        EmitEntryReader(root, type, typeName, index, parameterless);
        if (ExtensionProperty(type) is { } extension)
        {
            EmitKnownNames(root, properties, fields, index);
            EmitExtensionWriter(root, typeName, extension.Name, index);
        }
    }

    private static void EmitExtensionWriter(TypeBuilder<FileBuilder> root, string type, string name, int index)
    {
        var loop = NodeEach("entry", Cs.Symbol("extra"),
            NodeCall("w.WritePropertyName", NodeMember(Cs.Symbol("entry"), "Key")),
            NodeCall("entry.Value.WriteTo", Cs.Symbol("w")));
        root.Method(Cs.Void, $"WriteExtra_{index}").Private().Static()
            .Parameter(Cs.Type("Utf8JsonWriter"), "w").Parameter(NodeType(type), "value")
            .Body(Cs.Tree(NodeIf(Cs.Symbol(new() { AsIs = true, Value = NodeMember(Cs.Symbol("value"), name),
                Pattern = new CsPattern(PatternKind.Recursive) { HasProperties = true, Name = "extra" } }), loop)))
            .End();
    }

    private List<string> EmitWriters(TypeBuilder<FileBuilder> root, IPropertySymbol[] properties,
        IFieldSymbol[] fields, string typeName, int index)
    {
        var names = new List<string>();
        foreach (var member in properties.Cast<ISymbol>().Concat(fields))
        {
            string method = $"WriteMember_{index}_{names.Count}";
            names.Add(method);
            var body = new List<CsNode>();
            if (member is IPropertySymbol property) EmitPropertyWriter(body, property);
            else EmitFieldWriter(body, (IFieldSymbol)member);
            Writer(root, method, typeName).Body(Cs.Tree(body.ToArray())).End();
        }
        return names;
    }

    private void EmitPropertyWriter(List<CsNode> body, IPropertySymbol property)
    {
        string value = "value_" + property.Name;
        string name = "name_" + property.Name;
        body.Add(NodeVar(value, NodeMember(Cs.Symbol("value"), property.Name)));
        CsNode[] statements = [NodeVar(name, JsonNameExpression(property)),
            NodeCall("w.WritePropertyName", Cs.Symbol(name)),
            NodeCall($"Write_{owner.Register(property.Type)}", Cs.Symbol("w"), Cs.Symbol(value), Cs.Symbol("options"),
                Path(Cs.Symbol(name)))];
        CsNode? condition = IgnoreCondition(property) switch
        {
            2 => NonDefaultExpression(property), 3 => NullTest(Cs.Symbol(value), true), _ => null
        };
        if (condition is null) body.AddRange(statements);
        else body.Add(NodeIf(condition, statements));
    }

    private void EmitFieldWriter(List<CsNode> body, IFieldSymbol field)
    {
        CsNode name = JsonNameExpression(field);
        body.Add(NodeCall("w.WritePropertyName", name));
        body.Add(NodeCall($"Write_{owner.Register(field.Type)}", Cs.Symbol("w"),
            NodeMember(Cs.Symbol("value"), field.Name),
            Cs.Symbol("options"), Path(name)));
    }

    private List<string> EmitReaders(TypeBuilder<FileBuilder> root, IPropertySymbol[] properties, string typeName,
        int index, bool parameterless)
    {
        var names = new List<string>();
        for (int position = 0; position < properties.Length; position++)
        {
            var property = properties[position];
            string method = $"ReadMember_{index}_{position}";
            names.Add(method);
            var body = new List<CsNode> { NodeVar("p_" + property.Name, NodeMember(Cs.Symbol("entry"), "Value")) };
            if (IsRequired(property))
                body.Add(NodeAssign(Index(Cs.Symbol("state"), Cs.Value(properties.Length + position)), Cs.Value(true)));
            EmitReadPropertyValue(body, property, position, parameterless);
            Reader(root, method, typeName).Body(Cs.Tree(Cs.Instruction(InstructionKind.If, new()
            {
                Condition = NodeCall("entry.NameEquals", JsonNameExpression(property)),
                Body = Cs.Tree(body.ToArray())
            }))).End();
        }
        return names;
    }

    private void EmitReadPropertyValue(List<CsNode> body, IPropertySymbol property, int position,
        bool parameterless)
    {
        if (parameterless && PopulateEmitter.IsReadonly(property))
        {
            var element = PopulateEmitter.ListElementType(property);
            PopulateEmitter.EmitReadonlyRead(body, property, element is null ? -1 : owner.Register(element));
            return;
        }
        string input = "p_" + property.Name;
        CsNode path = Path(JsonNameExpression(property));
        CsNode read = NodeCall($"Read_{owner.Register(property.Type)}", Cs.Symbol(input), Cs.Symbol("options"), path);
        CsNode target = parameterless ? NodeMember(Cs.Symbol("result"), property.Name) :
            Index(Cs.Symbol("state"), Cs.Value(position));
        if (RequiresNumberParsing(property))
        {
            var condition = NodeBinary(CsOperator.Equal, NodeMember(Cs.Symbol(input), "ValueKind"),
                NodeMember(Cs.Symbol("JsonValueKind"), "String"));
            var parse = NodeCall("ParseInt32", Cs.Symbol(input), path);
            body.Add(Cs.Instruction(InstructionKind.If, new() { Condition = condition,
                Body = Cs.Tree(NodeAssign(target, parse)), Else = Cs.Tree(NodeAssign(target, read)) }));
        }
        else body.Add(NodeAssign(target, read));
    }

    private List<string> EmitFieldReaders(TypeBuilder<FileBuilder> root, IFieldSymbol[] fields, string typeName,
        int index)
    {
        var names = new List<string>();
        foreach (var field in fields)
        {
            string method = $"ReadField_{index}_{names.Count}";
            names.Add(method);
            CsNode name = JsonNameExpression(field);
            CsNode read = NodeCall($"Read_{owner.Register(field.Type)}", NodeMember(Cs.Symbol("entry"), "Value"),
                Cs.Symbol("options"), Path(name));
            Reader(root, method, typeName).Body(Cs.Tree(NodeIf(NodeCall("entry.NameEquals", name),
                NodeAssign(NodeMember(Cs.Symbol("result"), field.Name), read)))).End();
        }
        return names;
    }

    private static List<string> EmitValidators(TypeBuilder<FileBuilder> root, IPropertySymbol[] properties, int index)
    {
        var names = new List<string>();
        for (int position = 0; position < properties.Length; position++)
        {
            if (!IsRequired(properties[position])) continue;
            string method = $"ValidateMember_{index}_{position}";
            names.Add(method);
            Validator(root, method).Body(Cs.Tree(NodeIf(
                NullTest(Index(Cs.Symbol("state"), Cs.Value(properties.Length + position))),
                NodeThrow(NodeNew("JsonException"))))).End();
        }
        return names;
    }

    private static MethodBuilder<TypeBuilder<FileBuilder>> Writer(TypeBuilder<FileBuilder> root, string name,
        string type)
        => root.Method(Cs.Void, name).Private().Static().Parameter(Cs.Type("Utf8JsonWriter"), "w")
            .Parameter(NodeType(type), "value").Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Parameter(Cs.Type("string"), "path");

    private static MethodBuilder<TypeBuilder<FileBuilder>> Reader(TypeBuilder<FileBuilder> root, string name,
        string type)
        => root.Method(Cs.Void, name).Private().Static().Parameter(Cs.Type("JsonProperty"), "entry")
            .Parameter(NodeType(type), "result", "ref").Parameter(Cs.Type("object").Nullable().Array(), "state")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Parameter(Cs.Type("string"), "path");

    private static MethodBuilder<TypeBuilder<FileBuilder>> Validator(TypeBuilder<FileBuilder> root, string name)
        => root.Method(Cs.Void, name).Private().Static().Parameter(Cs.Type("object").Nullable().Array(), "state");

    private static void EmitDispatch(TypeBuilder<FileBuilder> root, string name, string type, List<string> methods,
        string kind) => EmitBatches(name, methods, (batchName, batch) =>
            EmitDispatchMethod(root, batchName, type, batch, kind));

    private static void EmitBatches(string root, List<string> methods, Action<string, IEnumerable<string>> emit)
    {
        int level = 0;
        while (methods.Count > CallsPerBatch)
        {
            var next = new List<string>();
            foreach (var batch in methods.Chunk(CallsPerBatch))
            {
                string name = root + "_" + level + "_" + next.Count;
                next.Add(name);
                emit(name, batch);
            }
            methods = next;
            level++;
        }
        emit(root, methods);
    }

    private static void EmitDispatchMethod(TypeBuilder<FileBuilder> root, string name, string type,
        IEnumerable<string> methods, string kind)
    {
        var method = kind switch { "write" => Writer(root, name, type), "read" => Reader(root, name, type),
            _ => Validator(root, name) };
        CsNode[] args = kind switch {
            "write" => [Cs.Symbol("w"), Cs.Symbol("value"), Cs.Symbol("options"), Cs.Symbol("path")],
            "read" => [Cs.Symbol("entry"), Arg(ArgumentModifier.Ref, Cs.Symbol("result")), Cs.Symbol("state"),
                Cs.Symbol("options"), Cs.Symbol("path")],
            _ => [Cs.Symbol("state")] };
        method.Body(Cs.Tree(methods.Select(item => NodeCall(item, args)).ToArray())).End();
    }

    private static void EmitEntryReader(TypeBuilder<FileBuilder> root, INamedTypeSymbol type, string typeName,
        int index, bool parameterless)
    {
        var body = new List<CsNode>();
        CsNode[] args = [Cs.Symbol("entry"), Arg(ArgumentModifier.Ref, Cs.Symbol("result")), Cs.Symbol("state"),
            Cs.Symbol("options"),
            Cs.Symbol("path")];
        if (parameterless) body.Add(NodeCall($"ReadMembers_{index}", args));
        body.Add(NodeCall($"ReadFields_{index}", args));
        var extension = ExtensionProperty(type);
        if (extension is not null)
            body.Add(NodeIf(Cs.Symbol(new() { Operator = CsOperator.Not, Parts =
                new CsNode[] { NodeCall($"IsKnown_{index}", NodeMember(Cs.Symbol("entry"), "Name"),
                    Cs.Symbol("options")) } }),
                NodeAssign(Index(Cs.Symbol(new() { AsParenthesized = true, Value = Cs.Symbol(new()
                    { AsAssignment = true, Operator = CsOperator.Coalesce,
                        Target = NodeMember(Cs.Symbol("result"), extension.Name),
                        Value = Cs.Symbol(new() { AsNew = true }) }) }), NodeMember(Cs.Symbol("entry"), "Name")),
                    NodeCall("entry.Value.Clone"))));
        Reader(root, (parameterless ? "ReadEntry_" : "ReadRemaining_") + index, typeName)
            .Body(Cs.Tree(body.ToArray())).End();
    }

    private static void EmitKnownNames(TypeBuilder<FileBuilder> root, IPropertySymbol[] properties,
        IFieldSymbol[] fields, int index)
    {
        var names = new List<string>();
        foreach (var member in properties.Cast<ISymbol>().Concat(fields))
        {
            string method = $"KnownName_{index}_{names.Count}";
            names.Add(method);
            Known(root, method).Body(Cs.Tree(Return(NodeBinary(CsOperator.Equal, Cs.Symbol("name"),
                JsonNameExpression(member))))).End();
        }
        EmitBatches("IsKnown_" + index, names, (name, batch) => EmitKnownMethod(root, name, batch));
    }

    private static void EmitKnownMethod(TypeBuilder<FileBuilder> root, string name, IEnumerable<string> methods)
    {
        var calls = methods.Select(method => (CsNode)NodeCall(method, Cs.Symbol("name"), Cs.Symbol("options")))
            .ToArray();
        CsNode expression = calls.Length == 0 ? Cs.Value(false) : calls.Aggregate(
            (left, right) => NodeBinary(CsOperator.OrElse, left, right));
        Known(root, name).Body(Cs.Tree(Return(expression))).End();
    }

    private static MethodBuilder<TypeBuilder<FileBuilder>> Known(TypeBuilder<FileBuilder> root, string name) =>
        root.Method(Cs.Bool, name).Private().Static().Parameter(Cs.Type("string"), "name")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options");

    private static CsSymbol Path(CsNode name) => NodeBinary(CsOperator.Add,
        NodeBinary(CsOperator.Add, Cs.Symbol("path"), Cs.Value(".")), name);

}

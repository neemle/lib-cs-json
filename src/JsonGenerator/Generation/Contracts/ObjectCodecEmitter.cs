using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static Neemle.Json.Generator.CodecApiEmitter;
using static Neemle.Json.Generator.CodecEmitter;

namespace Neemle.Json.Generator;

sealed class ObjectCodecEmitter(CodecEmitter owner)
{
    private const int CallsPerBatch = 8;
    private readonly Dictionary<ISymbol, string> attributeConverters = new(SymbolEqualityComparer.Default);
    private readonly MemberAccessEmitter access = new();

    internal void EmitObjectWriter(List<CsNode> body, INamedTypeSymbol type, int index)
    {
        EmitExtensionContractValidation(body, type);
        EmitUnsupportedFieldsCheck(body, type);
        body.Add(NodeCall($"ValidateNames_{index}", Cs.Symbol("options")));
        EmitCallback(body, type, "IJsonOnSerializing", "OnSerializing", "value");
        body.Add(NodeCall("w.WriteStartObject"));
        if (type.IsReferenceType) body.Add(NodeCall("WriteReferenceId", Cs.Symbol("w"), Cs.Symbol("value"),
            Cs.Symbol("options")));
        body.Add(NodeCall($"WriteMembers_{index}", Cs.Symbol("w"), Cs.Symbol("value"),
            Cs.Symbol("options"), Cs.Symbol("path")));
        var extension = ExtensionMember(type);
        if (extension is not null) body.Add(NodeIf(ExtensionEnabled(extension), NodeCall($"WriteExtra_{index}",
            Cs.Symbol("w"), Cs.Symbol("value"), Cs.Symbol("options"), Cs.Symbol("path"))));
        body.Add(NodeCall("w.WriteEndObject"));
        EmitCallback(body, type, "IJsonOnSerialized", "OnSerialized", "value");
    }

    internal void EmitObjectReader(List<CsNode> body, INamedTypeSymbol type, int index)
    {
        EmitExtensionContractValidation(body, type);
        EmitUnsupportedFieldsCheck(body, type);
        body.Add(NodeCall($"ValidateNames_{index}", Cs.Symbol("options")));
        var properties = Properties(type);
        string name = TypeName(type);
        bool parameterless = UsesParameterless(type, properties);
        body.Add(NodeCall("RequireObject", Cs.Symbol("e")));
        var validation = new List<CsNode>();
        PopulateEmitter.EmitValidation(validation, properties);
        if (validation.Count != 0) body.Add(NodeCall($"ValidatePopulate_{index}", Cs.Symbol("e"), Cs.Symbol("options")));
        body.Add(Cs.Symbol("result", new()
        {
            Type = NodeType(name),
            Value = parameterless ? Construct(type.InstanceConstructors.First(c => AccessibleConstructor(c) &&
                c.Parameters.Length == 0), index, []) : NonNull(DefaultValue())
        }));
        if (parameterless && type.IsReferenceType) body.Add(NodeCall("RegisterReference", Cs.Symbol("e"),
            Cs.Symbol("result"), Cs.Symbol("options")));
        if (parameterless) EmitCallback(body, type, "IJsonOnDeserializing", "OnDeserializing", "result");
        body.Add(Cs.Symbol("state", new()
        {
            Type = Cs.Type("object").Nullable().Array(),
            Value = Cs.Symbol(new()
            {
                AsArray = true,
                Type = Cs.Type("object").Nullable(),
                Arguments = Cs.Compound(Cs.Value(properties.Length * 2 + AvailableFields(type).Length))
            })
        }));
        string read = parameterless ? "ReadEntry_" : "ReadMembers_";
        body.Add(ReadAll(read + index));
        body.Add(NodeCall($"ValidateMembers_{index}", Cs.Symbol("state"), Cs.Symbol("options")));
        if (!parameterless)
        {
            body.Add(Cs.Symbol("result", new() { AsAssignment = true, Value = Construction(type, properties, index, body) }));
            if (type.IsReferenceType) body.Add(NodeCall("RegisterReference", Cs.Symbol("e"), Cs.Symbol("result"),
                Cs.Symbol("options")));
            EmitCallback(body, type, "IJsonOnDeserializing", "OnDeserializing", "result");
            body.Add(NodeCall($"ApplyRemaining_{index}", Arg(ArgumentModifier.Ref, Cs.Symbol("result")),
                Cs.Symbol("state")));
            body.Add(ReadAll("ReadRemaining_" + index));
        }
        body.Add(NodeCall($"ValidateFields_{index}", Cs.Symbol("state"), Cs.Symbol("options")));
        EmitCallback(body, type, "IJsonOnDeserialized", "OnDeserialized", "result");
        body.Add(Return(Cs.Symbol("result")));
    }

    private static void EmitExtensionContractValidation(List<CsNode> body, INamedTypeSymbol type)
    {
        if (ExtensionMember(type) is null) return;
        var attribute = type.GetAttributes().FirstOrDefault(item => IsJsonAttribute(item, "JsonUnmappedMemberHandlingAttribute"));
        if (attribute?.ConstructorArguments.FirstOrDefault().Value is int handling && handling == 1)
            body.Add(NodeCall("ThrowInvalidObjectContract"));
    }

    internal static void EmitCallback(List<CsNode> body, INamedTypeSymbol type, string contract, string method,
        string value)
    {
        string name = "System.Text.Json.Serialization." + contract;
        if (!type.AllInterfaces.Any(item => item.ToDisplayString() == name)) return;
        if (type.IsValueType)
        {
            body.Add(NodeCall("StructCallback_" + method, Arg(ArgumentModifier.Ref, Cs.Symbol(value))));
            return;
        }
        body.Add(Cs.Symbol(method, new()
        {
            Target = Cast("global::" + name, Cs.Symbol(value)),
            AsCall = true,
            Arguments = Cs.Compound()
        }));
    }

    private void EmitUnsupportedFieldsCheck(List<CsNode> body, INamedTypeSymbol type)
    {
        if (OptionalFields(type).Any(field => !owner.SupportsOptionalField(field)))
            body.Add(NodeIf(OptionEnabled("IncludeFields"), NodeThrow(NodeNew("NotSupportedException"))));
    }

    private static CsInstruction ReadEntries(string method) => NodeEach("entry", NodeCall("e.EnumerateObject"),
        NodeIf(NodeCall("IsReferenceId", NodeMember(Cs.Symbol("entry"), "Name"), Cs.Symbol("options")),
            Cs.Instruction(InstructionKind.Continue)),
        NodeCall(method, Cs.Symbol("entry"), Arg(ArgumentModifier.Ref, Cs.Symbol("result")), Cs.Symbol("state"),
            Cs.Symbol("options"), Cs.Symbol("path")));

    private static CsNode ReadAll(string method) => NodeCall("ReadAll_" + method, Cs.Symbol("e"),
        Arg(ArgumentModifier.Ref, Cs.Symbol("result")), Cs.Symbol("state"), Cs.Symbol("options"), Cs.Symbol("path"));

    private static void EmitReadAll(TypeBuilder<FileBuilder> root, string type, string method)
    {
        root.Method(Cs.Void, "ReadAll_" + method).Private().Static().Parameter(Cs.Type("JsonElement"), "e")
            .Parameter(NodeType(type), "result", "ref").Parameter(Cs.Type("object").Nullable().Array(), "state")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Parameter(Cs.Type("string"), "path")
            .Body(Cs.Tree(ReadEntries(method))).End();
    }

    private static bool UsesParameterless(INamedTypeSymbol type, IPropertySymbol[] properties) =>
        !IsKeyValuePair(type) && !type.InstanceConstructors.Any(c => IsJsonConstructor(c) && c.Parameters.Length > 0) &&
        type.InstanceConstructors.Any(c => AccessibleConstructor(c) && c.Parameters.Length == 0) &&
        properties.All(p => CanSet(p) || PopulateEmitter.CanHandleReadonly(p));

    private static CsNode Construction(INamedTypeSymbol type, IPropertySymbol[] properties, int index, List<CsNode> body)
    {
        var constructor = Constructor(type, properties) ?? throw new InvalidOperationException(
            "No supported JSON constructor for " + TypeName(type));
        var arguments = new List<CsNode>();
        foreach (var parameter in constructor.Parameters)
        {
            if (parameter.RefKind is RefKind.Ref or RefKind.Out || parameter.RefKind.ToString() == "RefReadOnlyParameter")
            {
                string name = "constructorArgument" + arguments.Count;
                body.Add(Cs.Symbol(name, new() { Type = NodeType(TypeName(parameter.Type)),
                    Value = parameter.RefKind == RefKind.Out ? DefaultValue() : ConstructorArgument(parameter, properties) }));
                arguments.Add(Arg(parameter.RefKind == RefKind.Out ? ArgumentModifier.Out :
                    parameter.RefKind == RefKind.Ref ? ArgumentModifier.Ref : ArgumentModifier.In, Cs.Symbol(name)));
            }
            else arguments.Add(ConstructorArgument(parameter, properties));
        }
        return Construct(constructor, index, arguments.ToArray());
    }

    private static CsNode Construct(IMethodSymbol constructor, int index, CsNode[] arguments) =>
        constructor.ContainingType.IsValueType && constructor.IsImplicitlyDeclared && arguments.Length == 0 ? DefaultValue() :
        !MemberAccessEmitter.NeedsConstructorAccessor(constructor) ? NodeNew(TypeName(constructor.ContainingType),
            arguments) : NodeCall("Access_constructor_" + index, arguments);

    private static CsNode ConstructorArgument(IParameterSymbol parameter, IPropertySymbol[] properties)
    {
        var args = new List<CsNode> { Cs.Symbol("state"), Cs.Value(PropertyIndex(properties, parameter)) };
        if (parameter.HasExplicitDefaultValue)
        {
            args.Add(Cs.Value(properties.Length));
            args.Add(ParameterDefault(parameter));
        }
        return Cs.Symbol(new()
        {
            AsCall = true,
            Target = Cs.Symbol(parameter.HasExplicitDefaultValue ? "GetSlotOrDefault" : "GetSlot", new()
            { TypeArguments = new[] { NodeType(TypeName(parameter.Type)) } }),
            Arguments = Cs.Compound(args.ToArray())
        });
    }

    private static CsNode ParameterDefault(IParameterSymbol parameter)
    {
        if (parameter.ExplicitDefaultValue is null && parameter.Type.IsValueType &&
            parameter.Type is not INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
            return DefaultValue();
        var value = Cs.Value(parameter.ExplicitDefaultValue);
        return parameter.Type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Enum ? Cast(TypeName(parameter.Type), value) : value;
    }

    private static int PropertyIndex(IPropertySymbol[] properties, IParameterSymbol parameter) =>
        parameter.RefKind == RefKind.Out ? -1 : Array.FindIndex(properties, property => string.Equals(property.Name, parameter.Name,
            StringComparison.OrdinalIgnoreCase) && SymbolEqualityComparer.Default.Equals(property.Type, parameter.Type));

    internal void EmitObjectHelpers(TypeBuilder<FileBuilder> root, INamedTypeSymbol type, int index)
    {
        var properties = Properties(type);
        var fields = AvailableFields(type);
        var extensionMember = ExtensionMember(type);
        EmitPopulateValidation(root, properties, index);
        access.Emit(root, extensionMember is IPropertySymbol extensionProperty ? properties.Append(extensionProperty) : properties,
            extensionMember is IFieldSymbol extensionField ? fields.Append(extensionField) : fields, index);
        var constructor = UsesParameterless(type, properties) ? type.InstanceConstructors.First(c =>
            AccessibleConstructor(c) && c.Parameters.Length == 0) : Constructor(type, properties);
        bool constructorSetsRequired = constructor?.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() ==
            "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute") == true;
        if (constructor is not null) MemberAccessEmitter.EmitConstructor(root, constructor, index);
        PropertyValidationEmitter.Emit(root, properties, fields, index);
        attributeConverters.Clear();
        foreach (var member in properties.Cast<ISymbol>().Concat(fields))
        {
            var memberType = member is IPropertySymbol property ? property.Type : ((IFieldSymbol)member).Type;
            if (ConverterAttributeEmitter.Converter(member, memberType) is not { } converter) continue;
            string suffix = $"{index}_{attributeConverters.Count}";
            attributeConverters.Add(member, suffix);
            ConverterAttributeEmitter.Emit(root, memberType, converter, suffix);
        }
        int? typeHandling = NumberHandling(type);
        bool parameterless = UsesParameterless(type, properties);
        string typeName = TypeName(type);
        var writers = EmitWriters(root, properties, fields, typeName, index, typeHandling);
        var readers = EmitReaders(root, type, properties, typeName, index, parameterless, typeHandling);
        var fieldReaders = EmitFieldReaders(root, fields, typeName, index, properties.Length, typeHandling);
        List<string> setters = parameterless ? [] : EmitRemainingSetters(root, type, properties, typeName, index);
        var validators = EmitValidators(root, type, properties, index, (parameterless, constructorSetsRequired));
        var fieldValidators = EmitFieldValidators(root, properties.Length, fields, index, constructorSetsRequired);
        EmitDispatch(root, "WriteMembers_" + index, typeName, writers, "write");
        EmitDispatch(root, "ReadMembers_" + index, typeName, readers, "read");
        EmitDispatch(root, "ReadFields_" + index, typeName, fieldReaders, "read");
        EmitDispatch(root, "ValidateMembers_" + index, typeName, validators, "validate");
        EmitDispatch(root, "ValidateFields_" + index, typeName, fieldValidators, "validate");
        if (!parameterless) EmitDispatch(root, "ApplyRemaining_" + index, typeName, setters, "apply");
        EmitEntryReader(root, type, typeName, index, parameterless);
        EmitReadAll(root, typeName, (parameterless ? "ReadEntry_" : "ReadMembers_") + index);
        if (parameterless) EmitPopulateObject(root, type, properties, fields.Length, index);
        if (!parameterless) EmitReadAll(root, typeName, "ReadRemaining_" + index);
        EmitKnownNames(root, properties, fields, index);
        if (ExtensionMember(type) is { } extension)
        {
            EmitExtensionWriter(root, typeName, extension, index);
        }
    }

    private static void EmitPopulateValidation(TypeBuilder<FileBuilder> root, IPropertySymbol[] properties, int index)
    {
        var checks = new List<CsNode>();
        PopulateEmitter.EmitValidation(checks, properties);
        if (checks.Count == 0) return;
        for (int i = 0; i < checks.Count; i++)
            root.Method(Cs.Void, $"ValidatePopulate_{index}_{i}").Private().Static()
                .Parameter(Cs.Type("JsonElement"), "e").Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
                .Body(Cs.Tree(checks[i])).End();
        root.Method(Cs.Void, $"ValidatePopulate_{index}").Private().Static()
            .Parameter(Cs.Type("JsonElement"), "e").Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Body(Cs.Tree(Enumerable.Range(0, checks.Count).Select(i => (CsNode)NodeCall($"ValidatePopulate_{index}_{i}",
                Cs.Symbol("e"), Cs.Symbol("options"))).ToArray())).End();
    }

    private static void EmitPopulateObject(TypeBuilder<FileBuilder> root, INamedTypeSymbol type,
        IPropertySymbol[] properties, int fieldCount, int index)
    {
        var body = new List<CsNode> { NodeCall("RequireObject", Cs.Symbol("e")),
            NodeCall($"ValidateNames_{index}", Cs.Symbol("options")) };
        EmitCallback(body, type, "IJsonOnDeserializing", "OnDeserializing", "result");
        body.Add(Cs.Symbol("state", new()
        {
            Type = Cs.Type("object").Nullable().Array(),
            Value = Cs.Symbol(new()
            {
                AsArray = true,
                Type = Cs.Type("object").Nullable(),
                Arguments = Cs.Compound(Cs.Value(properties.Length * 2 + fieldCount))
            })
        }));
        body.Add(ReadAll("ReadEntry_" + index));
        body.Add(NodeCall($"ValidateMembers_{index}", Cs.Symbol("state"), Cs.Symbol("options")));
        body.Add(NodeCall($"ValidateFields_{index}", Cs.Symbol("state"), Cs.Symbol("options")));
        EmitCallback(body, type, "IJsonOnDeserialized", "OnDeserialized", "result");
        root.Method(Cs.Void, $"Populate_{index}").Private().Static().Parameter(Cs.Type("JsonElement"), "e")
            .Parameter(NodeType(TypeName(type)), "result", "ref")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Parameter(Cs.Type("string"), "path")
            .Body(Cs.Tree(body.ToArray())).End();
    }

    private IFieldSymbol[] AvailableFields(INamedTypeSymbol type)
    {
        var available = new HashSet<IFieldSymbol>(Fields(type).Concat(OptionalFields(type)
            .Where(owner.SupportsOptionalField)), SymbolEqualityComparer.Default);
        return HierarchyMembers(type).OfType<IFieldSymbol>().Where(available.Contains).ToArray();
    }

    private void EmitExtensionWriter(TypeBuilder<FileBuilder> root, string type, ISymbol extension, int index)
    {
        var body = new List<CsNode> { NodeCall("w.WritePropertyName", NodeMember(Cs.Symbol("entry"), "Key")) };
        if (IsNodeExtension(extension))
        {
            body.Add(NodeVar("extraValue", NodeMember(Cs.Symbol("entry"), "Value")));
            body.Add(NodeIf(NullTest(Cs.Symbol("extraValue")), NodeCall("w.WriteNullValue")));
            body.Add(NodeIf(NullTest(Cs.Symbol("extraValue"), true), NodeCall("extraValue.WriteTo", Cs.Symbol("w"),
                Cs.Symbol("options"))));
        }
        else
        {
            var valueType = ((INamedTypeSymbol)MemberType(extension)).TypeArguments[1];
            body.Add(valueType.SpecialType == SpecialType.System_Object ?
                NodeCall($"Write_{owner.Register(valueType)}", Cs.Symbol("w"), NodeMember(Cs.Symbol("entry"), "Value"),
                    Cs.Symbol("options"), Path(NodeMember(Cs.Symbol("entry"), "Key"))) :
                NodeCall("entry.Value.WriteTo", Cs.Symbol("w")));
        }
        var loop = NodeEach("entry", Cs.Symbol("extra"), body.ToArray());
        root.Method(Cs.Void, $"WriteExtra_{index}").Private().Static()
            .Parameter(Cs.Type("Utf8JsonWriter"), "w").Parameter(NodeType(type), "value")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Parameter(Cs.Type("string"), "path")
            .Body(Cs.Tree(NodeIf(Cs.Symbol(new()
            {
                AsIs = true,
                Value = access.Read(extension, "value"),
                Pattern = new CsPattern(PatternKind.Recursive) { HasProperties = true, Name = "extra" }
            }), loop)))
            .End();
    }

    private List<string> EmitWriters(TypeBuilder<FileBuilder> root, IPropertySymbol[] properties,
        IFieldSymbol[] fields, string typeName, int index, int? typeHandling)
    {
        var names = new List<string>();
        foreach (var member in properties.Cast<ISymbol>().Concat(fields).OrderBy(PropertyOrder))
        {
            if (IgnoreCondition(member) == 4) continue;
            string method = $"WriteMember_{index}_{names.Count}";
            names.Add(method);
            var body = new List<CsNode>();
            if (member is IPropertySymbol property) EmitPropertyWriter(body, property, typeHandling);
            else EmitFieldWriter(body, (IFieldSymbol)member, typeHandling);
            Writer(root, method, typeName).Body(Cs.Tree(body.ToArray())).End();
        }
        return names;
    }

    private static int PropertyOrder(ISymbol member) => member.GetAttributes()
        .FirstOrDefault(attribute => IsJsonAttribute(attribute, "JsonPropertyOrderAttribute"))
        ?.ConstructorArguments.FirstOrDefault().Value as int? ?? 0;

    private void EmitPropertyWriter(List<CsNode> body, IPropertySymbol property, int? typeHandling)
    {
        string value = "value_" + property.Name;
        string name = "name_" + property.Name;
        var writeBody = new List<CsNode> { NodeVar(value, access.Read(property, "value")) };
        var arguments = new List<CsNode> { Cs.Symbol("w"), Cs.Symbol(value), Cs.Symbol("options"),
            Path(Cs.Symbol(name)) };
        bool attributed = attributeConverters.TryGetValue(property, out string? converter);
        if (!attributed) arguments.AddRange(NumberHandlingEmitter.Arguments(property, typeHandling));
        if (!attributed) arguments.AddRange(EnumHandlingEmitter.Arguments(property, property.Type));
        var statements = new List<CsNode> { NodeVar(name, JsonNameExpression(property)) };
        if (NullableContractEmitter.WriteCheck(property, property.Type, Cs.Symbol(value), Path(Cs.Symbol(name))) is
            { } nullCheck) statements.Add(nullCheck);
        statements.Add(NodeCall("w.WritePropertyName", Cs.Symbol(name)));
        statements.Add(NodeCall(attributed ? $"WriteAttribute_{converter}" :
            $"Write_{owner.Register(property.Type)}", arguments.ToArray()));
        CsNode? condition = IgnoreConditionEmitter.Condition(property, property.Type, value);
        if (condition is null) writeBody.AddRange(statements);
        else writeBody.Add(NodeIf(condition, statements.ToArray()));
        if (IgnoreReadonlyOptionApplies(property))
            body.Add(NodeIf(NodeBinary(CsOperator.NotEqual, Cs.Symbol("IgnoreReadOnlyProperties", new()
            { Target = Cs.Symbol("options"), ConditionalAccess = true }), Cs.Value(true)), writeBody.ToArray()));
        else body.AddRange(writeBody);
    }

    private static bool IgnoreReadonlyOptionApplies(IPropertySymbol property)
    {
        if (CanSet(property)) return false;
        if (property.Type is INamedTypeSymbol named && named.SpecialType != SpecialType.System_String &&
            named.AllInterfaces.Any(type => type.ToDisplayString() == "System.Collections.IEnumerable")) return false;
        if (property.Type is IArrayTypeSymbol) return false;
        return !property.GetAttributes().Any(attribute => IsJsonAttribute(attribute, "JsonIgnoreAttribute") &&
            attribute.NamedArguments.Any(argument => argument.Key == "Condition" && (int?)argument.Value.Value == 0));
    }

    private void EmitFieldWriter(List<CsNode> body, IFieldSymbol field, int? typeHandling)
    {
        string local = "value_" + field.Name;
        var writeBody = new List<CsNode> { NodeVar(local, access.Read(field, "value")) };
        CsNode name = JsonNameExpression(field);
        var arguments = new List<CsNode> { Cs.Symbol("w"), Cs.Symbol(local), Cs.Symbol("options"), Path(name) };
        bool attributed = attributeConverters.TryGetValue(field, out string? converter);
        if (!attributed) arguments.AddRange(NumberHandlingEmitter.Arguments(field, typeHandling));
        if (!attributed) arguments.AddRange(EnumHandlingEmitter.Arguments(field, field.Type));
        var statements = new List<CsNode>();
        if (NullableContractEmitter.WriteCheck(field, field.Type, Cs.Symbol(local), Path(name)) is { } nullCheck)
            statements.Add(nullCheck);
        statements.Add(NodeCall("w.WritePropertyName", name));
        statements.Add(NodeCall(attributed ? $"WriteAttribute_{converter}" :
            $"Write_{owner.Register(field.Type)}", arguments.ToArray()));
        CsNode? condition = IgnoreConditionEmitter.Condition(field, field.Type, local);
        if (condition is null) writeBody.AddRange(statements);
        else writeBody.Add(NodeIf(condition, statements.ToArray()));
        CsNode? allowed = HasJsonInclude(field) ? null : OptionEnabled("IncludeFields");
        if (field.IsReadOnly && !IsExplicitNever(field))
        {
            var writable = NodeBinary(CsOperator.NotEqual, Option("IgnoreReadOnlyFields"), Cs.Value(true));
            allowed = allowed is null ? writable : NodeBinary(CsOperator.AndAlso, allowed, writable);
        }
        if (allowed is null) body.AddRange(writeBody);
        else body.Add(NodeIf(allowed, writeBody.ToArray()));
    }

    private static CsSymbol Option(string name) => Cs.Symbol(name, new()
    { Target = Cs.Symbol("options"), ConditionalAccess = true });

    private static CsNode OptionEnabled(string name) => NodeBinary(CsOperator.Equal, Option(name), Cs.Value(true));

    private static bool IsExplicitNever(ISymbol member) => member.GetAttributes()
        .Any(attribute => IsJsonAttribute(attribute, "JsonIgnoreAttribute") && attribute.NamedArguments
            .Any(argument => argument.Key == "Condition" && (int?)argument.Value.Value == 0));

    private List<string> EmitReaders(TypeBuilder<FileBuilder> root, INamedTypeSymbol type,
        IPropertySymbol[] properties, string typeName, int index, bool parameterless, int? typeHandling)
    {
        var names = new List<string>();
        for (int position = 0; position < properties.Length; position++)
        {
            var property = properties[position];
            if (IgnoreCondition(property) == 5) continue;
            string method = $"ReadMember_{index}_{position}";
            names.Add(method);
            var body = new List<CsNode> { NodeVar("p_" + property.Name, NodeMember(Cs.Symbol("entry"), "Value")) };
            int seen = properties.Length + position;
            if (!parameterless || !PopulateEmitter.IsReadonly(property))
                body.AddRange(DuplicatePropertyEmitter.Track(seen));
            else if (IsRequired(property)) body.Add(NodeAssign(Index(Cs.Symbol("state"), Cs.Value(seen)), Cs.Value(true)));
            var parameter = parameterless ? null : Constructor(type, properties)!.Parameters
                .FirstOrDefault(item => PropertyIndex(properties, item) == position);
            ISymbol contract = parameter is null ? property : parameter;
            EmitReadPropertyValue(body, property, (position, parameterless, typeHandling, contract,
                parameter?.Type ?? property.Type));
            if (parameterless && (PopulateEmitter.CollectionElementType(property) is not null ||
                PopulateEmitter.MapTypes(property) is not null || PopulateEmitter.ObjectType(property) is not null))
            {
                string helper = method + "Value";
                Reader(root, helper, typeName).Body(Cs.Tree(body.ToArray())).End();
                body = [NodeCall(helper, Cs.Symbol("entry"), Arg(ArgumentModifier.Ref, Cs.Symbol("result")),
                    Cs.Symbol("state"), Cs.Symbol("options"), Cs.Symbol("path"))];
            }
            Reader(root, method, typeName).Body(Cs.Tree(Cs.Instruction(InstructionKind.If, new()
            {
                Condition = NameMatches(NodeMember(Cs.Symbol("entry"), "Name"), JsonNameExpression(property)),
                Body = Cs.Tree(body.ToArray())
            }))).End();
        }
        return names;
    }

    private void EmitReadPropertyValue(List<CsNode> body, IPropertySymbol property,
        (int Position, bool Parameterless, int? TypeHandling, ISymbol Contract, ITypeSymbol ContractType) context)
    {
        if (context.Parameterless && PopulateEmitter.IsReadonly(property))
        {
            if (PopulateEmitter.MapTypes(property) is { } readonlyMap)
            {
                body.Add(PopulateEmitter.ReadonlyMapRead(property, access,
                    (owner.RegisterMapKey(readonlyMap.Key), owner.Register(readonlyMap.Value)), context.TypeHandling));
                return;
            }
            var element = PopulateEmitter.CollectionElementType(property);
            if (element is null && !property.Type.IsValueType &&
                PopulateEmitter.ObjectType(property) is { } readonlyObject)
            {
                body.AddRange(PopulateEmitter.ObjectRead(property, access, owner.Register(readonlyObject), null));
                return;
            }
            PopulateEmitter.EmitReadonlyRead(body, property, element is null ? -1 : owner.Register(element),
                context.TypeHandling);
            return;
        }
        string input = "p_" + property.Name;
        CsNode path = Path(JsonNameExpression(property));
        if (NullableContractEmitter.ReadCheck(context.Contract, context.ContractType, Cs.Symbol(input), path) is
            { } nullCheck)
            body.Add(nullCheck);
        var arguments = new List<CsNode> { Cs.Symbol(input), Cs.Symbol("options"), path };
        bool attributed = attributeConverters.TryGetValue(property, out string? converter);
        if (!attributed) arguments.AddRange(NumberHandlingEmitter.Arguments(property, context.TypeHandling));
        if (!attributed) arguments.AddRange(EnumHandlingEmitter.Arguments(property, property.Type));
        CsNode read = NodeCall(attributed ? $"ReadAttribute_{converter}" :
            $"Read_{owner.Register(property.Type)}", arguments.ToArray());
        CsNode assignment = context.Parameterless ? access.Write(property, "result", read) :
            NodeAssign(Index(Cs.Symbol("state"), Cs.Value(context.Position)), read);
        if (!attributed && context.Parameterless && PopulateEmitter.CollectionElementType(property) is { } listElement)
            body.AddRange(PopulateEmitter.WritableCollectionRead(property, access,
                (owner.Register(listElement), owner.Register(property.Type)), assignment, context.TypeHandling));
        else if (!attributed && context.Parameterless && PopulateEmitter.MapTypes(property) is { } writableMap)
            body.AddRange(PopulateEmitter.WritableMapRead(property, access,
                (owner.RegisterMapKey(writableMap.Key), owner.Register(writableMap.Value), owner.Register(property.Type)), assignment,
                context.TypeHandling));
        else if (!attributed && context.Parameterless && PopulateEmitter.ObjectType(property) is { } nestedObject)
            body.AddRange(PopulateEmitter.ObjectRead(property, access, owner.Register(nestedObject), assignment));
        else body.Add(assignment);
    }

    private List<string> EmitFieldReaders(TypeBuilder<FileBuilder> root, IFieldSymbol[] fields, string typeName,
        int index, int propertyCount, int? typeHandling)
    {
        var names = new List<string>();
        for (int position = 0; position < fields.Length; position++)
        {
            var field = fields[position];
            if (IgnoreCondition(field) == 5 || field.IsReadOnly &&
                (attributeConverters.ContainsKey(field) || PopulateEmitter.CollectionElementType(field) is null &&
                    PopulateEmitter.MapTypes(field) is null && (field.Type.IsValueType ||
                        PopulateEmitter.ObjectType(field) is null))) continue;
            string method = $"ReadField_{index}_{names.Count}";
            names.Add(method);
            CsNode name = JsonNameExpression(field);
            var arguments = new List<CsNode> { NodeMember(Cs.Symbol("entry"), "Value"), Cs.Symbol("options"),
                Path(name) };
            bool attributed = attributeConverters.TryGetValue(field, out string? converter);
            if (!attributed) arguments.AddRange(NumberHandlingEmitter.Arguments(field, typeHandling));
            if (!attributed) arguments.AddRange(EnumHandlingEmitter.Arguments(field, field.Type));
            CsNode read = NodeCall(attributed ? $"ReadAttribute_{converter}" :
                $"Read_{owner.Register(field.Type)}", arguments.ToArray());
            CsNode action = access.Write(field, "result", read);
            CsNode matches = NameMatches(NodeMember(Cs.Symbol("entry"), "Name"), name);
            if (!HasJsonInclude(field)) matches = NodeBinary(CsOperator.AndAlso, OptionEnabled("IncludeFields"), matches);
            var statements = DuplicatePropertyEmitter.Track(propertyCount * 2 + position).ToList();
            if (NullableContractEmitter.ReadCheck(field, field.Type, NodeMember(Cs.Symbol("entry"), "Value"),
                Path(name)) is { } nullCheck) statements.Add(nullCheck);
            if (!attributed && field.IsReadOnly && PopulateEmitter.CollectionElementType(field) is { } readonlyElement)
            {
                statements.Add(NodeVar("p_" + field.Name, NodeMember(Cs.Symbol("entry"), "Value")));
                statements.Add(PopulateEmitter.ReadonlyCollectionRead(field, access, owner.Register(readonlyElement),
                    typeHandling));
            }
            else if (!attributed && field.IsReadOnly && PopulateEmitter.MapTypes(field) is { } readonlyMap)
            {
                statements.Add(NodeVar("p_" + field.Name, NodeMember(Cs.Symbol("entry"), "Value")));
                statements.Add(PopulateEmitter.ReadonlyMapRead(field, access,
                    (owner.RegisterMapKey(readonlyMap.Key), owner.Register(readonlyMap.Value)), typeHandling));
            }
            else if (!attributed && field.IsReadOnly && !field.Type.IsValueType &&
                PopulateEmitter.ObjectType(field) is { } readonlyObject)
            {
                statements.Add(NodeVar("p_" + field.Name, NodeMember(Cs.Symbol("entry"), "Value")));
                statements.AddRange(PopulateEmitter.ObjectRead(field, access, owner.Register(readonlyObject), null));
            }
            else if (field.IsReadOnly) continue;
            else if (!attributed && PopulateEmitter.CollectionElementType(field) is { } element)
            {
                statements.Add(NodeVar("p_" + field.Name, NodeMember(Cs.Symbol("entry"), "Value")));
                statements.AddRange(PopulateEmitter.WritableCollectionRead(field, access,
                    (owner.Register(element), owner.Register(field.Type)),
                    action, typeHandling));
            }
            else if (!attributed && PopulateEmitter.MapTypes(field) is { } map)
            {
                statements.Add(NodeVar("p_" + field.Name, NodeMember(Cs.Symbol("entry"), "Value")));
                statements.AddRange(PopulateEmitter.WritableMapRead(field, access,
                    (owner.RegisterMapKey(map.Key), owner.Register(map.Value), owner.Register(field.Type)), action, typeHandling));
            }
            else if (!attributed && PopulateEmitter.ObjectType(field) is { } nested)
            {
                statements.Add(NodeVar("p_" + field.Name, NodeMember(Cs.Symbol("entry"), "Value")));
                statements.AddRange(PopulateEmitter.ObjectRead(field, access, owner.Register(nested), action));
            }
            else statements.Add(action);
            if (statements.Count > 3)
            {
                string helper = method + "Value";
                Reader(root, helper, typeName).Body(Cs.Tree(statements.ToArray())).End();
                statements = [NodeCall(helper, Cs.Symbol("entry"), Arg(ArgumentModifier.Ref, Cs.Symbol("result")),
                    Cs.Symbol("state"), Cs.Symbol("options"), Cs.Symbol("path"))];
            }
            Reader(root, method, typeName).Body(Cs.Tree(NodeIf(matches, statements.ToArray()))).End();
        }
        return names;
    }

    private static List<string> EmitValidators(TypeBuilder<FileBuilder> root, INamedTypeSymbol type,
        IPropertySymbol[] properties, int index, (bool Parameterless, bool ConstructorSetsRequired) settings)
    {
        var names = new List<string>();
        var parameters = settings.Parameterless ? [] : Constructor(type, properties)!.Parameters;
        for (int position = 0; position < properties.Length; position++)
        {
            bool required = IsRequired(properties[position]) && (!settings.ConstructorSetsRequired || properties[position]
                .GetAttributes().Any(attribute => IsJsonAttribute(attribute, "JsonRequiredAttribute")));
            bool requiredParameter = parameters.Any(parameter => !parameter.HasExplicitDefaultValue &&
                PropertyIndex(properties, parameter) == position);
            if (!required && !requiredParameter) continue;
            string method = $"ValidateMember_{index}_{position}";
            names.Add(method);
            CsNode missing = NullTest(Index(Cs.Symbol("state"), Cs.Value(properties.Length + position)));
            if (!required) missing = NodeBinary(CsOperator.AndAlso,
                NodeBinary(CsOperator.Equal, Option("RespectRequiredConstructorParameters"), Cs.Value(true)), missing);
            Validator(root, method).Body(Cs.Tree(NodeIf(missing, NodeThrow(NodeNew("JsonException"))))).End();
        }
        return names;
    }

    private static List<string> EmitFieldValidators(TypeBuilder<FileBuilder> root, int propertyCount,
        IFieldSymbol[] fields, int index, bool constructorSetsRequired)
    {
        var names = new List<string>();
        for (int position = 0; position < fields.Length; position++)
        {
            if (!IsRequired(fields[position]) || constructorSetsRequired && !fields[position].GetAttributes()
                .Any(attribute => IsJsonAttribute(attribute, "JsonRequiredAttribute"))) continue;
            string method = $"ValidateField_{index}_{position}";
            names.Add(method);
            CsNode missing = NullTest(Index(Cs.Symbol("state"), Cs.Value(propertyCount * 2 + position)));
            if (!HasJsonInclude(fields[position])) missing = NodeBinary(CsOperator.AndAlso, OptionEnabled("IncludeFields"), missing);
            Validator(root, method).Body(Cs.Tree(NodeIf(missing, NodeThrow(NodeNew("JsonException"))))).End();
        }
        return names;
    }

    private List<string> EmitRemainingSetters(TypeBuilder<FileBuilder> root, INamedTypeSymbol type,
        IPropertySymbol[] properties, string typeName, int index)
    {
        var parameters = Constructor(type, properties)!.Parameters;
        var names = new List<string>();
        for (int position = 0; position < properties.Length; position++)
        {
            var property = properties[position];
            if (parameters.Any(parameter => PropertyIndex(properties, parameter) == position) ||
                !CanSet(property)) continue;
            string name = $"ApplyMember_{index}_{position}";
            names.Add(name);
            var value = Cs.Symbol(new()
            {
                AsCall = true,
                Target = Cs.Symbol("GetSlot", new() { TypeArguments = new[] { NodeType(TypeName(property.Type)) } }),
                Arguments = Cs.Compound(Cs.Symbol("state"), Cs.Value(position))
            });
            Apply(root, name, typeName).Body(Cs.Tree(NodeIf(NullTest(Index(Cs.Symbol("state"),
                    Cs.Value(properties.Length + position)), true),
                access.Write(property, "result", value)))).End();
        }
        return names;
    }

    private static MethodBuilder<TypeBuilder<FileBuilder>> Apply(TypeBuilder<FileBuilder> root, string name,
        string type) => root.Method(Cs.Void, name).Private().Static().Parameter(NodeType(type), "result", "ref")
            .Parameter(Cs.Type("object").Nullable().Array(), "state");

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
        => root.Method(Cs.Void, name).Private().Static().Parameter(Cs.Type("object").Nullable().Array(), "state")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options");

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
        var method = kind switch
        {
            "write" => Writer(root, name, type),
            "read" => Reader(root, name, type),
            "apply" => Apply(root, name, type),
            _ => Validator(root, name)
        };
        CsNode[] args = kind switch
        {
            "write" => [Cs.Symbol("w"), Cs.Symbol("value"), Cs.Symbol("options"), Cs.Symbol("path")],
            "read" => [Cs.Symbol("entry"), Arg(ArgumentModifier.Ref, Cs.Symbol("result")), Cs.Symbol("state"),
                Cs.Symbol("options"), Cs.Symbol("path")],
            "apply" => [Arg(ArgumentModifier.Ref, Cs.Symbol("result")), Cs.Symbol("state")],
            _ => [Cs.Symbol("state"), Cs.Symbol("options")]
        };
        method.Body(Cs.Tree(methods.Select(item => NodeCall(item, args)).ToArray())).End();
    }

    private void EmitEntryReader(TypeBuilder<FileBuilder> root, INamedTypeSymbol type, string typeName,
        int index, bool parameterless)
    {
        var body = new List<CsNode>();
        CsNode[] args = [Cs.Symbol("entry"), Arg(ArgumentModifier.Ref, Cs.Symbol("result")), Cs.Symbol("state"),
            Cs.Symbol("options"),
            Cs.Symbol("path")];
        if (parameterless) body.Add(NodeCall($"ReadMembers_{index}", args));
        body.Add(NodeCall($"ReadFields_{index}", args));
        var extension = ExtensionMember(type);
        if (extension is not null)
            body.Add(NodeIf(Cs.Symbol(new()
            {
                Operator = CsOperator.Not,
                Parts =
                new CsNode[] { NodeCall($"IsKnown_{index}", NodeMember(Cs.Symbol("entry"), "Name"),
                    Cs.Symbol("options")) }
            }), NodeIf(ExtensionEnabled(extension), ExtensionWrite(extension)),
                NodeIf(NodeBinary(CsOperator.AndAlso, Cs.Symbol(new()
                {
                    Operator = CsOperator.Not,
                    Parts = new CsNode[] { ExtensionEnabled(extension) }
                }), RejectUnmapped(type)),
                    NodeThrow(NodeNew("JsonException")))));
        else
            body.Add(NodeIf(NodeBinary(CsOperator.AndAlso, RejectUnmapped(type), Cs.Symbol(new()
            {
                Operator = CsOperator.Not,
                Parts = new CsNode[] { NodeCall($"IsKnown_{index}", NodeMember(Cs.Symbol("entry"), "Name"),
                    Cs.Symbol("options")) }
            })), NodeThrow(NodeNew("JsonException"))));
        Reader(root, (parameterless ? "ReadEntry_" : "ReadRemaining_") + index, typeName)
            .Body(Cs.Tree(body.ToArray())).End();
    }

    private CsNode[] ExtensionWrite(ISymbol extension)
    {
        var name = NodeMember(Cs.Symbol("entry"), "Name");
        bool readOnly = MemberType(extension) is INamedTypeSymbol map && map.OriginalDefinition.ToDisplayString() ==
            "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>";
        CsNode read;
        if (IsNodeExtension(extension)) read = NodeCall("JsonNode.Parse", NodeCall("entry.Value.GetRawText"));
        else
        {
            var valueType = ((INamedTypeSymbol)MemberType(extension)).TypeArguments[1];
            read = valueType.SpecialType == SpecialType.System_Object ?
                NodeCall($"Read_{owner.Register(valueType)}", NodeMember(Cs.Symbol("entry"), "Value"),
                    Cs.Symbol("options"), Path(name)) : NodeCall("entry.Value.Clone");
        }
        if (readOnly)
        {
            string valueType = TypeName(((INamedTypeSymbol)MemberType(extension)).TypeArguments[1]);
            return [NodeVar("previousData", access.Read(extension, "result")),
                NodeVar("extensionData", NodeNew("System.Collections.Generic.Dictionary<string, " + valueType + ">")),
                NodeIf(NullTest(Cs.Symbol("previousData"), true), NodeEach("item", Cs.Symbol("previousData"),
                    NodeAssign(Index(Cs.Symbol("extensionData"), NodeMember(Cs.Symbol("item"), "Key")),
                        NodeMember(Cs.Symbol("item"), "Value")))),
                DuplicatePropertyEmitter.Check(NodeCall("extensionData.ContainsKey", name)),
                NodeAssign(Index(Cs.Symbol("extensionData"), name), read),
                access.Write(extension, "result", Cs.Symbol("extensionData"))];
        }
        return [NodeVar("extensionData", access.Read(extension, "result")),
            NodeIf(NullTest(Cs.Symbol("extensionData")),
                NodeAssign(Cs.Symbol("extensionData"), Cs.Symbol(new() { AsNew = true })),
                access.Write(extension, "result", Cs.Symbol("extensionData"))),
            DuplicatePropertyEmitter.Check(NodeCall("extensionData.ContainsKey", name)),
            NodeAssign(Index(Cs.Symbol("extensionData"), name), read)];
    }

    private static bool IsNodeExtension(ISymbol extension) => MemberType(extension)
        .WithNullableAnnotation(NullableAnnotation.None).ToDisplayString() == "System.Text.Json.Nodes.JsonObject";

    internal static CsNode ExtensionEnabled(ISymbol extension) => extension is IFieldSymbol field && !HasJsonInclude(field) ?
        OptionEnabled("IncludeFields") : Cs.Value(true);

    private static CsNode RejectUnmapped(INamedTypeSymbol type)
    {
        var attribute = type.GetAttributes().FirstOrDefault(item =>
            IsJsonAttribute(item, "JsonUnmappedMemberHandlingAttribute"));
        if (attribute is not null) return Cs.Value((int?)attribute.ConstructorArguments[0].Value == 1);
        return NodeBinary(CsOperator.Equal,
            Cs.Symbol("UnmappedMemberHandling", new() { Target = Cs.Symbol("options"), ConditionalAccess = true }),
            NodeMember(TypeRef("global::System.Text.Json.Serialization.JsonUnmappedMemberHandling"), "Disallow"));
    }

    private static void EmitKnownNames(TypeBuilder<FileBuilder> root, IPropertySymbol[] properties,
        IFieldSymbol[] fields, int index)
    {
        var names = new List<string>();
        foreach (var member in properties.Cast<ISymbol>().Concat(fields))
        {
            string method = $"KnownName_{index}_{names.Count}";
            names.Add(method);
            CsNode matches = NameMatches(Cs.Symbol("name"), JsonNameExpression(member));
            if (member is IFieldSymbol field && !HasJsonInclude(field))
                matches = NodeBinary(CsOperator.AndAlso, OptionEnabled("IncludeFields"), matches);
            Known(root, method).Body(Cs.Tree(Return(matches))).End();
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

    private static CsSymbol NameMatches(CsNode actual, CsNode expected) =>
        NodeCall("NamesEqual", actual, expected, Cs.Symbol("options"));

    private static CsSymbol Path(CsNode name) => NodeBinary(CsOperator.Add,
        NodeBinary(CsOperator.Add, Cs.Symbol("path"), Cs.Value(".")), name);

}

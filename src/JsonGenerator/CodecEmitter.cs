using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using Neemle.CSharpComposer.Rendering;
using static CodecApiEmitter;
using TypeKind = Microsoft.CodeAnalysis.TypeKind;

sealed class CodecEmitter
{
    private enum Shape { String, Number, Boolean, Bytes, Array, Nullable, List, Enum, Map, Object }

    private readonly List<ITypeSymbol> types = [];
    private readonly ObjectCodecEmitter objectEmitter;

    public CodecEmitter() => objectEmitter = new(this);
    private readonly HashSet<ITypeSymbol> checkedTypes = new(SymbolEqualityComparer.Default);

    public bool TryRegister(ITypeSymbol type, out int index)
    {
        type = type.WithNullableAnnotation(NullableAnnotation.None);
        if (!Supports(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default))) { index = -1; return false; }
        index = Register(type);
        return true;
    }

    public bool SupportsPopulate(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named || named.TypeKind is not (TypeKind.Class or TypeKind
            .Struct)) return false;
        if (ExtensionProperty(named) is not null || Fields(named).Any(f => f.Type.IsReferenceType && f.Type
            .SpecialType != SpecialType.System_String)) return false;
        return Properties(named).All(p => PopulateEmitter.CanHandleReadonly(p) || !p.Type.IsReferenceType
            || p.Type.SpecialType == SpecialType.System_String);
    }

    internal int Register(ITypeSymbol type)
    {
        type = type.WithNullableAnnotation(NullableAnnotation.None);
        int found = types.FindIndex(t => SymbolEqualityComparer.Default.Equals(t, type));
        if (found >= 0) return found;
        types.Add(type);
        return types.Count - 1;
    }

    private bool Supports(ITypeSymbol type, HashSet<ITypeSymbol> active)
    {
        type = type.WithNullableAnnotation(NullableAnnotation.None);
        if (checkedTypes.Contains(type) || !active.Add(type)) return true;
        if (type.SpecialType is SpecialType.System_String or SpecialType.System_Boolean ||
            NumericGetter(type.SpecialType) is not null)
        { checkedTypes.Add(type); return true; }
        if (type is IArrayTypeSymbol array) return Supports(array.ElementType, active);
        if (type is not INamedTypeSymbol named) return false;
        bool? collection = SupportsCollection(named, active);
        if (collection.HasValue) return collection.Value;
        if (!SupportsObject(named, active)) return false;
        checkedTypes.Add(type);
        return true;
    }

    private bool? SupportsCollection(INamedTypeSymbol named, HashSet<ITypeSymbol> active) => named switch
    {
        { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } => Supports(named.TypeArguments[0], active),
        _ when named.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>" =>
            Supports(named.TypeArguments[0], active),
        _ when named.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.Dictionary<TKey, TValue>" =>
            named.TypeArguments[0].SpecialType == SpecialType.System_String && Supports(named.TypeArguments[1], active),
        { TypeKind: TypeKind.Enum } => EnumEmitter.CanEmit(named),
        _ => null
    };

    private bool SupportsObject(INamedTypeSymbol named, HashSet<ITypeSymbol> active)
    {
        if (named.SpecialType != SpecialType.None || IsFrameworkType(named)) return false;
        if (named.TypeKind is not (TypeKind.Class or TypeKind.Struct) || named.IsAbstract || named
            .DeclaredAccessibility != Accessibility.Public)
            return false;
        if (named.GetAttributes().Any(a => a.AttributeClass?.ContainingNamespace.ToDisplayString() ==
            "System.Text.Json.Serialization")) return false;
        if (HierarchyMembers(named).Any(m => m.DeclaredAccessibility != Accessibility.Public &&
            m.GetAttributes().Any(a => IsJsonAttribute(a, "JsonIncludeAttribute") ||
                IsJsonAttribute(a, "JsonRequiredAttribute")))) return false;
        var members = Properties(named);
        if (members.Select(JsonName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != members.Length) return false;
        if (members.Any(p => UnsupportedAttributes(p) || !SupportsIgnoreCondition(p))) return false;
        if (members.Any(p => RequiresNumberParsing(p) && p.Type.SpecialType != SpecialType.System_Int32)) return false;
        if (members.Any(p => p.GetMethod is null || p.GetMethod.DeclaredAccessibility != Accessibility.Public ||
            !Supports(p.Type, active) || p.GetAttributes().Any(a => IsJsonAttribute(a, "JsonConverterAttribute"))))
            return false;
        if (Fields(named).Any(f => f.IsReadOnly || f.IsRequired || UnsupportedAttributes(f) ||
            RequiresNumberParsing(f) ||
            IgnoreCondition(f) is 2 or 3 || !Supports(f.Type, active))) return false;
        return SupportsExtension(named) && SupportsConstructor(named, members);
    }

    private static bool IsFrameworkType(INamedTypeSymbol type)
    {
        string name = type.ContainingAssembly.Identity.Name;
        return name == "mscorlib" || name == "System" || name.StartsWith("System.", StringComparison.Ordinal);
    }

    private static string? NumericGetter(SpecialType type) => IntegralGetter(type) ?? FloatingGetter(type);

    internal static string? IntegralGetter(SpecialType type) => type switch
    {
        SpecialType.System_Byte => "GetByte", SpecialType.System_SByte => "GetSByte",
        SpecialType.System_Int16 => "GetInt16", SpecialType.System_UInt16 => "GetUInt16",
        SpecialType.System_Int32 => "GetInt32", SpecialType.System_UInt32 => "GetUInt32",
        SpecialType.System_Int64 => "GetInt64", SpecialType.System_UInt64 => "GetUInt64",
        _ => null
    };

    private static string? FloatingGetter(SpecialType type) => type switch
    {
        SpecialType.System_Single => "GetSingle", SpecialType.System_Double => "GetDouble",
        SpecialType.System_Decimal => "GetDecimal", _ => null
    };

    private static Shape GetShape(ITypeSymbol type) => ScalarShape(type.SpecialType) ?? CompositeShape(type);

    private static Shape? ScalarShape(SpecialType type) => type switch
    {
        SpecialType.System_String => Shape.String,
        SpecialType.System_Boolean => Shape.Boolean,
        _ when NumericGetter(type) is not null => Shape.Number,
        _ => null
    };

    private static Shape CompositeShape(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte } => Shape.Bytes,
        IArrayTypeSymbol => Shape.Array,
        INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } => Shape.Nullable,
        INamedTypeSymbol named when named.OriginalDefinition.ToDisplayString() ==
            "System.Collections.Generic.List<T>" => Shape.List,
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => Shape.Enum,
        INamedTypeSymbol named when named.OriginalDefinition.ToDisplayString() ==
            "System.Collections.Generic.Dictionary<TKey, TValue>" => Shape.Map,
        _ => Shape.Object
    };

    private static bool SupportsExtension(INamedTypeSymbol named)
    {
        if (HierarchyMembers(named).OfType<IPropertySymbol>().Count(p => p.GetAttributes()
            .Any(a => IsJsonAttribute(a, "JsonExtensionDataAttribute"))) > 1) return false;
        var extension = ExtensionProperty(named);
        if (extension is not null && (extension.SetMethod is not { DeclaredAccessibility: Accessibility
            .Public, IsInitOnly: false } ||
            extension.Type is not INamedTypeSymbol extType || extType.OriginalDefinition.ToDisplayString()
                != "System.Collections.Generic.Dictionary<TKey, TValue>" ||
            extType.TypeArguments[0].SpecialType != SpecialType.System_String || extType.TypeArguments[1]
                .ToDisplayString() != "System.Text.Json.JsonElement")) return false;
        return true;
    }

    private static bool SupportsConstructor(INamedTypeSymbol named, IPropertySymbol[] members)
    {
        bool parameterless = named.InstanceConstructors.Any(c => c.DeclaredAccessibility == Accessibility
            .Public && c.Parameters.Length == 0);
        bool settable = members.All(p => p.SetMethod is { DeclaredAccessibility: Accessibility.Public,
            IsInitOnly: false } || PopulateEmitter.CanHandleReadonly(p));
        bool positional = Constructor(named, members) is not null;
        var marked = named.InstanceConstructors.Where(IsJsonConstructor).ToArray();
        if (marked.Length > 1 || marked.Length == 1 && (marked[0].DeclaredAccessibility != Accessibility.Public ||
            (marked[0].Parameters.Length == 0 ? !settable : !positional || !SymbolEqualityComparer.Default
                .Equals(marked[0], Constructor(named, members))))) return false;
        if (!(parameterless && settable) && !positional) return false;
        return true;
    }

    private static IEnumerable<ISymbol> HierarchyMembers(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null && current.SpecialType != SpecialType
            .System_Object; current = current.BaseType)
            foreach (var member in current.GetMembers()) yield return member;
    }

    internal static IPropertySymbol[] Properties(INamedTypeSymbol type) => HierarchyMembers(type)
        .OfType<IPropertySymbol>()
        .Where(p => !p.IsStatic && p.DeclaredAccessibility == Accessibility.Public && !p.IsIndexer && !IgnoreAlways(p)
            && !p.GetAttributes().Any(a => IsJsonAttribute(a, "JsonExtensionDataAttribute"))).ToArray();

    internal static IPropertySymbol? ExtensionProperty(INamedTypeSymbol type) => HierarchyMembers(type)
        .OfType<IPropertySymbol>()
        .FirstOrDefault(p => p.GetAttributes().Any(a => IsJsonAttribute(a, "JsonExtensionDataAttribute")));
    internal static IFieldSymbol[] Fields(INamedTypeSymbol type) => HierarchyMembers(type).OfType<IFieldSymbol>()
        .Where(f => !f.IsStatic && f.DeclaredAccessibility == Accessibility.Public && !IgnoreAlways(f) && f
            .GetAttributes().Any(a => IsJsonAttribute(a, "JsonIncludeAttribute"))).ToArray();

    internal static bool IsJsonConstructor(IMethodSymbol constructor) => constructor.GetAttributes().Any(a
        => IsJsonAttribute(a, "JsonConstructorAttribute"));
    private static IMethodSymbol? Constructor(INamedTypeSymbol type, IPropertySymbol[] props) =>
        type.InstanceConstructors.Where(c => !type.InstanceConstructors.Any(IsJsonConstructor) || IsJsonConstructor(c))
        .FirstOrDefault(c => c.DeclaredAccessibility == Accessibility.Public && c.Parameters.Length == props.Length &&
            c.Parameters.Select((p, i) => string.Equals(p.Name, props[i].Name, StringComparison.OrdinalIgnoreCase) &&
                SymbolEqualityComparer.Default.Equals(p.Type, props[i].Type)).All(x => x));

    public string Generate()
    {
        var document = Cs.Document(CsLanguageVersion.CSharp14).Nullable(NullableMode.Enable);
        foreach (string ns in new[] { "System", "System.IO", "System.Text", "System.Text.Json",
            "System.Globalization", "System.Threading", "System.Threading.Tasks",
            "System.Text.Json.Serialization", "System.Runtime.CompilerServices" }) document.Using(ns);
        var root = document.Class(GeneratedCodeLayout.GeneratedTypeName).Public().Static();
        var converter = root.Class("Converter").Public().Abstract().Generic("T");
        converter.Method(Cs.Type("T").Nullable(), "Read").Public().Abstract()
            .Parameter(Cs.Type("Utf8JsonReader"), "reader", "ref")
            .Parameter(Cs.Type("Type"), "typeToConvert")
            .Parameter(Cs.Type("JsonSerializerOptions"), "options").End();
        converter.Method(Cs.Type("void"), "Write").Public().Abstract()
            .Parameter(Cs.Type("Utf8JsonWriter"), "writer").Parameter(Cs.Type("T"), "value")
            .Parameter(Cs.Type("JsonSerializerOptions"), "options").End();
        converter.End();
        ExceptionEmitter.Emit(root);
        for (int i = 0; i < types.Count; i++) Emit(types[i], i, root);
        root.End();
        return document.Render();
    }

    private void Emit(ITypeSymbol type, int i, TypeBuilder<FileBuilder> root)
    {
        string name = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        EmitRegistration(i, name, root);
        CodecApiEmitter.EmitPublicApi(i, name, root);
        bool objectCodec = EmitWriter(type, i, name, root);
        EmitReader(type, i, name, root);
        if (objectCodec) objectEmitter.EmitObjectHelpers(root, (INamedTypeSymbol)type, i);
        if (type is INamedTypeSymbol enumeration && enumeration.TypeKind == TypeKind.Enum)
            EnumEmitter.EmitHelpers(root, enumeration, i);
    }

    private static void EmitRegistration(int i, string name, TypeBuilder<FileBuilder> root)
    {
        CsType converterType = Cs.Generic("Converter", NodeType(name));
        var holder = root.Class($"Holder_{i}").Private().Sealed();
        holder.Field(converterType.Nullable(), "Converter").Public().End();
        foreach (string field in new[] { "StringEnum", "Registered" })
            holder.Field(Cs.Type("bool"), field).Public().End();
        holder.End();
        CsType tableType = Cs.Generic("ConditionalWeakTable", Cs.Type("JsonSerializerOptions"),
            Cs.Type($"Holder_{i}"));
        root.Field(tableType, $"Table_{i}").Private().Static().ReadOnly()
            .Initialize(new CsExpression(ExpressionKind.New, "", []))
            .End();
        foreach (bool isEnum in new[] { false, true })
        {
            string methodName = isEnum ? $"RegisterEnum_{i}" : $"Register_{i}";
            var method = root.Method(Cs.Type("void"), methodName).Internal().Static()
                .Parameter(Cs.Type("JsonSerializerOptions"), "options");
            if (!isEnum) method.Parameter(converterType, "converter");
            var factory = Cs.Lambda(new CsExpression(ExpressionKind.New, "", []) { Type = Cs.Type($"Holder_{i}") },
                Cs.LambdaParameter("_"))
                with { IsStatic = true };
            var condition = Cs.Symbol(new() { Operator = CsOperator.Not,
                Parts = new CsNode[] { NodeMember(Cs.Symbol("holder"), "Registered") } });
            method.Body(Cs.Tree(NodeVar("holder", NodeCall($"Table_{i}.GetValue", Cs.Symbol("options"), factory)),
                NodeIf(condition, NodeAssign(NodeMember(Cs.Symbol("holder"), isEnum ? "StringEnum" : "Converter"),
                        isEnum ? Cs.Value(true) : Cs.Symbol("converter")),
                    NodeAssign(NodeMember(Cs.Symbol("holder"), "Registered"), Cs.Value(true))))).End();
        }
    }

    private bool EmitWriter(ITypeSymbol type, int i, string name, TypeBuilder<FileBuilder> root)
    {
        var body = new List<CsNode>();
        if (type.IsReferenceType)
            body.Add(NodeIf(NullTest(Cs.Symbol("value")), NodeCall("w.WriteNullValue"),
                Cs.Instruction(InstructionKind.Return)));
        body.Add(NodeIf(ConverterCondition(i), CustomWrite()));
        bool isObject = EmitWriteBody(type, i, body);
        root.Method(Cs.Void, $"Write_{i}").Private().Static().Parameter(Cs.Type("Utf8JsonWriter"), "w")
            .Parameter(type.IsReferenceType ? NodeType(name).Nullable() : NodeType(name), "value")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Parameter(Cs.Type("string"), "path")
            .Body(Cs.Tree(body.ToArray())).End();
        return isObject;
    }

    private static CsStatement CustomWrite() => Cs.Try(Cs.Body(
        Call("converter.Write", Cs.Id("w"),
            new CsExpression(ExpressionKind.NullForgiving, "", new[] { Cs.Id("value") }),
            Cs.Id("options")), Cs.Return()),
        new Values<CsCatch>([Cs.Catch(Cs.Type("NotSupportedException"),
            Cs.Body(Cs.Throw(Invoke("ConverterNotSupported", Cs.Id("error"), Cs.Id("path")))), "error")]));

    private bool EmitWriteBody(ITypeSymbol type, int i, List<CsNode> body)
    {
        switch (GetShape(type))
        {
            case Shape.String: body.Add(NodeCall("w.WriteStringValue", Cs.Symbol("value"))); break;
            case Shape.Number: body.Add(NodeCall("w.WriteNumberValue", Cs.Symbol("value"))); break;
            case Shape.Boolean: body.Add(NodeCall("w.WriteBooleanValue", Cs.Symbol("value"))); break;
            case Shape.Bytes: body.Add(NodeCall("w.WriteBase64StringValue", Cs.Symbol("value"))); break;
            case Shape.Array: SequenceWriter(body, ((IArrayTypeSymbol)type).ElementType); break;
            case Shape.Nullable: EmitNullableWriter(body, (INamedTypeSymbol)type); break;
            case Shape.List: SequenceWriter(body, ((INamedTypeSymbol)type).TypeArguments[0]); break;
            case Shape.Enum: EmitEnumWriter(body, i); break;
            case Shape.Map: EmitMapWriter(body, (INamedTypeSymbol)type); break;
            default: objectEmitter.EmitObjectWriter(body, (INamedTypeSymbol)type, i); return true;
        }
        return false;
    }

    private void EmitNullableWriter(List<CsNode> body, INamedTypeSymbol type)
    {
        body.Add(NodeIf(Cs.Symbol(new() { Operator = CsOperator.Not,
            Parts = new CsNode[] { NodeMember(Cs.Symbol("value"), "HasValue") } }), NodeCall("w.WriteNullValue"),
            Cs.Instruction(InstructionKind.Return)));
        body.Add(NodeCall($"Write_{Register(type.TypeArguments[0])}", Cs.Symbol("w"),
            NodeMember(Cs.Symbol("value"), "Value"), Cs.Symbol("options"), Cs.Symbol("path")));
    }

    private static void EmitEnumWriter(List<CsNode> body, int i)
    {
        body.Add(EnumOption(i));
        body.Add(NodeCall($"WriteEnum_{i}", Cs.Symbol("w"), Cs.Symbol("value"), Cs.Symbol("stringEnum")));
    }

    private void EmitMapWriter(List<CsNode> body, INamedTypeSymbol type)
    {
        body.Add(NodeCall("w.WriteStartObject"));
        var key = NodeMember(Cs.Symbol("item"), "Key");
        body.Add(NodeEach("item", Cs.Symbol("value"), NodeCall("w.WritePropertyName", key),
            NodeCall($"Write_{Register(type.TypeArguments[1])}", Cs.Symbol("w"), NodeMember(Cs.Symbol("item"), "Value"),
                Cs.Symbol("options"), NodeBinary(CsOperator.Add,
                    NodeBinary(CsOperator.Add, Cs.Symbol("path"), Cs.Value(".")), key))));
        body.Add(NodeCall("w.WriteEndObject"));
    }

    private void EmitReader(ITypeSymbol type, int i, string name, TypeBuilder<FileBuilder> root)
    {
        var body = new List<CsNode>();
        if (type.IsReferenceType || type is INamedTypeSymbol maybe && maybe.OriginalDefinition.SpecialType
            == SpecialType.System_Nullable_T)
            body.Add(NodeIf(NodeBinary(CsOperator.Equal, NodeMember(Cs.Symbol("e"), "ValueKind"),
                NodeMember(Cs.Symbol("JsonValueKind"), "Null")), Return(NonNull(DefaultValue()))));
        body.Add(NodeIf(NodeCall($"TryReadCustom_{i}", Cs.Symbol("e"), Cs.Symbol("options"), Cs.Symbol("path"),
            Arg(ArgumentModifier.Out, Cs.Symbol("customValue", new() { Operation = SymbolOperation.Declaration,
                Type = NodeType(name) }))), Return(Cs.Symbol("customValue"))));
        EmitReadBody(type, i, name, body);
        root.Method(NodeType(name), $"Read_{i}").Private().Static().Parameter(Cs.Type("JsonElement"), "e")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Parameter(Cs.Type("string"), "path")
            .Body(Cs.Tree(body.ToArray())).End();
        EmitCustomReader(i, name, root);
    }

    private static void EmitCustomReader(int i, string name, TypeBuilder<FileBuilder> root)
    {
        var custom = Cs.Tree(NodeVar("reader", NodeNew("Utf8JsonReader",
                NodeCall("Encoding.UTF8.GetBytes", NodeCall("e.GetRawText")))),
            NodeIf(Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { NodeCall("reader.Read") } }),
                NodeThrow(NodeNew("JsonException"))),
            Cs.Try(Cs.Body(Cs.Var("converted", Invoke("converter.Read", LegacyArgument(ArgumentModifier.Ref, "reader"),
                new CsExpression(ExpressionKind.TypeOf, "", []) { Type = NodeType(name) }, Cs.Id("options"))),
                Assign(Cs.Id("value"),
                    new CsExpression(ExpressionKind.NullForgiving, "", new[] { Cs.Id("converted") })),
                Cs.Return(Cs.Literal(true))),
                new Values<CsCatch>([Cs.Catch(Cs.Type("NotSupportedException"),
                Cs.Body(Cs.Throw(Invoke("ConverterNotSupported", Cs.Id("error"), Cs.Id("path")))), "error")])));
        root.Method(Cs.Bool, $"TryReadCustom_{i}").Private().Static().Parameter(Cs.Type("JsonElement"), "e")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Parameter(Cs.Type("string"), "path")
            .Parameter(NodeType(name), "value", "out")
            .Body(Cs.Tree(Cs.Instruction(InstructionKind.If, new()
                { Condition = ConverterCondition(i), Body = custom }),
                NodeAssign(Cs.Symbol("value"), NonNull(DefaultValue())), Return(Cs.Value(false)))).End();
    }

    private static CsSymbol ConverterCondition(int i) => NodeBinary(CsOperator.AndAlso,
        NodeBinary(CsOperator.AndAlso, NullTest(Cs.Symbol("options"), true),
            NodeCall($"Table_{i}.TryGetValue", Cs.Symbol("options"), OutVar("custom"))),
        Cs.Symbol(new() { AsIs = true, Value = NodeMember(Cs.Symbol("custom"), "Converter"),
            Pattern = new CsPattern(PatternKind.Recursive) { HasProperties = true, Name = "converter" } }));

    private void EmitReadBody(ITypeSymbol type, int i, string name, List<CsNode> body)
    {
        switch (GetShape(type))
        {
            case Shape.String: body.Add(Return(NonNull(NodeCall("e.GetString")))); break;
            case Shape.Number: EmitNumberReader(body, NumericGetter(type.SpecialType)!); break;
            case Shape.Boolean: body.Add(Return(NodeCall("e.GetBoolean"))); break;
            case Shape.Bytes: body.Add(Return(NodeCall("e.GetBytesFromBase64"))); break;
            case Shape.Array: EmitArrayReader(body, (IArrayTypeSymbol)type); break;
            case Shape.Nullable:
                int index = Register(((INamedTypeSymbol)type).TypeArguments[0]);
                body.Add(Return(NodeCall($"Read_{index}", Cs.Symbol("e"), Cs.Symbol("options"), Cs.Symbol("path"))));
                break;
            case Shape.List: EmitListReader(body, name, (INamedTypeSymbol)type); break;
            case Shape.Enum: body.Add(EnumOption(i));
                body.Add(Return(NodeCall($"ReadEnum_{i}", Cs.Symbol("e"), Cs.Symbol("stringEnum")))); break;
            case Shape.Map: EmitMapReader(body, name, (INamedTypeSymbol)type); break;
            default: objectEmitter.EmitObjectReader(body, (INamedTypeSymbol)type, i); break;
        }
    }

    private static CsSymbol EnumOption(int i) => Cs.Symbol("stringEnum", new() { Type = Cs.Bool,
        Value = NodeBinary(CsOperator.AndAlso, NodeBinary(CsOperator.AndAlso, NullTest(Cs.Symbol("options"), true),
            NodeCall($"Table_{i}.TryGetValue", Cs.Symbol("options"), OutVar("entry"))),
            NodeMember(Cs.Symbol("entry"), "StringEnum")) });

    private void EmitListReader(List<CsNode> body, string name, INamedTypeSymbol type)
    {
        body.Add(NodeVar("list", NodeNew(name)));
        body.Add(NodeVar("index", Cs.Value(0)));
        body.Add(ReadSequence(Register(type.TypeArguments[0])));
        body.Add(Return(Cs.Symbol("list")));
    }

    private static void EmitNumberReader(List<CsNode> body, string getter)
    {
        var catches = new[] { "FormatException", "InvalidOperationException", "OverflowException" }
            .Select(type => Cs.Catch(NodeType(type),
                Cs.Body(Cs.Throw(Invoke("InvalidScalar", Cs.Id("error"), Cs.Id("path")))), "error"));
        body.Add(Cs.Try(Cs.Body(Cs.Return(Invoke($"e.{getter}"))), new Values<CsCatch>(catches)));
    }

    private void EmitArrayReader(List<CsNode> body, IArrayTypeSymbol array)
    {
        string element = TypeName(array.ElementType);
        body.Add(NodeVar("list", NodeNew($"System.Collections.Generic.List<{element}>")));
        body.Add(NodeVar("index", Cs.Value(0)));
        body.Add(ReadSequence(Register(array.ElementType)));
        body.Add(Return(NodeCall("list.ToArray")));
    }

    private void EmitMapReader(List<CsNode> body, string name, INamedTypeSymbol map)
    {
        body.Add(NodeVar("map", NodeNew(name)));
        var key = NodeMember(Cs.Symbol("v"), "Name");
        var path = NodeBinary(CsOperator.Add, NodeBinary(CsOperator.Add, Cs.Symbol("path"), Cs.Value(".")), key);
        var read = NodeCall($"Read_{Register(map.TypeArguments[1])}", NodeMember(Cs.Symbol("v"), "Value"),
            Cs.Symbol("options"), path);
        body.Add(NodeEach("v", NodeCall("e.EnumerateObject"),
            NodeAssign(Index(Cs.Symbol("map"), NodeMember(Cs.Symbol("v"), "Name")), read)));
        body.Add(Return(Cs.Symbol("map")));
    }

    private void SequenceWriter(List<CsNode> body, ITypeSymbol element)
    {
        body.Add(NodeCall("w.WriteStartArray"));
        body.Add(NodeVar("index", Cs.Value(0)));
        body.Add(NodeEach("item", Cs.Symbol("value"), NodeCall($"Write_{Register(element)}",
            Cs.Symbol("w"), Cs.Symbol("item"), Cs.Symbol("options"), SequencePath())));
        body.Add(NodeCall("w.WriteEndArray"));
    }

    private static CsInstruction ReadSequence(int index) => NodeEach("v", NodeCall("e.EnumerateArray"),
        NodeCall("list.Add", NodeCall($"Read_{index}", Cs.Symbol("v"), Cs.Symbol("options"), SequencePath())));

    private static CsSymbol SequencePath() => NodeBinary(CsOperator.Add,
        NodeBinary(CsOperator.Add, NodeBinary(CsOperator.Add, Cs.Symbol("path"), Cs.Value("[")),
            Cs.PostIncrement(Cs.Id("index"))), Cs.Value("]"));

    private static CsStatement Call(string method, params CsExpression[] arguments) => Cs.Statement(
        Invoke(method, arguments));

    private static CsStatement Assign(CsExpression target, CsExpression value) =>
        Cs.Statement(Cs.Assign(target, value));

    internal static string TypeName(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    private static CsExpression LegacyArgument(ArgumentModifier modifier, string name) =>
        new(ExpressionKind.Argument, "", new[] { Cs.Id(name) }) { ArgumentModifier = modifier };
    private static string JsonName(ISymbol p) => p.GetAttributes()
        .FirstOrDefault(a => IsJsonAttribute(a, "JsonPropertyNameAttribute"))?.ConstructorArguments[0].Value
        as string ?? p.Name;
    internal static CsNode JsonNameExpression(ISymbol p)
    {
        if (p.GetAttributes().Any(a => IsJsonAttribute(a, "JsonPropertyNameAttribute")))
            return Cs.Value(JsonName(p));
        var policy = Cs.Symbol("PropertyNamingPolicy", new() { Target = Cs.Symbol("options"),
            ConditionalAccess = true });
        var convert = Cs.Symbol("ConvertName", new() { Target = policy, ConditionalAccess = true,
            AsCall = true, Arguments = Cs.Compound(Cs.Value(p.Name)) });
        return NodeBinary(CsOperator.Coalesce, convert, Cs.Value(p.Name));
    }
    internal static bool IsRequired(IPropertySymbol p) => p.IsRequired ||
        p.GetAttributes().Any(a => IsJsonAttribute(a, "JsonRequiredAttribute"));
    internal static int IgnoreCondition(ISymbol p)
    {
        var attr = p.GetAttributes().FirstOrDefault(a => IsJsonAttribute(a, "JsonIgnoreAttribute"));
        if (attr is null) return 0;
        return attr.NamedArguments.FirstOrDefault(a => a.Key == "Condition").Value.Value is int value ? value : 1;
    }
    private static bool IgnoreAlways(ISymbol p) => IgnoreCondition(p) == 1;
    private static bool SupportsIgnoreCondition(IPropertySymbol p) => IgnoreCondition(p) switch
    {
        0 or 1 => true,
        2 => p.Type.IsReferenceType || NumericGetter(p.Type.SpecialType) is not null ||
            p.Type.SpecialType == SpecialType.System_Boolean ||
            p.Type is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType
                .System_Nullable_T,
        3 => p.Type.IsReferenceType || p.Type is INamedTypeSymbol nullable && nullable.OriginalDefinition
            .SpecialType == SpecialType.System_Nullable_T,
        _ => false
    };
    internal static CsNode NonDefaultExpression(IPropertySymbol p)
    {
        var value = Cs.Symbol("value_" + p.Name);
        if (p.Type.IsReferenceType) return NullTest(value, true);
        if (NumericGetter(p.Type.SpecialType) is not null) return NodeBinary(CsOperator.NotEqual, value, Cs.Value(0));
        return p.Type.SpecialType == SpecialType.System_Boolean ? value : NodeMember(value, "HasValue");
    }
    private static bool UnsupportedAttributes(ISymbol member) => member.GetAttributes().Any(a =>
        a.AttributeClass?.ContainingNamespace.ToDisplayString() == "System.Text.Json.Serialization" &&
        (a.AttributeClass.Name is not ("JsonPropertyNameAttribute" or "JsonRequiredAttribute" or
            "JsonIgnoreAttribute" or "JsonNumberHandlingAttribute" or "JsonIncludeAttribute" or
                "JsonObjectCreationHandlingAttribute") ||
         a.AttributeClass.Name == "JsonNumberHandlingAttribute" && (a.ConstructorArguments.Length != 1 || a
             .ConstructorArguments[0].Value is not (0 or 1))));

    internal static bool RequiresNumberParsing(ISymbol property) => property.GetAttributes().Any(attribute =>
        IsJsonAttribute(attribute, "JsonNumberHandlingAttribute") &&
        (int?)attribute.ConstructorArguments[0].Value == 1);

    private static bool IsJsonAttribute(AttributeData attribute, string name) =>
        attribute.AttributeClass?.Name == name &&
        attribute.AttributeClass.ContainingNamespace.ToDisplayString() == "System.Text.Json.Serialization";
}

using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using Neemle.CSharpComposer.Rendering;
using static Neemle.Json.Generator.CodecApiEmitter;
using TypeKind = Microsoft.CodeAnalysis.TypeKind;

namespace Neemle.Json.Generator;

sealed class CodecEmitter
{
    private enum Shape
    {
        String, Number, Boolean, FrameworkScalar, DomValue, Bytes, Array, Nullable, Collection, Enum, Map, Object,
        Dynamic, WideNumber, Memory
    }

    private readonly List<ITypeSymbol> types = [];
    private readonly HashSet<int> nodeValues = [];
    private readonly HashSet<int> asyncSequences = [];
    private readonly HashSet<int> asyncWriters = [];
    private readonly HashSet<int> mapKeys = [];
    private readonly HashSet<int> enumDisplays = [];
    private bool allEnumFactory;
    private bool contextOptions;
    private bool converterFactories;
    private bool frameworkFactoryAdapter;
    private IEnumerable<ITypeSymbol> dynamicCandidates = [];
    private readonly ObjectCodecEmitter objectEmitter;

    private readonly ITypeSymbol objectType;

    public CodecEmitter(ITypeSymbol objectType)
    {
        this.objectType = objectType;
        objectEmitter = new(this);
    }
    public void SetDynamicCandidates(IEnumerable<ITypeSymbol> candidates) => dynamicCandidates = candidates;
    internal IEnumerable<ITypeSymbol> KnownTypes => dynamicCandidates;
    internal IEnumerable<ITypeSymbol> RegisteredTypes => types;
    public void RegisterNodeValue(int index) => nodeValues.Add(index);
    public void RegisterAsyncSequence(int index) => asyncSequences.Add(index);
    public void RegisterAsyncWriter(int index) => asyncWriters.Add(index);
    public void RegisterAllEnums() => allEnumFactory = true;
    public void RegisterContextOptions() => contextOptions = true;
    public void RegisterConverterFactories() => converterFactories = true;
    public void RegisterFrameworkFactoryAdapter() => frameworkFactoryAdapter = true;
    public bool TryRegisterEnumDisplay(ITypeSymbol type, out int index)
    {
        if (ConverterAttributeEmitter.Converter(type, type) is not null) { index = -1; return false; }
        if (!TryRegister(type, out index)) return false;
        enumDisplays.Add(index);
        return true;
    }
    public int RegisterMapKey(ITypeSymbol type)
    {
        int index = Register(type);
        mapKeys.Add(index);
        return index;
    }
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
        if (Fields(named).Any(f => f.Type.IsReferenceType && f.Type
            .SpecialType != SpecialType.System_String && PopulateEmitter.CollectionElementType(f) is null &&
            PopulateEmitter.MapTypes(f) is null && PopulateEmitter.ObjectType(f) is null &&
            ConverterAttributeEmitter.Converter(f, f.Type) is null &&
            ConverterAttributeEmitter.Converter(f.Type, f.Type) is null)) return false;
        return Properties(named).All(p => PopulateEmitter.CanHandleReadonly(p) || PopulateEmitter.CollectionElementType(p) is not null ||
            PopulateEmitter.MapTypes(p) is not null || PopulateEmitter.ObjectType(p) is not null ||
            PopulateEmitter.IsReadOnlyEnumerable(p.Type) ||
            ConverterAttributeEmitter.Converter(p, p.Type) is not null ||
            ConverterAttributeEmitter.Converter(p.Type, p.Type) is not null ||
            !p.Type.IsReferenceType
            || p.Type.SpecialType == SpecialType.System_String);
    }

    internal int Register(ITypeSymbol type)
    {
        type = type.WithNullableAnnotation(NullableAnnotation.None);
        int found = types.FindIndex(t => SymbolEqualityComparer.Default.Equals(t, type));
        if (found >= 0) return found;
        types.Add(type);
        if (type is INamedTypeSymbol { IsAnonymousType: true }) SetAnonymousName(type, "GeneratedAnonymous_" +
            (types.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return types.Count - 1;
    }

    private bool Supports(ITypeSymbol type, HashSet<ITypeSymbol> active)
    {
        type = type.WithNullableAnnotation(NullableAnnotation.None);
        if (checkedTypes.Contains(type) || !active.Add(type)) return true;
        try
        {
            if (ConverterAttributeEmitter.Converter(type, type) is not null || DomValue.Kind(type) is not null ||
                FrameworkScalar.Getter(type) is not null || type.SpecialType is SpecialType.System_String or
                SpecialType.System_Boolean or SpecialType.System_Object || NumericGetter(type.SpecialType) is not null ||
                WideNumberEmitter.IsWide(type))
            { if (active.Count == 1) checkedTypes.Add(type); return true; }
            if (type is IArrayTypeSymbol array) return array.Rank == 1 && Supports(array.ElementType, active);
            if (type is not INamedTypeSymbol named) return false;
            bool? collection = SupportsCollection(named, active);
            if (collection.HasValue) return collection.Value;
            if (!SupportsObject(named, active)) return false;
            if (active.Count == 1) checkedTypes.Add(type);
            return true;
        }
        finally { active.Remove(type); }
    }

    private bool? SupportsCollection(INamedTypeSymbol named, HashSet<ITypeSymbol> active) => named switch
    {
        { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } => Supports(named.TypeArguments[0], active),
        _ when IsMemory(named) => Supports(named.TypeArguments[0], active),
        _ when FrameworkCollection.ImmutableSequenceFactory(named) is not null =>
            Supports(named.TypeArguments[0], active),
        _ when FrameworkCollection.AddMethod(named) is not null =>
            Supports(FrameworkCollection.ElementType(named, objectType), active),
        _ when FrameworkCollection.IsMap(named) =>
            (FrameworkCollection.SupportsKey(FrameworkCollection.MapKeyType(named)) ||
                Supports(FrameworkCollection.MapKeyType(named), active)) &&
                Supports(FrameworkCollection.MapValueType(named), active),
        { TypeKind: TypeKind.Enum } => EnumEmitter.CanEmit(named),
        _ => null
    };

    private bool SupportsObject(INamedTypeSymbol named, HashSet<ITypeSymbol> active)
    {
        if (!IsKeyValuePair(named) && (named.SpecialType != SpecialType.None || IsFrameworkType(named))) return false;
        var variants = PolymorphismEmitter.Variants(named);
        if (PolymorphismEmitter.HasContract(named) && variants is null) return false;
        if (named.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Interface) ||
            (named.IsAbstract || named.TypeKind == TypeKind.Interface) && variants is null ||
            named.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
            return false;
        if (named.GetAttributes().Any(a => a.AttributeClass?.ContainingNamespace.ToDisplayString() ==
            "System.Text.Json.Serialization" && a.AttributeClass.Name is not
                ("JsonUnmappedMemberHandlingAttribute" or "JsonNumberHandlingAttribute" or
                    "JsonDerivedTypeAttribute" or "JsonPolymorphicAttribute" or "JsonObjectCreationHandlingAttribute")))
            return false;
        if (variants is not null && variants.Any(variant =>
            ConverterAttributeEmitter.Converter(variant.Type, variant.Type) is not null || !Supports(variant.Type, active)))
            return false;
        if (NumberHandling(named) is int numberHandling && (numberHandling & ~7) != 0) return false;
        if (HierarchyMembers(named).Any(m => m.DeclaredAccessibility != Accessibility.Public &&
            !(m is IPropertySymbol property && IncludedAccessible(property)) &&
            !(m is IFieldSymbol field && IncludedAccessible(field)) &&
            m.GetAttributes().Any(a => IsJsonAttribute(a, "JsonIncludeAttribute") ||
                IsJsonAttribute(a, "JsonRequiredAttribute")))) return false;
        var members = Properties(named);
        if (members.Any(p => UnsupportedAttributes(p) || !SupportsIgnoreCondition(p, p.Type))) return false;
        if (members.Any(p => NumberHandling(p) is not null && !NumberHandlingEmitter.CanApply(p.Type))) return false;
        if (members.Any(p => !CanRead(p) ||
            !Supports(p.Type, active) && ConverterAttributeEmitter.Converter(p, p.Type) is null ||
            !ConverterAttributeEmitter.IsSupported(p, p.Type)))
            return false;
        if (Fields(named).Any(f => UnsupportedAttributes(f) ||
            !ConverterAttributeEmitter.IsSupported(f, f.Type) ||
            NumberHandling(f) is not null && !NumberHandlingEmitter.CanApply(f.Type) ||
            !SupportsIgnoreCondition(f, f.Type) || !Supports(f.Type, active) &&
            ConverterAttributeEmitter.Converter(f, f.Type) is null)) return false;
        return SupportsExtension(named) && (named.IsAbstract || named.TypeKind == TypeKind.Interface ||
            SupportsConstructor(named, members));
    }

    private static bool IsFrameworkType(INamedTypeSymbol type)
    {
        string name = type.ContainingAssembly.Identity.Name;
        return name == "mscorlib" || name == "System" || name.StartsWith("System.", StringComparison.Ordinal);
    }

    internal static bool IsKeyValuePair(INamedTypeSymbol type) => type.OriginalDefinition.ToDisplayString() ==
        "System.Collections.Generic.KeyValuePair<TKey, TValue>";

    internal static bool IsMemory(INamedTypeSymbol type) => type.OriginalDefinition.ToDisplayString() is
        "System.Memory<T>" or "System.ReadOnlyMemory<T>";

    internal static string? NumericGetter(SpecialType type) => IntegralGetter(type) ?? FloatingGetter(type);

    internal static string? IntegralGetter(SpecialType type) => type switch
    {
        SpecialType.System_Byte => "GetByte",
        SpecialType.System_SByte => "GetSByte",
        SpecialType.System_Int16 => "GetInt16",
        SpecialType.System_UInt16 => "GetUInt16",
        SpecialType.System_Int32 => "GetInt32",
        SpecialType.System_UInt32 => "GetUInt32",
        SpecialType.System_Int64 => "GetInt64",
        SpecialType.System_UInt64 => "GetUInt64",
        _ => null
    };

    private static string? FloatingGetter(SpecialType type) => type switch
    {
        SpecialType.System_Single => "GetSingle",
        SpecialType.System_Double => "GetDouble",
        SpecialType.System_Decimal => "GetDecimal",
        _ => null
    };

    private static Shape GetShape(ITypeSymbol type) => WideNumberEmitter.IsWide(type) ? Shape.WideNumber :
        DomValue.Kind(type) is not null ? Shape.DomValue :
        FrameworkScalar.Getter(type) is not null ? Shape.FrameworkScalar : ScalarShape(type.SpecialType) ??
            CompositeShape(type);

    private static Shape? ScalarShape(SpecialType type) => type switch
    {
        SpecialType.System_String => Shape.String,
        SpecialType.System_Boolean => Shape.Boolean,
        SpecialType.System_Object => Shape.Dynamic,
        _ when NumericGetter(type) is not null => Shape.Number,
        _ => null
    };

    private static Shape CompositeShape(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte } => Shape.Bytes,
        IArrayTypeSymbol => Shape.Array,
        INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } => Shape.Nullable,
        INamedTypeSymbol named when IsMemory(named) => Shape.Memory,
        INamedTypeSymbol named when FrameworkCollection.ImmutableSequenceFactory(named) is not null => Shape.Collection,
        INamedTypeSymbol named when FrameworkCollection.AddMethod(named) is not null => Shape.Collection,
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => Shape.Enum,
        INamedTypeSymbol named when FrameworkCollection.IsMap(named) => Shape.Map,
        _ => Shape.Object
    };

    private static bool SupportsExtension(INamedTypeSymbol named)
    {
        if (HierarchyMembers(named).Where(member => member is IPropertySymbol or IFieldSymbol).Count(p => p.GetAttributes()
            .Any(a => IsJsonAttribute(a, "JsonExtensionDataAttribute"))) > 1) return false;
        var extension = ExtensionMember(named);
        if (extension is IPropertySymbol property && (!CanRead(property) || !CanSet(property) ||
            property.DeclaredAccessibility != Accessibility.Public && !IncludedAccessible(property))) return false;
        if (extension is IFieldSymbol field && (field.IsReadOnly || field.DeclaredAccessibility != Accessibility.Public &&
            !IncludedAccessible(field))) return false;
        if (extension is not null && MemberType(extension).WithNullableAnnotation(NullableAnnotation.None).ToDisplayString() ==
            "System.Text.Json.Nodes.JsonObject") return true;
        if (extension is not null && (MemberType(extension) is not INamedTypeSymbol extType || extType.OriginalDefinition.ToDisplayString()
                is not ("System.Collections.Generic.Dictionary<TKey, TValue>" or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>") ||
            extType.TypeArguments[0].SpecialType != SpecialType.System_String ||
            (extType.TypeArguments[1].ToDisplayString() != "System.Text.Json.JsonElement" &&
                extType.TypeArguments[1].SpecialType != SpecialType.System_Object))) return false;
        return true;
    }

    private static bool SupportsConstructor(INamedTypeSymbol named, IPropertySymbol[] members)
    {
        bool parameterless = named.InstanceConstructors.Any(c => AccessibleConstructor(c) && c.Parameters.Length == 0);
        bool settable = members.All(p => CanSet(p) || PopulateEmitter.CanHandleReadonly(p));
        bool positional = Constructor(named, members) is not null;
        var marked = named.InstanceConstructors.Where(IsJsonConstructor).ToArray();
        if (marked.Length > 1 || marked.Length == 1 && (!AccessibleConstructor(marked[0]) ||
            (marked[0].Parameters.Length == 0 ? !settable : !positional || !SymbolEqualityComparer.Default
                .Equals(marked[0], Constructor(named, members))))) return false;
        if (!(parameterless && settable) && !positional) return false;
        return true;
    }

    internal static IEnumerable<ISymbol> HierarchyMembers(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null && current.SpecialType != SpecialType
            .System_Object; current = current.BaseType)
            foreach (var member in current.GetMembers()) yield return member;
    }

    internal static IPropertySymbol[] Properties(INamedTypeSymbol type) => HierarchyMembers(type)
        .OfType<IPropertySymbol>()
        .Where(p => !p.IsStatic && (p.DeclaredAccessibility == Accessibility.Public || IncludedAccessible(p)) &&
            !p.IsIndexer && !IgnoreAlways(p)
            && !p.GetAttributes().Any(a => IsJsonAttribute(a, "JsonExtensionDataAttribute"))).ToArray();

    private static bool IncludedAccessible(ISymbol property) => property.GetAttributes().Any(attribute =>
        IsJsonAttribute(attribute, "JsonIncludeAttribute")) && property.Locations.Any(location => location.IsInSource);

    private static bool IncludedAccessor(IPropertySymbol property, IMethodSymbol? accessor) => accessor is not null &&
        property.GetAttributes().Any(attribute => IsJsonAttribute(attribute, "JsonIncludeAttribute")) &&
        accessor.Locations.Any(location => location.IsInSource);

    internal static bool CanRead(IPropertySymbol property) => property.GetMethod?.DeclaredAccessibility ==
        Accessibility.Public || IncludedAccessor(property, property.GetMethod);

    internal static bool CanSet(IPropertySymbol property) => property.SetMethod is { } setter &&
        (setter.DeclaredAccessibility == Accessibility.Public || IncludedAccessor(property, setter));

    internal static ISymbol? ExtensionMember(INamedTypeSymbol type) => HierarchyMembers(type)
        .Where(member => member is IPropertySymbol or IFieldSymbol)
        .FirstOrDefault(p => p.GetAttributes().Any(a => IsJsonAttribute(a, "JsonExtensionDataAttribute")));
    internal static ITypeSymbol MemberType(ISymbol member) => member switch
    {
        IPropertySymbol property => property.Type,
        IFieldSymbol field => field.Type,
        _ => throw new InvalidOperationException("Unsupported JSON member.")
    };
    internal static IFieldSymbol[] Fields(INamedTypeSymbol type) => HierarchyMembers(type).OfType<IFieldSymbol>()
        .Where(f => !f.IsStatic && (f.DeclaredAccessibility == Accessibility.Public || IncludedAccessible(f)) && !IgnoreAlways(f) &&
            !f.GetAttributes().Any(attribute => IsJsonAttribute(attribute, "JsonExtensionDataAttribute")) && f
            .GetAttributes().Any(a => IsJsonAttribute(a, "JsonIncludeAttribute"))).ToArray();

    internal static IFieldSymbol[] OptionalFields(INamedTypeSymbol type) => HierarchyMembers(type)
        .OfType<IFieldSymbol>().Where(f => !f.IsStatic && f.DeclaredAccessibility == Accessibility.Public &&
            !f.GetAttributes().Any(attribute => IsJsonAttribute(attribute, "JsonExtensionDataAttribute")) &&
            !IgnoreAlways(f) && !HasJsonInclude(f)).ToArray();

    internal static bool HasJsonInclude(IFieldSymbol field) => field.GetAttributes()
        .Any(attribute => IsJsonAttribute(attribute, "JsonIncludeAttribute"));

    internal bool SupportsOptionalField(IFieldSymbol field) => !UnsupportedAttributes(field) &&
        ConverterAttributeEmitter.IsSupported(field, field.Type) &&
        (NumberHandling(field) is null || NumberHandlingEmitter.CanApply(field.Type)) &&
        SupportsIgnoreCondition(field, field.Type) &&
        (Supports(field.Type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)) ||
            ConverterAttributeEmitter.Converter(field, field.Type) is not null);

    internal static bool IsJsonConstructor(IMethodSymbol constructor) => constructor.GetAttributes().Any(a
        => IsJsonAttribute(a, "JsonConstructorAttribute"));
    internal static bool AccessibleConstructor(IMethodSymbol constructor) => constructor.DeclaredAccessibility ==
        Accessibility.Public || constructor.Locations.Any(location => location.IsInSource) && IsJsonConstructor(constructor);
    internal static IMethodSymbol? Constructor(INamedTypeSymbol type, IPropertySymbol[] props)
    {
        if (IsKeyValuePair(type)) return type.InstanceConstructors.FirstOrDefault(constructor =>
            constructor.DeclaredAccessibility == Accessibility.Public && constructor.Parameters.Length == 2);
        var marked = type.InstanceConstructors.Where(IsJsonConstructor).ToArray();
        var constructors = marked.Length > 0 ? marked : type.InstanceConstructors
            .Where(constructor => constructor.DeclaredAccessibility == Accessibility.Public).ToArray();
        if (constructors.Length != 1) return null;
        var constructor = constructors[0];
        if (!AccessibleConstructor(constructor) || constructor.Parameters.Length == 0) return null;
        return constructor.Parameters.All(parameter => props.Any(property => Matches(parameter, property))) &&
            props.All(property => CanSet(property) || constructor.Parameters.Any(parameter => Matches(parameter, property))) ?
            constructor : null;
    }

    private static bool Matches(IParameterSymbol parameter, IPropertySymbol property) =>
        string.Equals(parameter.Name, property.Name, StringComparison.OrdinalIgnoreCase) &&
        SymbolEqualityComparer.Default.Equals(parameter.Type, property.Type);

    public string Generate()
    {
        var document = Cs.Document(CsLanguageVersion.CSharp14).Nullable(NullableMode.Enable);
        foreach (string ns in new[] { "System", "System.IO", "System.Text", "System.Text.Json", "System.Text.Json.Nodes",
            "System.Globalization", "System.Threading", "System.Threading.Tasks",
            "System.Text.Json.Serialization", "System.Text.Json.Serialization.Metadata",
            "System.Runtime.CompilerServices" }) document.Using(ns);
        var root = document.Class(GeneratedCodeLayout.GeneratedTypeName).Public().Static();
        StaticMetadataEmitter.Emit(root);
        ConverterFactoryEmitter.EmitBase(root);
        ConverterFactoryEmitter.EmitCollection(root);
        if (frameworkFactoryAdapter)
            root.Method(Cs.Type("JsonConverter"), "WrapFactory").Internal().Static()
                .Parameter(Cs.Type("ConverterFactory"), "factory")
                .Body(Cs.Tree(Return(NodeCall("BridgeWrapFactory", Cs.Symbol("factory"))))).End();
        root.Method(Cs.Type("TResult"), "Project").Internal().Static().Generic("TValue").Generic("TResult")
            .Parameter(Cs.Type("TValue"), "value").Parameter(Cs.Generic("Func", Cs.Type("TValue"),
                Cs.Type("TResult")), "projection").Body(Cs.Tree(Return(NodeCall("projection", Cs.Symbol("value"))))).End();
        root.Method(Cs.Type("T"), "InvalidContract").Internal().Static().Generic("T")
            .Parameter(Cs.Type("string"), "json")
            .Body(Cs.Tree(NodeThrow(NodeNew("InvalidOperationException")))).End();
        root.Method(Cs.Type("T"), "InvalidPopulateContract").Internal().Static().Generic("T")
            .Parameter(Cs.Type("string"), "json")
            .Body(Cs.Tree(NodeThrow(NodeNew("NotSupportedException")))).End();
        root.Method(Cs.Type("string"), "InvalidEncodeContract").Internal().Static().Generic("T")
            .Parameter(Cs.Type("T"), "value")
            .Body(Cs.Tree(NodeThrow(NodeNew("InvalidOperationException")))).End();
        root.Method(Cs.Void, "ThrowInvalidObjectContract").Private().Static()
            .Body(Cs.Tree(NodeThrow(NodeNew("InvalidOperationException")))).End();
        var converter = root.Class("Converter").Public().Abstract().Generic("T");
        converter.Property(Cs.Bool, "HandleNull").Public().Virtual().Expression(Cs.Literal(false)).End();
        converter.Method(Cs.Bool, "CanConvert").Public().Virtual().Parameter(Cs.Type("Type"), "typeToConvert")
            .Body(Cs.Tree(Return(NodeBinary(CsOperator.Equal, Cs.Symbol("typeToConvert"), TypeOf("T"))))).End();
        converter.Method(Cs.Type("T").Nullable(), "Read").Public().Abstract()
            .Parameter(Cs.Type("Utf8JsonReader"), "reader", "ref")
            .Parameter(Cs.Type("Type"), "typeToConvert")
            .Parameter(Cs.Type("JsonSerializerOptions"), "options").End();
        converter.Method(Cs.Type("void"), "Write").Public().Abstract()
            .Parameter(Cs.Type("Utf8JsonWriter"), "writer").Parameter(Cs.Type("T"), "value")
            .Parameter(Cs.Type("JsonSerializerOptions"), "options").End();
        converter.Method(Cs.Type("T"), "ReadAsPropertyName").Public().Virtual()
            .Parameter(Cs.Type("Utf8JsonReader"), "reader", "ref").Parameter(Cs.Type("Type"), "typeToConvert")
            .Parameter(Cs.Type("JsonSerializerOptions"), "options")
            .Body(Cs.Tree(NodeThrow(NodeNew("NotSupportedException")))).End();
        converter.Method(Cs.Void, "WriteAsPropertyName").Public().Virtual()
            .Parameter(Cs.Type("Utf8JsonWriter"), "writer").Parameter(Cs.Type("T"), "value")
            .Parameter(Cs.Type("JsonSerializerOptions"), "options")
            .Body(Cs.Tree(NodeThrow(NodeNew("NotSupportedException")))).End();
        converter.End();
        if (allEnumFactory) EnumFactoryEmitter.EmitPublic(root);
        if (contextOptions) ContextOptionsCacheEmitter.Emit(root);
        if (types.Any(type => type.SpecialType == SpecialType.System_Object)) RuntimeTypeEmitter.EmitPublic(root);
        ExceptionEmitter.Emit(root);
        PolymorphismEmitter.EmitCommon(root);
        ReferenceCycleEmitter.Emit(root);
        ReferenceReadEmitter.Emit(root);
        EmitStructCallbacks(root);
        root.Method(Cs.Void, "AssignPrivateField").Private().Static().Generic("T")
            .Parameter(Cs.Type("T"), "target", "ref").Parameter(Cs.Type("T"), "value")
            .Body(Cs.Tree(NodeAssign(Cs.Symbol("target"), Cs.Symbol("value")))).End();
        bool dynamicExpanded = false;
        for (int i = 0; i < types.Count; i++)
        {
            if (!dynamicExpanded && types[i].SpecialType == SpecialType.System_Object)
            {
                foreach (var candidate in dynamicCandidates)
                    if (candidate.SpecialType != SpecialType.System_Object && !candidate.IsRefLikeType)
                        TryRegister(candidate, out _);
                dynamicExpanded = true;
            }
            Emit(types[i], i, root);
        }
        if (dynamicExpanded)
        {
            DynamicEmitter.Emit(root, types);
            RuntimeTypeEmitter.Emit(root, types);
        }
        bool objectMapKeys = mapKeys.Any(key => types[key].SpecialType == SpecialType.System_Object);
        if (objectMapKeys)
            for (int i = 0; i < types.Count; i++)
                if (FrameworkCollection.SupportsKey(types[i]) ||
                    ConverterAttributeEmitter.Converter(types[i], types[i]) is not null) mapKeys.Add(i);
        foreach (int key in mapKeys) MapKeyEmitter.Emit(root, this, types[key], key);
        if (objectMapKeys) MapKeyEmitter.EmitObjectWriter(root, types, mapKeys);
        if (allEnumFactory) EnumFactoryEmitter.Emit(root, types);
        if (converterFactories) ConverterFactoryEmitter.EmitDispatch(root, types);
        if (frameworkFactoryAdapter) ConverterBridgeEmitter.Emit(root, types);
        root.End();
        try { return document.Render(); }
        catch (Neemle.CSharpComposer.Validation.GenValidationException error)
        {
            var members = document.Build().Types[0].Members;
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                error.Message, @"Types\[0\]\.Members\[(\d+)\]"))
            {
                int index = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                Console.Error.WriteLine("Invalid generated member: " + members[index].Name);
            }
            throw;
        }
    }

    private static void EmitStructCallbacks(TypeBuilder<FileBuilder> root)
    {
        foreach (string name in new[] { "OnSerializing", "OnSerialized", "OnDeserializing", "OnDeserialized" })
        {
            root.Method(Cs.Void, "StructCallback_" + name).Private().Static()
                .Generic("T", Cs.Constraint("struct"), Cs.Constraint(NodeType("IJson" + name)))
                .Parameter(Cs.Type("T"), "value", "ref")
                .Body(Cs.Tree(NodeCall("value." + name))).End();
        }
    }

    private void Emit(ITypeSymbol type, int i, TypeBuilder<FileBuilder> root)
    {
        string name = TypeName(type);
        var attributeConverter = ConverterAttributeEmitter.Converter(type, type);
        if (attributeConverter is not null) ConverterAttributeEmitter.Emit(root, type, attributeConverter, "type_" + i);
        EmitRegistration(i, name, root, type.TypeKind == TypeKind.Enum && attributeConverter is null);
        if (asyncSequences.Contains(i)) AsyncEnumerableEmitter.EmitPublic(root, i, name);
        if (asyncWriters.Contains(i)) AsyncSequenceWriterEmitter.EmitPublic(root, i, name, type.IsReferenceType);
        CodecApiEmitter.EmitPublicApi(i, name, type, root);
        if (nodeValues.Contains(i)) JsonDomApiEmitter.EmitNodeValue(i, name, type, root);
        if (asyncSequences.Contains(i)) AsyncEnumerableEmitter.Emit(root, i, name);
        if (asyncWriters.Contains(i)) AsyncSequenceWriterEmitter.Emit(root, i, name, type.IsReferenceType);
        if (type is INamedTypeSymbol polymorphic && PolymorphismEmitter.Variants(polymorphic) is not null)
            PolymorphismEmitter.Emit(root, this, polymorphic, i);
        bool objectCodec = EmitWriter(type, i, name, root);
        EmitReader(type, i, name, root);
        if (objectCodec) objectEmitter.EmitObjectHelpers(root, (INamedTypeSymbol)type, i);
        if (attributeConverter is null && type is INamedTypeSymbol enumeration && enumeration.TypeKind == TypeKind.Enum)
            EnumEmitter.EmitHelpers(root, enumeration, i, enumDisplays.Contains(i));
    }

    public void WriteAnonymousTypes(string directory)
    {
        var anonymous = types.OfType<INamedTypeSymbol>().Where(type => type.IsAnonymousType).ToArray();
        if (anonymous.Length == 0) return;
        var document = Cs.Document(CsLanguageVersion.CSharp14).Nullable(NullableMode.Enable);
        foreach (var type in anonymous)
        {
            var declaration = document.Class(TypeName(type)).Internal().Sealed();
            var properties = Properties(type);
            for (int i = 0; i < properties.Length; i++)
                declaration.Parameter(NodeType(TypeName(properties[i].Type)), "value" + i);
            for (int i = 0; i < properties.Length; i++)
                declaration.Property(NodeType(TypeName(properties[i].Type)), properties[i].Name).Public().Get()
                    .Initialize(Cs.Id("value" + i)).End();
            declaration.End();
        }
        File.WriteAllText(Path.Combine(directory, "JsonCodecsAnonymous.g.cs"), document.Render());
    }

    private void EmitRegistration(int i, string name, TypeBuilder<FileBuilder> root, bool supportsEnum)
    {
        CsType converterType = Cs.Generic("Converter", NodeType(name));
        var holder = root.Class($"Holder_{i}").Private().Sealed();
        holder.Field(converterType.Nullable(), "Converter").Public().End();
        holder.Field(Cs.Type("ConverterFactory").Nullable(), "Factory").Public().End();
        holder.Field(Cs.Type("bool"), "Registered").Public().End();
        if (supportsEnum)
        {
            holder.Field(Cs.Type("bool"), "StringEnum").Public().End();
            holder.Field(Cs.Int, "EnumPosition").Public().End();
            holder.Field(Cs.Type("bool"), "AllowEnumIntegers").Public().Initialize(Cs.Literal(true)).End();
            holder.Field(Cs.Type("string").Array().Nullable(), "EnumNames").Public().End();
        }
        holder.End();
        if (enumDisplays.Contains(i))
            root.Method(Cs.Type("string"), $"EnumDisplay_{i}").Internal().Static()
                .Parameter(NodeType(name), "value")
                .Body(Cs.Tree(Return(NodeCall($"FormatEnumDisplay_{i}", Cs.Symbol("value"))))).End();
        CsType tableType = Cs.Generic("ConditionalWeakTable", Cs.Type("JsonSerializerOptions"),
            Cs.Type($"Holder_{i}"));
        root.Field(tableType, $"Table_{i}").Private().Static().ReadOnly()
            .Initialize(new CsExpression(ExpressionKind.New, "", []))
            .End();
        ConverterFactoryEmitter.EmitRegistration(root, i, name, converterType);
        ConverterFactoryEmitter.EmitLookup(root, i, name, converterType, supportsEnum);
        root.Method(Cs.Bool, $"HasCustomConverter_{i}").Private().Static()
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Body(Cs.Tree(Return(ConverterCondition(i)))).End();
        foreach (bool isEnum in supportsEnum ? new[] { false, true } : [false])
        {
            string methodName = isEnum ? $"RegisterEnum_{i}" : $"Register_{i}";
            var method = root.Method(Cs.Type("void"), methodName).Internal().Static()
                .Parameter(Cs.Type("JsonSerializerOptions"), "options");
            if (!isEnum) method.Parameter(converterType, "converter");
            else method.Parameter(Cs.Type("JsonNamingPolicy").Nullable(), "namingPolicy", @default: Cs.Literal(null))
                .Parameter(Cs.Bool, "allowIntegerValues", @default: Cs.Literal(true));
            var factory = Cs.Lambda(new CsExpression(ExpressionKind.New, "", []) { Type = Cs.Type($"Holder_{i}") },
                Cs.LambdaParameter("_"))
                with
            { IsStatic = true };
            var condition = Cs.Symbol(new()
            {
                Operator = CsOperator.Not,
                Parts = new CsNode[] { NodeMember(Cs.Symbol("holder"), "Registered") }
            });
            var assignments = new List<CsNode> { NodeAssign(NodeMember(Cs.Symbol("holder"), isEnum ? "StringEnum" :
                "Converter"), isEnum ? Cs.Value(true) : Cs.Symbol("converter")) };
            if (isEnum) assignments.Add(NodeAssign(NodeMember(Cs.Symbol("holder"), "AllowEnumIntegers"),
                Cs.Symbol("allowIntegerValues")));
            if (isEnum) assignments.Add(NodeAssign(NodeMember(Cs.Symbol("holder"), "EnumNames"),
                NodeCall($"CreateEnumNames_{i}", Cs.Symbol("namingPolicy"))));
            if (isEnum) assignments.Add(NodeAssign(NodeMember(Cs.Symbol("holder"), "EnumPosition"),
                NodeMember(NodeCall("Converters", Cs.Symbol("options")), "Count")));
            assignments.Add(NodeAssign(NodeMember(Cs.Symbol("holder"), "Registered"), Cs.Value(true)));
            var statements = new List<CsNode>();
            statements.Add(Cs.Symbol("EnsureMutable", new()
            {
                Target = NodeCall("Converters", Cs.Symbol("options")),
                AsCall = true
            }));
            if (!isEnum) statements.Add(NodeIf(Cs.Symbol(new()
            {
                Operator = CsOperator.Not,
                Parts = new CsNode[] {
                NodeCall("converter.CanConvert", TypeOf(name)) }
            }), Cs.Instruction(InstructionKind.Return)));
            statements.Add(NodeVar("holder", NodeCall($"Table_{i}.GetValue", Cs.Symbol("options"), factory)));
            statements.Add(NodeIf(condition, assignments.ToArray()));
            method.Body(Cs.Tree(statements.ToArray())).End();
            var configure = root.Method(Cs.Type("JsonSerializerOptions"), isEnum ? $"ConfigureEnum_{i}" :
                $"ConfigureConverter_{i}").Internal().Static().Parameter(Cs.Type("JsonSerializerOptions"), "options");
            if (!isEnum) configure.Parameter(converterType, "converter");
            configure.Body(Cs.Tree(NodeCall(methodName, isEnum ? [Cs.Symbol("options")] :
                [Cs.Symbol("options"), Cs.Symbol("converter")]), Return(Cs.Symbol("options")))).End();
        }
    }

    private bool EmitWriter(ITypeSymbol type, int i, string name, TypeBuilder<FileBuilder> root)
    {
        var body = new List<CsNode>();
        bool trackCycles = type.IsReferenceType && GetShape(type) is Shape.Array or Shape.Collection or Shape.Map or
            Shape.Object;
        if (type.IsReferenceType)
        {
            EmitNullWriter(i, root);
            var nullBody = new List<CsNode> { NodeIf(NodeCall($"TryWriteNull_{i}", Cs.Symbol("w"), Cs.Symbol("options")),
                Cs.Instruction(InstructionKind.Return)) };
            if (ConverterAttributeEmitter.Converter(type, type) is not null)
                nullBody.Add(NodeCall($"WriteAttribute_type_{i}", Cs.Symbol("w"), NonNull(DefaultValue()),
                    Cs.Symbol("options"), Cs.Symbol("path")));
            else nullBody.Add(NodeCall("w.WriteNullValue"));
            nullBody.Add(Cs.Instruction(InstructionKind.Return));
            body.Add(NodeIf(NullTest(Cs.Symbol("value")), nullBody.ToArray()));
        }
        CsNode custom = ConverterCondition(i);
        if (type is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            custom = NodeBinary(CsOperator.AndAlso, custom, NodeBinary(CsOperator.OrElse,
                NodeMember(Cs.Symbol("value"), "HasValue"), NodeMember(Cs.Symbol("converter"), "HandleNull")));
        EmitCustomWriter(i, name, root, custom);
        CsNode useCustom = NodeCall($"TryWriteCustom_{i}", Cs.Symbol("w"), Cs.Symbol("value"), Cs.Symbol("options"),
            Cs.Symbol("path"));
        if (type.TypeKind == TypeKind.Enum) useCustom = EnumHandlingEmitter.AllowRegisteredConverter(useCustom);
        body.Add(NodeIf(useCustom, Cs.Instruction(InstructionKind.Return)));
        if (type is INamedTypeSymbol polymorphic && PolymorphismEmitter.Variants(polymorphic) is not null)
            body.Add(NodeIf(NodeCall($"TryWritePolymorphic_{i}", Cs.Symbol("w"), Cs.Symbol("value"),
                Cs.Symbol("options"), Cs.Symbol("path")), Cs.Instruction(InstructionKind.Return)));
        bool isObject;
        if (ConverterAttributeEmitter.Converter(type, type) is not null)
        {
            body.Add(NodeCall($"WriteAttribute_type_{i}", Cs.Symbol("w"), NonNull(Cs.Symbol("value")),
                Cs.Symbol("options"), Cs.Symbol("path")));
            isObject = false;
        }
        else if (type is INamedTypeSymbol abstractType && (abstractType.IsAbstract ||
            abstractType.TypeKind == TypeKind.Interface) && PolymorphismEmitter.Variants(abstractType) is not null)
        {
            body.Add(NodeThrow(NodeNew("NotSupportedException")));
            isObject = false;
        }
        else isObject = EmitWriteBody(type, i, body);
        bool numeric = NumberHandlingEmitter.CanApply(type);
        bool allowPreserve = type is not IArrayTypeSymbol && (type is not INamedTypeSymbol named ||
            FrameworkCollection.ImmutableSequenceFactory(named) is null &&
            FrameworkCollection.ImmutableMapFactory(named) is null) &&
            ConverterAttributeEmitter.Converter(type, type) is null;
        if (trackCycles)
        {
            var core = root.Method(Cs.Void, $"WriteCore_{i}").Private().Static()
                .Parameter(Cs.Type("Utf8JsonWriter"), "w").Parameter(NodeType(name), "value")
                .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Parameter(Cs.Type("string"), "path");
            if (numeric) core.Parameter(Cs.Type("JsonNumberHandling").Nullable(), "handlingOverride");
            core.Body(Cs.Tree(body.Skip(2).ToArray())).End();
        }
        var method = root.Method(Cs.Void, $"Write_{i}").Private().Static().Parameter(Cs.Type("Utf8JsonWriter"), "w")
            .Parameter(type.IsReferenceType ? NodeType(name).Nullable() : NodeType(name), "value")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Parameter(Cs.Type("string"), "path");
        if (numeric)
            method.Parameter(Cs.Type("JsonNumberHandling").Nullable(), "handlingOverride", @default: Cs.Literal(null));
        if (type.TypeKind == TypeKind.Enum)
            method.Parameter(Cs.Bool.Nullable(), "enumOverride", @default: Cs.Literal(null));
        method.Body(Cs.Tree(trackCycles ? ReferenceCycleEmitter.Wrap(i, body, numeric, allowPreserve) :
            body.ToArray())).End();
        if (type is INamedTypeSymbol map && FrameworkCollection.IsMap(map)) EmitMapWriterHelper(root, map, i, name);
        return isObject;
    }

    private static void EmitNullWriter(int i, TypeBuilder<FileBuilder> root)
    {
        root.Method(Cs.Bool, $"TryWriteNull_{i}").Private().Static().Parameter(Cs.Type("Utf8JsonWriter"), "w")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Body(Cs.Tree(NodeIf(ConverterCondition(i), NodeIf(NodeMember(Cs.Symbol("converter"), "HandleNull"),
                    NodeCall("converter.Write", Cs.Symbol("w"), NonNull(DefaultValue()), NonNull(Cs.Symbol("options")))),
                NodeIf(Cs.Symbol(new()
                {
                    Operator = CsOperator.Not,
                    Parts = new CsNode[] {
                    NodeMember(Cs.Symbol("converter"), "HandleNull") }
                }), NodeCall("w.WriteNullValue")),
                Return(Cs.Value(true))), Return(Cs.Value(false)))).End();
    }

    private static void EmitCustomWriter(int index, string type, TypeBuilder<FileBuilder> root, CsNode condition)
    {
        var write = Cs.Try(Cs.Body(Call("converter.Write", Cs.Id("w"), Cs.Id("value"), Cs.Id("options")),
            Cs.Return(Cs.Literal(true))), ConverterCatches());
        root.Method(Cs.Bool, $"TryWriteCustom_{index}").Private().Static()
            .Parameter(Cs.Type("Utf8JsonWriter"), "w").Parameter(NodeType(type), "value")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Parameter(Cs.Type("string"), "path")
            .Body(Cs.Tree(NodeIf(condition, write), Return(Cs.Value(false)))).End();
    }

    private void EmitMapWriterHelper(TypeBuilder<FileBuilder> root, INamedTypeSymbol type, int index, string name)
    {
        var body = new List<CsNode>();
        FrameworkCollection.EmitMapWriter(body, type, this);
        var method = root.Method(Cs.Void, $"WriteMap_{index}").Private().Static()
            .Parameter(Cs.Type("Utf8JsonWriter"), "w").Parameter(NodeType(name), "value")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Parameter(Cs.Type("string"), "path");
        if (NumberHandlingEmitter.CanApply(type)) method.Parameter(Cs.Type("JsonNumberHandling").Nullable(),
            "handlingOverride");
        method.Body(Cs.Tree(body.ToArray())).End();
    }

    private static Values<CsCatch> ConverterCatches() => new([
        Cs.Catch(Cs.Type("NotSupportedException"),
            Cs.Body(Cs.Throw(Invoke("ConverterNotSupported", Cs.Id("error"), Cs.Id("path")))), "error"),
        Cs.Catch(Cs.Type("JsonException"),
            Cs.Body(Cs.Throw(Invoke("ConverterJsonError", Cs.Id("error"), Cs.Id("path")))), "error")]);

    private bool EmitWriteBody(ITypeSymbol type, int i, List<CsNode> body)
    {
        var shape = GetShape(type);
        if (shape is Shape.Array or Shape.Collection or Shape.Map or Shape.Object or Shape.Memory)
            body.Add(NodeCall("CheckWriteDepth", Cs.Symbol("w"), Cs.Symbol("options"), Cs.Symbol("path")));
        switch (shape)
        {
            case Shape.String: body.Add(NodeCall("w.WriteStringValue", Cs.Symbol("value"))); break;
            case Shape.Number: NumberHandlingEmitter.EmitWriter(body, i); break;
            case Shape.WideNumber: WideNumberEmitter.Write(body, type, i); break;
            case Shape.Boolean: body.Add(NodeCall("w.WriteBooleanValue", Cs.Symbol("value"))); break;
            case Shape.Dynamic:
                body.Add(NodeCall("WriteDynamic", Cs.Symbol("w"), Cs.Symbol("value"), Cs.Symbol("options"),
                    Cs.Symbol("path")));
                break;
            case Shape.DomValue: DomValue.EmitWriter(body, type); break;
            case Shape.FrameworkScalar:
                body.Add(NodeCall("w.WriteStringValue", FrameworkScalar.WriteValue(type)));
                break;
            case Shape.Bytes: body.Add(NodeCall("w.WriteBase64StringValue", Cs.Symbol("value"))); break;
            case Shape.Array: SequenceWriter(body, ((IArrayTypeSymbol)type).ElementType, false); break;
            case Shape.Memory: EmitMemoryWriter(body, (INamedTypeSymbol)type); break;
            case Shape.Nullable: EmitNullableWriter(body, (INamedTypeSymbol)type); break;
            case Shape.Collection:
                var collection = (INamedTypeSymbol)type;
                SequenceWriter(body, FrameworkCollection.ElementType(collection, objectType), collection.IsReferenceType &&
                    FrameworkCollection.ImmutableSequenceFactory(collection) is null);
                break;
            case Shape.Enum: EmitEnumWriter(body, i, type); break;
            case Shape.Map:
                body.Add(NodeCall($"WriteMap_{i}", NumberHandlingEmitter.Forward(type, Cs.Symbol("w"),
                    Cs.Symbol("value"), Cs.Symbol("options"), Cs.Symbol("path"))));
                break;
            default: objectEmitter.EmitObjectWriter(body, (INamedTypeSymbol)type, i); return true;
        }
        return false;
    }

    private void EmitNullableWriter(List<CsNode> body, INamedTypeSymbol type)
    {
        body.Add(NodeIf(Cs.Symbol(new()
        {
            Operator = CsOperator.Not,
            Parts = new CsNode[] { NodeMember(Cs.Symbol("value"), "HasValue") }
        }), NodeCall("w.WriteNullValue"),
            Cs.Instruction(InstructionKind.Return)));
        var element = type.TypeArguments[0];
        body.Add(NodeCall($"Write_{Register(element)}", NumberHandlingEmitter.Forward(element, Cs.Symbol("w"),
            NodeMember(Cs.Symbol("value"), "Value"), Cs.Symbol("options"), Cs.Symbol("path"))));
    }

    private void EmitMemoryWriter(List<CsNode> body, INamedTypeSymbol type)
    {
        var element = type.TypeArguments[0];
        var span = NodeMember(Cs.Symbol("value"), "Span");
        if (element.SpecialType == SpecialType.System_Byte)
        {
            body.Add(NodeCall("w.WriteBase64StringValue", span));
            return;
        }
        body.Add(NodeCall("w.WriteStartArray"));
        body.Add(NodeVar("index", Cs.Value(0)));
        body.Add(NodeEach("item", span, NodeCall($"Write_{Register(element)}",
            NumberHandlingEmitter.Forward(element, Cs.Symbol("w"), Cs.Symbol("item"), Cs.Symbol("options"),
                SequencePath()))));
        body.Add(NodeCall("w.WriteEndArray"));
    }

    private static void EmitEnumWriter(List<CsNode> body, int i, ITypeSymbol type)
    {
        body.Add(EnumOption(i, type));
        body.Add(NodeCall($"WriteEnum_{i}", Cs.Symbol("w"), Cs.Symbol("value"), Cs.Symbol("stringEnum"),
            NodeCall($"EnumAllowsInteger_{i}", Cs.Symbol("options"), Cs.Symbol("enumOverride")),
            NodeCall($"EnumNamesFor_{i}", Cs.Symbol("options"), Cs.Symbol("enumOverride"))));
    }

    private void EmitReader(ITypeSymbol type, int i, string name, TypeBuilder<FileBuilder> root)
    {
        var body = new List<CsNode>();
        bool allowNull = type.IsReferenceType || type is INamedTypeSymbol maybe && maybe.OriginalDefinition.SpecialType
            == SpecialType.System_Nullable_T;
        CsNode custom = NodeCall($"TryReadCustom_{i}", Cs.Symbol("e"), Cs.Symbol("options"), Cs.Symbol("path"),
            Arg(ArgumentModifier.Out, Cs.Symbol("customValue", new()
            {
                Operation = SymbolOperation.Declaration,
                Type = NodeType(name)
            })));
        if (type.TypeKind == TypeKind.Enum) custom = EnumHandlingEmitter.AllowRegisteredConverter(custom);
        body.Add(NodeIf(custom, Return(Cs.Symbol("customValue"))));
        if (allowNull)
        {
            CsNode nullValue = ConverterAttributeEmitter.Converter(type, type) is not null ?
                NodeCall($"ReadAttribute_type_{i}", Cs.Symbol("e"), Cs.Symbol("options"), Cs.Symbol("path")) :
                NonNull(DefaultValue());
            body.Add(NodeIf(NodeBinary(CsOperator.Equal, NodeMember(Cs.Symbol("e"), "ValueKind"),
                NodeMember(Cs.Symbol("JsonValueKind"), "Null")), Return(nullValue)));
        }
        bool scoped = GetShape(type) is Shape.Array or Shape.Collection or Shape.Map or Shape.Object or Shape.Dynamic;
        bool resolve = scoped && type.IsReferenceType && type is not IArrayTypeSymbol &&
            ConverterAttributeEmitter.Converter(type, type) is null && (type is not INamedTypeSymbol referenceType ||
                FrameworkCollection.ImmutableSequenceFactory(referenceType) is null &&
                FrameworkCollection.ImmutableMapFactory(referenceType) is null);
        if (resolve) body.Add(NodeIf(ReferenceReadEmitter.Resolve(name), Return(Cs.Symbol("reference"))));
        if (type is INamedTypeSymbol polymorphic && PolymorphismEmitter.Variants(polymorphic) is not null)
            body.Add(NodeIf(NodeCall($"TryReadPolymorphic_{i}", Cs.Symbol("e"), Cs.Symbol("options"), Cs.Symbol("path"),
                OutVar("polymorphicValue")), Return(Cs.Symbol("polymorphicValue"))));
        if (ConverterAttributeEmitter.Converter(type, type) is not null)
            body.Add(Return(NodeCall($"ReadAttribute_type_{i}", Cs.Symbol("e"), Cs.Symbol("options"),
                Cs.Symbol("path"))));
        else if (type is INamedTypeSymbol abstractType && (abstractType.IsAbstract ||
            abstractType.TypeKind == TypeKind.Interface) && PolymorphismEmitter.Variants(abstractType) is not null)
            body.Add(NodeThrow(NodeNew("NotSupportedException")));
        else EmitReadBody(type, i, body);
        bool numeric = NumberHandlingEmitter.CanApply(type);
        if (scoped)
        {
            var core = root.Method(NodeType(name), $"ReadCore_{i}").Private().Static()
                .Parameter(Cs.Type("JsonElement"), "e").Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
                .Parameter(Cs.Type("string"), "path");
            if (numeric) core.Parameter(Cs.Type("JsonNumberHandling").Nullable(), "handlingOverride");
            core.Body(Cs.Tree(body.ToArray())).End();
        }
        var method = root.Method(NodeType(name), $"Read_{i}").Private().Static().Parameter(Cs.Type("JsonElement"), "e")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Parameter(Cs.Type("string"), "path");
        if (numeric)
            method.Parameter(Cs.Type("JsonNumberHandling").Nullable(), "handlingOverride", @default: Cs.Literal(null));
        if (type.TypeKind == TypeKind.Enum)
            method.Parameter(Cs.Bool.Nullable(), "enumOverride", @default: Cs.Literal(null));
        method.Body(Cs.Tree(scoped ? ReferenceReadEmitter.Wrap(i, numeric) : body.ToArray())).End();
        EmitCompositeReader(type, i, name, root);
        FrameworkScalar.EmitHelper(type, i, root);
        if (NumericGetter(type.SpecialType) is not null || WideNumberEmitter.IsWide(type))
            NumberHandlingEmitter.EmitHelpers(root, type, i);
        EmitCustomReader(i, name, root, allowNull);
    }

    private static void EmitCustomReader(int i, string name, TypeBuilder<FileBuilder> root, bool allowNull)
    {
        var customBody = new List<CsNode>();
        if (allowNull) customBody.Add(NodeIf(NodeBinary(CsOperator.AndAlso,
            NodeBinary(CsOperator.Equal, NodeMember(Cs.Symbol("e"), "ValueKind"), NodeMember(Cs.Symbol("JsonValueKind"), "Null")),
            Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { NodeMember(Cs.Symbol("converter"), "HandleNull") } })),
            NodeAssign(Cs.Symbol("value"), NonNull(DefaultValue())), Return(Cs.Value(true))));
        customBody.Add(NodeVar("reader", NodeNew("Utf8JsonReader",
                NodeCall("Encoding.UTF8.GetBytes", NodeCall("e.GetRawText")))));
        customBody.Add(NodeIf(Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { NodeCall("reader.Read") } }),
                NodeThrow(NodeNew("JsonException"))));
        customBody.Add(Cs.Try(Cs.Body(Cs.Var("converted", Invoke("converter.Read", LegacyArgument(ArgumentModifier.Ref, "reader"),
                new CsExpression(ExpressionKind.TypeOf, "", []) { Type = NodeType(name == "object?[]" ? "object[]" : name) },
                Cs.Id("options"))),
                Assign(Cs.Id("value"),
                    new CsExpression(ExpressionKind.NullForgiving, "", new[] { Cs.Id("converted") })),
                Cs.Return(Cs.Literal(true))), ConverterCatches()));
        root.Method(Cs.Bool, $"ReadWithCustom_{i}").Private().Static().Parameter(Cs.Type("JsonElement"), "e")
            .Parameter(Cs.Generic("Converter", NodeType(name)), "converter")
            .Parameter(Cs.Type("JsonSerializerOptions"), "options").Parameter(Cs.Type("string"), "path")
            .Parameter(NodeType(name), "value", "out").Body(Cs.Tree(customBody.ToArray())).End();
        root.Method(Cs.Bool, $"TryReadCustom_{i}").Private().Static().Parameter(Cs.Type("JsonElement"), "e")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options").Parameter(Cs.Type("string"), "path")
            .Parameter(NodeType(name), "value", "out")
            .Body(Cs.Tree(Cs.Instruction(InstructionKind.If, new()
            {
                Condition = ConverterCondition(i),
                Body = Cs.Tree(Return(NodeCall($"ReadWithCustom_{i}", Cs.Symbol("e"),
                Cs.Symbol("converter"), Cs.Symbol("options"), Cs.Symbol("path"),
                Arg(ArgumentModifier.Out, Cs.Symbol("value")))))
            }),
                NodeAssign(Cs.Symbol("value"), NonNull(DefaultValue())), Return(Cs.Value(false)))).End();
    }

    internal static CsSymbol ConverterCondition(int i) => NodeBinary(CsOperator.AndAlso,
        NullTest(Cs.Symbol("options"), true),
        NodeCall($"TryFindConverter_{i}", Cs.Symbol("options"), OutVar("converter")));

    private void EmitReadBody(ITypeSymbol type, int i, List<CsNode> body)
    {
        switch (GetShape(type))
        {
            case Shape.String: FrameworkScalar.EmitStringReader(body); break;
            case Shape.Number:
                NumberHandlingEmitter.EmitReader(body, i);
                FrameworkScalar.EmitReader(body, NumericGetter(type.SpecialType)!,
                    type.SpecialType.ToString().Replace('_', '.'));
                break;
            case Shape.WideNumber: WideNumberEmitter.Read(body, type, i); break;
            case Shape.Boolean: FrameworkScalar.EmitReader(body, "GetBoolean"); break;
            case Shape.Dynamic:
                body.Add(Return(NodeCall("ReadDynamic", Cs.Symbol("e"), Cs.Symbol("options"))));
                break;
            case Shape.DomValue: DomValue.EmitReader(body, type); break;
            case Shape.FrameworkScalar:
                FrameworkScalar.EmitFrameworkReader(body, type, i);
                break;
            case Shape.Bytes: FrameworkScalar.EmitReader(body, "GetBytesFromBase64"); break;
            case Shape.Memory: EmitMemoryReader(body, (INamedTypeSymbol)type); break;
            case Shape.Array:
            case Shape.Collection:
            case Shape.Map:
                body.Add(Return(NodeCall($"ReadComposite_{i}", NumberHandlingEmitter.Forward(type,
                    Cs.Symbol("e"), Cs.Symbol("options"), Cs.Symbol("path")))));
                break;
            case Shape.Nullable:
                var element = ((INamedTypeSymbol)type).TypeArguments[0];
                int index = Register(element);
                body.Add(Return(NodeCall($"Read_{index}", NumberHandlingEmitter.Forward(element,
                    Cs.Symbol("e"), Cs.Symbol("options"), Cs.Symbol("path")))));
                break;
            case Shape.Enum:
                body.Add(EnumOption(i, type));
                body.Add(Return(NodeCall($"ReadEnum_{i}", Cs.Symbol("e"), Cs.Symbol("stringEnum"),
                    NodeCall($"EnumAllowsInteger_{i}", Cs.Symbol("options"), Cs.Symbol("enumOverride")),
                    NodeCall($"EnumNamesFor_{i}", Cs.Symbol("options"), Cs.Symbol("enumOverride"))))); break;
            default: objectEmitter.EmitObjectReader(body, (INamedTypeSymbol)type, i); break;
        }
    }

    private void EmitCompositeReader(ITypeSymbol type, int index, string name, TypeBuilder<FileBuilder> root)
    {
        var shape = GetShape(type);
        if (shape is not (Shape.Array or Shape.Collection or Shape.Map)) return;
        var body = new List<CsNode>();
        if (shape == Shape.Array) EmitArrayReader(body, (IArrayTypeSymbol)type);
        else if (shape == Shape.Collection) EmitCollectionReader(body, (INamedTypeSymbol)type);
        else EmitMapReader(body, (INamedTypeSymbol)type);
        var method = root.Method(NodeType(name), $"ReadComposite_{index}").Private().Static()
            .Parameter(Cs.Type("JsonElement"), "e").Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options")
            .Parameter(Cs.Type("string"), "path");
        if (NumberHandlingEmitter.CanApply(type))
            method.Parameter(Cs.Type("JsonNumberHandling").Nullable(), "handlingOverride");
        method.Body(Cs.Tree(body.ToArray())).End();
    }

    private static CsSymbol EnumOption(int i, ITypeSymbol type) => Cs.Symbol("stringEnum", new()
    {
        Type = Cs.Bool,
        Value = EnumHandlingEmitter.UseRegisteredOrDefault(i, type)
    });

    private void EmitCollectionReader(List<CsNode> body, INamedTypeSymbol type)
    {
        if (!FrameworkCollection.CanDeserialize(type))
        {
            body.Add(NodeThrow(NodeNew("NotSupportedException")));
            return;
        }
        string? factory = FrameworkCollection.ImmutableSequenceFactory(type);
        var element = FrameworkCollection.ElementType(type, objectType);
        bool preserve = type.IsReferenceType && factory is null;
        if (preserve)
        {
            body.Add(NodeVar("referenceSource", Cs.Symbol("e")));
            body.Add(NodeAssign(Cs.Symbol("e"), NodeCall("ReferenceValues", Cs.Symbol("e"), Cs.Symbol("options"))));
        }
        body.Add(FrameworkScalar.RequireKind("Array"));
        string constructor = factory is null ? FrameworkCollection.Constructor(type) :
            $"System.Collections.Generic.List<{TypeName(element)}>";
        body.Add(NodeVar("list", NodeNew(constructor)));
        if (preserve) body.Add(NodeCall("RegisterReference", Cs.Symbol("referenceSource"), Cs.Symbol("list"),
            Cs.Symbol("options")));
        body.Add(NodeVar("index", Cs.Value(0)));
        body.Add(ReadSequence(Register(element), factory is null ? FrameworkCollection.AddMethod(type)! : "Add", element,
            factory is null && FrameworkCollection.UseCollectionInterface(type)));
        body.Add(Return(factory is null ? Cs.Symbol("list") : NodeCall(factory + ".CreateRange", Cs.Symbol("list"))));
    }

    private void EmitArrayReader(List<CsNode> body, IArrayTypeSymbol array)
    {
        body.Add(FrameworkScalar.RequireKind("Array"));
        string element = TypeName(array.ElementType);
        body.Add(NodeVar("list", NodeNew($"System.Collections.Generic.List<{element}>")));
        body.Add(NodeVar("index", Cs.Value(0)));
        body.Add(ReadSequence(Register(array.ElementType), "Add", array.ElementType));
        body.Add(Return(NodeCall("list.ToArray")));
    }

    private void EmitMemoryReader(List<CsNode> body, INamedTypeSymbol type)
    {
        var element = type.TypeArguments[0];
        if (element.SpecialType == SpecialType.System_Byte)
        {
            body.Add(Return(NodeNew(TypeName(type), NodeCall("e.GetBytesFromBase64"))));
            return;
        }
        body.Add(FrameworkScalar.RequireKind("Array"));
        body.Add(NodeVar("list", NodeNew($"System.Collections.Generic.List<{TypeName(element)}>")));
        body.Add(NodeVar("index", Cs.Value(0)));
        body.Add(ReadSequence(Register(element), "Add", element));
        body.Add(Return(NodeNew(TypeName(type), NodeCall("list.ToArray"))));
    }

    private void EmitMapReader(List<CsNode> body, INamedTypeSymbol map)
    {
        if (!FrameworkCollection.CanDeserialize(map))
        {
            body.Add(NodeThrow(NodeNew("NotSupportedException")));
            return;
        }
        body.Add(FrameworkScalar.RequireKind("Object"));
        string? factory = FrameworkCollection.ImmutableMapFactory(map);
        var keyType = FrameworkCollection.MapKeyType(map);
        var valueType = FrameworkCollection.MapValueType(map);
        string constructor = factory is null ? FrameworkCollection.Constructor(map) :
            $"System.Collections.Generic.Dictionary<{TypeName(keyType)}, {TypeName(valueType)}>";
        body.Add(NodeVar("map", NodeNew(constructor)));
        bool preserve = map.IsReferenceType && factory is null;
        if (preserve) body.Add(NodeCall("RegisterReference", Cs.Symbol("e"), Cs.Symbol("map"), Cs.Symbol("options")));
        var key = NodeMember(Cs.Symbol("v"), "Name");
        var path = NodeBinary(CsOperator.Add, NodeBinary(CsOperator.Add, Cs.Symbol("path"), Cs.Value(".")), key);
        var read = NodeCall($"Read_{Register(valueType)}", NumberHandlingEmitter.Forward(valueType,
            NodeMember(Cs.Symbol("v"), "Value"), Cs.Symbol("options"), path));
        var statements = FrameworkCollection.ReadMapKey(keyType, this);
        if (preserve) statements.Insert(0, NodeIf(NodeCall("IsReferenceId", NodeMember(Cs.Symbol("v"), "Name"),
            Cs.Symbol("options")), Cs.Instruction(InstructionKind.Continue)));
        CsNode target = FrameworkCollection.UseMapInterface(map) ?
            Cast($"System.Collections.Generic.IDictionary<{TypeName(keyType)}, {TypeName(valueType)}>", Cs.Symbol("map")) :
            Cs.Symbol("map");
        statements.Add(DuplicatePropertyEmitter.Check(Cs.Symbol("ContainsKey", new()
        { Target = target, AsCall = true, Arguments = Cs.Compound(Cs.Symbol("key")) })));
        statements.Add(NodeAssign(Index(target, Cs.Symbol("key")), read));
        body.Add(NodeEach("v", NodeCall("e.EnumerateObject"), statements.ToArray()));
        body.Add(Return(factory is null ? Cs.Symbol("map") : NodeCall(factory + ".CreateRange", Cs.Symbol("map"))));
    }

    private void SequenceWriter(List<CsNode> body, ITypeSymbol element, bool preserve)
    {
        if (preserve) body.Add(NodeIf(ReferenceCycleEmitter.IsPreserve(), NodeCall("w.WriteStartObject"),
            NodeCall("WriteReferenceId", Cs.Symbol("w"), Cs.Symbol("value"), Cs.Symbol("options")),
            NodeCall("w.WritePropertyName", Cs.Value("$values"))));
        body.Add(NodeCall("w.WriteStartArray"));
        body.Add(NodeVar("index", Cs.Value(0)));
        body.Add(NodeEach("item", Cs.Symbol("value"), NodeCall($"Write_{Register(element)}",
            NumberHandlingEmitter.Forward(element, Cs.Symbol("w"), Cs.Symbol("item"), Cs.Symbol("options"),
                SequencePath()))));
        body.Add(NodeCall("w.WriteEndArray"));
        if (preserve) body.Add(NodeIf(ReferenceCycleEmitter.IsPreserve(), NodeCall("w.WriteEndObject")));
    }

    private static CsInstruction ReadSequence(int index, string addMethod, ITypeSymbol element, bool asCollection = false)
    {
        CsNode target = asCollection ? Cast($"System.Collections.Generic.ICollection<{TypeName(element)}>",
            Cs.Symbol("list")) : Cs.Symbol("list");
        var read = NodeCall($"Read_{index}", NumberHandlingEmitter.Forward(element,
            Cs.Symbol("v"), Cs.Symbol("options"), SequencePath()));
        return NodeEach("v", NodeCall("e.EnumerateArray"), Cs.Symbol(addMethod, new()
        { Target = target, AsCall = true, Arguments = Cs.Compound(read) }));
    }

    private static CsSymbol SequencePath() => NodeBinary(CsOperator.Add,
        NodeBinary(CsOperator.Add, NodeBinary(CsOperator.Add, Cs.Symbol("path"), Cs.Value("[")),
            Cs.PostIncrement(Cs.Id("index"))), Cs.Value("]"));

    private static CsStatement Call(string method, params CsExpression[] arguments) => Cs.Statement(
        Invoke(method, arguments));

    private static CsStatement Assign(CsExpression target, CsExpression value) =>
        Cs.Statement(Cs.Assign(target, value));

    private static CsExpression LegacyArgument(ArgumentModifier modifier, string name) =>
        new(ExpressionKind.Argument, "", new[] { Cs.Id(name) }) { ArgumentModifier = modifier };
    private static string JsonName(ISymbol p) => p.GetAttributes()
        .FirstOrDefault(a => IsJsonAttribute(a, "JsonPropertyNameAttribute"))?.ConstructorArguments[0].Value
        as string ?? p.Name;
    internal static CsNode JsonNameExpression(ISymbol p)
    {
        if (p.GetAttributes().Any(a => IsJsonAttribute(a, "JsonPropertyNameAttribute")))
            return Cs.Value(JsonName(p));
        string key = TypeName(p.ContainingType) + "." + p.Name;
        return NodeCall("PropertyName", Cs.Symbol("options"), Cs.Value(key), Cs.Value(p.Name));
    }
    internal static bool IsRequired(IPropertySymbol p) => p.IsRequired ||
        p.GetAttributes().Any(a => IsJsonAttribute(a, "JsonRequiredAttribute"));
    internal static bool IsRequired(IFieldSymbol field) => field.IsRequired ||
        field.GetAttributes().Any(attribute => IsJsonAttribute(attribute, "JsonRequiredAttribute"));
    internal static int IgnoreCondition(ISymbol p)
    {
        var attr = p.GetAttributes().FirstOrDefault(a => IsJsonAttribute(a, "JsonIgnoreAttribute"));
        if (attr is null) return 0;
        return attr.NamedArguments.FirstOrDefault(a => a.Key == "Condition").Value.Value is int value ? value : 1;
    }
    private static bool IgnoreAlways(ISymbol p) => IgnoreCondition(p) == 1;
    private static bool SupportsIgnoreCondition(ISymbol member, ITypeSymbol type) => IgnoreCondition(member) switch
    {
        0 or 1 or 2 or 4 or 5 => true,
        3 => type.IsReferenceType || type is INamedTypeSymbol nullable && nullable.OriginalDefinition
            .SpecialType == SpecialType.System_Nullable_T,
        _ => false
    };
    private static bool UnsupportedAttributes(ISymbol member) => member.GetAttributes().Any(a =>
        a.AttributeClass?.ContainingNamespace.ToDisplayString() == "System.Text.Json.Serialization" &&
        (a.AttributeClass.Name is not ("JsonPropertyNameAttribute" or "JsonPropertyOrderAttribute" or
            "JsonRequiredAttribute" or
            "JsonIgnoreAttribute" or "JsonNumberHandlingAttribute" or "JsonIncludeAttribute" or "JsonConverterAttribute" or
                "JsonObjectCreationHandlingAttribute") ||
         a.AttributeClass.Name == "JsonNumberHandlingAttribute" && (a.ConstructorArguments.Length != 1 || a
             .ConstructorArguments[0].Value is not int value || (value & ~7) != 0)));

    internal static int? NumberHandling(ISymbol member) => member.GetAttributes()
        .FirstOrDefault(attribute => IsJsonAttribute(attribute, "JsonNumberHandlingAttribute"))
        ?.ConstructorArguments.FirstOrDefault().Value as int?;

    internal static bool IsJsonAttribute(AttributeData attribute, string name) =>
        attribute.AttributeClass?.Name == name &&
        attribute.AttributeClass.ContainingNamespace.ToDisplayString() == "System.Text.Json.Serialization";
}

using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Model;
using static Neemle.Json.Generator.CodecApiEmitter;

namespace Neemle.Json.Generator;

internal static class FrameworkCollection
{
    internal static bool SupportsKey(ITypeSymbol type) => type.SpecialType is SpecialType.System_String or
        SpecialType.System_Boolean or SpecialType.System_Char || CodecEmitter.NumericGetter(type.SpecialType) is not null ||
        WideNumberEmitter.IsWide(type) ||
        type.ToDisplayString() is "System.Guid" or "System.Uri" or "System.Version" or "System.DateTime" or
            "System.DateTimeOffset" or "System.DateOnly" or "System.TimeOnly" or "System.TimeSpan" ||
        type is INamedTypeSymbol { TypeKind: Microsoft.CodeAnalysis.TypeKind.Enum } enumeration &&
            EnumEmitter.CanEmit(enumeration);

    internal static string? AddMethod(INamedTypeSymbol type) => KnownAddMethod(type) ?? CustomAddMethod(type);

    private static string? KnownAddMethod(INamedTypeSymbol type) => type.OriginalDefinition.ToDisplayString() switch
    {
        "System.Collections.Generic.List<T>" => "Add",
        "System.Collections.Generic.HashSet<T>" => "Add",
        "System.Collections.Generic.Queue<T>" => "Enqueue",
        "System.Collections.Generic.Stack<T>" => "Push",
        "System.Collections.Generic.LinkedList<T>" => "AddLast",
        "System.Collections.Generic.SortedSet<T>" => "Add",
        "System.Collections.Generic.IEnumerable<T>" => "Add",
        "System.Collections.Generic.ICollection<T>" => "Add",
        "System.Collections.Generic.IList<T>" => "Add",
        "System.Collections.Generic.IReadOnlyCollection<T>" => "Add",
        "System.Collections.Generic.IReadOnlyList<T>" => "Add",
        "System.Collections.Generic.IReadOnlySet<T>" => "Add",
        "System.Collections.Generic.ISet<T>" => "Add",
        "System.Collections.Concurrent.ConcurrentQueue<T>" => "Enqueue",
        "System.Collections.Concurrent.ConcurrentStack<T>" => "Push",
        "System.Collections.Concurrent.ConcurrentBag<T>" => "Add",
        "System.Collections.Concurrent.BlockingCollection<T>" => "Add",
        "System.Collections.ObjectModel.Collection<T>" => "Add",
        "System.Collections.ObjectModel.ObservableCollection<T>" => "Add",
        "System.Collections.ObjectModel.ReadOnlyCollection<T>" => "Add",
        "System.Collections.ObjectModel.ReadOnlyObservableCollection<T>" => "Add",
        "System.Collections.ArrayList" or "System.Collections.IEnumerable" or "System.Collections.ICollection" or
            "System.Collections.IList" or "System.Collections.BitArray" => "Add",
        "System.Collections.Queue" => "Enqueue",
        "System.Collections.Stack" => "Push",
        _ => null
    };

    private static string? CustomAddMethod(INamedTypeSymbol type)
    {
        if (IsMap(type) || ImmutableSequenceFactory(type) is not null || GenericInterface(type,
            "System.Collections.Generic.IEnumerable<T>") is null) return null;
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            string name = current.OriginalDefinition.ToDisplayString();
            if (name is "System.Collections.Generic.Queue<T>" or
                "System.Collections.Concurrent.ConcurrentQueue<T>") return "Enqueue";
            if (name is "System.Collections.Generic.Stack<T>" or
                "System.Collections.Concurrent.ConcurrentStack<T>") return "Push";
        }
        return "Add";
    }

    internal static ITypeSymbol ElementType(INamedTypeSymbol type, ITypeSymbol objectType) =>
        GenericInterface(type, "System.Collections.Generic.IEnumerable<T>")?.TypeArguments[0] ??
        (type.TypeArguments.Length == 0 ? objectType : type.TypeArguments[0]);

    private static INamedTypeSymbol? GenericInterface(INamedTypeSymbol type, string name) =>
        type.OriginalDefinition.ToDisplayString() == name ? type : type.AllInterfaces.FirstOrDefault(item =>
            item.OriginalDefinition.ToDisplayString() == name);

    internal static ITypeSymbol? GenericElement(INamedTypeSymbol type) =>
        GenericInterface(type, "System.Collections.Generic.IEnumerable<T>")?.TypeArguments[0];

    internal static bool UseCollectionInterface(INamedTypeSymbol type) => KnownAddMethod(type) is null &&
        GenericInterface(type, "System.Collections.Generic.ICollection<T>") is not null;

    internal static string? ImmutableSequenceFactory(INamedTypeSymbol type) => type.OriginalDefinition.ToDisplayString() switch
    {
        "System.Collections.Immutable.ImmutableArray<T>" => "System.Collections.Immutable.ImmutableArray",
        "System.Collections.Immutable.ImmutableList<T>" or "System.Collections.Immutable.IImmutableList<T>" =>
            "System.Collections.Immutable.ImmutableList",
        "System.Collections.Immutable.ImmutableHashSet<T>" or "System.Collections.Immutable.IImmutableSet<T>" =>
            "System.Collections.Immutable.ImmutableHashSet",
        "System.Collections.Immutable.ImmutableSortedSet<T>" => "System.Collections.Immutable.ImmutableSortedSet",
        "System.Collections.Immutable.ImmutableQueue<T>" or "System.Collections.Immutable.IImmutableQueue<T>" =>
            "System.Collections.Immutable.ImmutableQueue",
        "System.Collections.Immutable.ImmutableStack<T>" or "System.Collections.Immutable.IImmutableStack<T>" =>
            "System.Collections.Immutable.ImmutableStack",
        _ => null
    };

    internal static string? ImmutableMapFactory(INamedTypeSymbol type) => type.OriginalDefinition.ToDisplayString() switch
    {
        "System.Collections.Immutable.ImmutableDictionary<TKey, TValue>" or
            "System.Collections.Immutable.IImmutableDictionary<TKey, TValue>" =>
            "System.Collections.Immutable.ImmutableDictionary",
        "System.Collections.Immutable.ImmutableSortedDictionary<TKey, TValue>" =>
            "System.Collections.Immutable.ImmutableSortedDictionary",
        _ => null
    };

    private static bool IsKnownMap(INamedTypeSymbol type) => ImmutableMapFactory(type) is not null ||
        type.OriginalDefinition.ToDisplayString() is
        "System.Collections.Generic.Dictionary<TKey, TValue>" or
        "System.Collections.Generic.SortedDictionary<TKey, TValue>" or
        "System.Collections.Generic.SortedList<TKey, TValue>" or
        "System.Collections.Concurrent.ConcurrentDictionary<TKey, TValue>" or
        "System.Collections.ObjectModel.ReadOnlyDictionary<TKey, TValue>" or
        "System.Collections.Generic.IDictionary<TKey, TValue>" or
        "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>";

    internal static bool IsMap(INamedTypeSymbol type) => IsKnownMap(type) ||
        GenericInterface(type, "System.Collections.Generic.IDictionary<TKey, TValue>") is not null ||
        GenericInterface(type, "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>") is not null;

    private static INamedTypeSymbol MapContract(INamedTypeSymbol type) =>
        GenericInterface(type, "System.Collections.Generic.IDictionary<TKey, TValue>") ??
        GenericInterface(type, "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>") ?? type;

    internal static ITypeSymbol MapKeyType(INamedTypeSymbol type) => MapContract(type).TypeArguments[0];
    internal static ITypeSymbol MapValueType(INamedTypeSymbol type) => MapContract(type).TypeArguments[1];
    internal static bool UseMapInterface(INamedTypeSymbol type) => IsMap(type) && !IsKnownMap(type);

    internal static bool CanDeserialize(INamedTypeSymbol type)
    {
        if (type.OriginalDefinition.ToDisplayString() is "System.Collections.Concurrent.ConcurrentBag<T>" or
            "System.Collections.Concurrent.BlockingCollection<T>" or "System.Collections.BitArray" or
            "System.Collections.ObjectModel.ReadOnlyCollection<T>" or
            "System.Collections.ObjectModel.ReadOnlyObservableCollection<T>" or
            "System.Collections.ObjectModel.ReadOnlyDictionary<TKey, TValue>") return false;
        if (IsMap(type) && !IsKnownMap(type)) return type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Class &&
            !type.IsAbstract && type.InstanceConstructors.Any(ctor => ctor.Parameters.Length == 0 &&
                ctor.DeclaredAccessibility == Accessibility.Public) && GenericInterface(type,
                "System.Collections.Generic.IDictionary<TKey, TValue>") is not null;
        if (KnownAddMethod(type) is not null || ImmutableSequenceFactory(type) is not null || IsMap(type)) return true;
        return type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Class && !type.IsAbstract &&
            type.InstanceConstructors.Any(ctor => ctor.Parameters.Length == 0 &&
                ctor.DeclaredAccessibility == Accessibility.Public) &&
            (UseCollectionInterface(type) || CustomAddMethod(type) is "Enqueue" or "Push");
    }

    internal static string Constructor(INamedTypeSymbol type)
    {
        if (type.TypeKind != Microsoft.CodeAnalysis.TypeKind.Interface) return CodecApiEmitter.TypeName(type);
        string definition = type.OriginalDefinition.ToDisplayString();
        if (type.TypeArguments.Length == 0) return "System.Collections.ArrayList";
        string element = CodecApiEmitter.TypeName(type.TypeArguments[0]);
        if (IsMap(type)) return $"System.Collections.Generic.Dictionary<{element}, " +
            CodecApiEmitter.TypeName(type.TypeArguments[1]) + ">";
        return definition is "System.Collections.Generic.ISet<T>" or "System.Collections.Generic.IReadOnlySet<T>" ?
            $"System.Collections.Generic.HashSet<{element}>" : $"System.Collections.Generic.List<{element}>";
    }

    internal static void EmitMapWriter(List<CsNode> body, INamedTypeSymbol type, CodecEmitter owner)
    {
        body.Add(NodeCall("w.WriteStartObject"));
        if (type.IsReferenceType && ImmutableMapFactory(type) is null)
            body.Add(NodeCall("WriteReferenceId", Cs.Symbol("w"), Cs.Symbol("value"), Cs.Symbol("options")));
        var key = NodeMember(Cs.Symbol("item"), "Key");
        var policy = Cs.Symbol("DictionaryKeyPolicy", new()
        { Target = Cs.Symbol("options"), ConditionalAccess = true });
        var converted = NodeCall("ApplyNamingPolicy", policy, key);
        var keyType = MapKeyType(type);
        var valueType = MapValueType(type);
        int keyIndex = owner.RegisterMapKey(keyType);
        bool dateKey = IsDateKey(keyType);
        bool standardKey = SupportsKey(keyType);
        CsNode name = !standardKey ? Cs.Value("?") : dateKey ? NonNull(NodeCall("keyDocument.RootElement.GetString")) :
            keyType.TypeKind == Microsoft.CodeAnalysis.TypeKind.Enum ?
            NodeCall($"EnumKey_{owner.Register(keyType)}", key, Cs.Symbol("options")) : KeyName(keyType, key, converted);
        var fallback = new List<CsNode>();
        if (dateKey) fallback.Add(Cs.Symbol("keyDocument", new()
        {
            AsVar = true,
            Using = true,
            Value = NodeCall("JsonDocument.Parse", NodeCall($"Encode_{owner.Register(keyType)}", key))
        }));
        if (standardKey) fallback.Add(NodeAssign(Cs.Symbol("name"), name));
        if (keyType.SpecialType is SpecialType.System_Single or SpecialType.System_Double || WideNumberEmitter.IsFloating(keyType))
        {
            var finite = Cs.Symbol("IsFinite", new()
            {
                Target = TypeRef(TypeName(keyType)),
                AsCall = true,
                Arguments = Cs.Compound(key)
            });
            fallback.Add(NodeIf(Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { finite } }),
                NodeThrow(NodeNew("ArgumentException"))));
        }
        CsNode propertyName = dateKey ? NodeCall("JsonEncodedText.Encode", Cs.Symbol("name"), NodeMember(
            TypeRef("global::System.Text.Encodings.Web.JavaScriptEncoder"), "UnsafeRelaxedJsonEscaping")) :
            Cs.Symbol("name");
        var customWritten = NodeCall($"TryWriteMapKey_{keyIndex}", Cs.Symbol("w"), key, Cs.Symbol("options"));
        if (keyType.SpecialType == SpecialType.System_Object)
            fallback.Add(NodeIf(Cs.Symbol(new()
            {
                Operator = CsOperator.Not,
                Parts = new CsNode[] { NodeCall("TryWriteObjectMapKey", Cs.Symbol("w"), key, Cs.Symbol("options")) }
            }), NodeThrow(NodeNew("NotSupportedException"))));
        else fallback.Add(standardKey ? NodeCall("w.WritePropertyName", propertyName) : NodeThrow(NodeNew("NotSupportedException")));
        var itemBody = new List<CsNode> { NodeVar("name", Cs.Value("?")), NodeIf(Cs.Symbol(new()
            { Operator = CsOperator.Not, Parts = new CsNode[] { customWritten } }), fallback.ToArray()) };
        itemBody.Add(NodeCall($"Write_{owner.Register(valueType)}", NumberHandlingEmitter.Forward(
                valueType, Cs.Symbol("w"), NodeMember(Cs.Symbol("item"), "Value"), Cs.Symbol("options"),
                NodeBinary(CsOperator.Add, NodeBinary(CsOperator.Add, Cs.Symbol("path"), Cs.Value(".")),
                    Cs.Symbol("name")))));
        body.Add(NodeEach("item", Cs.Symbol("value"), itemBody.ToArray()));
        body.Add(NodeCall("w.WriteEndObject"));
    }

    private static CsNode KeyName(ITypeSymbol type, CsNode key, CsNode converted)
    {
        if (type.SpecialType == SpecialType.System_String) return converted;
        if (type.ToDisplayString() == "System.Uri") return NodeMember(key, "OriginalString");
        CsNode[] arguments = CodecEmitter.NumericGetter(type.SpecialType) is null && !WideNumberEmitter.IsWide(type) ? [] :
            [NodeMember(TypeRef("global::System.Globalization.CultureInfo"), "InvariantCulture")];
        return Cs.Symbol("ToString", new() { Target = key, AsCall = true, Arguments = Cs.Compound(arguments) });
    }

    internal static List<CsNode> ReadMapKey(ITypeSymbol type, CodecEmitter owner)
    {
        var name = NodeMember(Cs.Symbol("v"), "Name");
        return [NodeVar("key", NodeCall($"ReadMapKey_{owner.RegisterMapKey(type)}", name, Cs.Symbol("options")))];
    }

    internal static List<CsNode> ReadMapKeyDefault(ITypeSymbol type, CodecEmitter owner)
    {
        var name = Cs.Symbol("name");
        if (type.SpecialType == SpecialType.System_String) return [NodeVar("key", name)];
        if (type.SpecialType == SpecialType.System_Char)
            return [NodeIf(NodeBinary(CsOperator.NotEqual, NodeMember(name, "Length"), Cs.Value(1)),
                NodeThrow(NodeNew("JsonException"))), NodeVar("key", Index(name, Cs.Value(0)))];
        if (IsDateKey(type))
        {
            var escaped = Cs.Symbol("ToString", new()
            {
                Target = NodeCall("JsonEncodedText.Encode", name),
                AsCall = true
            });
            var json = NodeBinary(CsOperator.Add, NodeBinary(CsOperator.Add, Cs.Value("\""), escaped), Cs.Value("\""));
            return [NodeVar("key", NodeCall($"Decode_{owner.Register(type)}", json))];
        }
        if (type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Enum)
            return [NodeVar("key", NodeCall($"ReadEnumString_{owner.Register(type)}", name,
                NodeCall($"EnumAllowsInteger_{owner.Register(type)}", Cs.Symbol("options"), Cs.Value(null)),
                NodeCall($"EnumNamesFor_{owner.Register(type)}", Cs.Symbol("options"), Cs.Value(null))))];
        if (CodecEmitter.NumericGetter(type.SpecialType) is null && !WideNumberEmitter.IsWide(type))
            return ReadNonIntegerKey(type, name);
        var parse = Cs.Symbol("TryParse", new()
        {
            Target = TypeRef(TypeName(type)),
            AsCall = true,
            Arguments = Cs.Compound(name, NodeMember(TypeRef("global::System.Globalization.NumberStyles"),
                    WideNumberEmitter.IsWide(type) && !WideNumberEmitter.IsFloating(type) ? "AllowLeadingSign" :
                    CodecEmitter.IntegralGetter(type.SpecialType) is null ? "Float" : "AllowLeadingSign"),
                NodeMember(TypeRef("global::System.Globalization.CultureInfo"), "InvariantCulture"), OutVar("key"))
        });
        return [NodeIf(Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { parse } }),
            NodeThrow(NodeNew("JsonException")))];
    }

    private static bool IsDateKey(ITypeSymbol type) => type.ToDisplayString() is "System.DateTime" or
        "System.DateTimeOffset" or "System.DateOnly" or "System.TimeOnly" or "System.TimeSpan";

    private static List<CsNode> ReadNonIntegerKey(ITypeSymbol type, CsNode name)
    {
        var arguments = new List<CsNode> { name };
        string method = "TryParse";
        if (type.ToDisplayString() == "System.Guid")
        {
            method = "TryParseExact";
            arguments.Add(Cs.Value("D"));
        }
        if (type.ToDisplayString() == "System.Uri")
        {
            method = "TryCreate";
            arguments.Add(NodeMember(Cs.Symbol("UriKind"), "RelativeOrAbsolute"));
        }
        arguments.Add(OutVar("key"));
        var parse = Cs.Symbol(method, new()
        { Target = TypeRef(TypeName(type)), AsCall = true, Arguments = Cs.Compound(arguments.ToArray()) });
        CsNode failed = Cs.Symbol(new() { Operator = CsOperator.Not, Parts = new CsNode[] { parse } });
        if (type.SpecialType == SpecialType.System_Boolean)
            failed = NodeBinary(CsOperator.OrElse, NodeBinary(CsOperator.NotEqual, name,
                Cs.Symbol("Trim", new() { Target = name, AsCall = true })), failed);
        if (type.ToDisplayString() == "System.Guid")
            failed = NodeBinary(CsOperator.OrElse, NodeBinary(CsOperator.NotEqual,
                NodeMember(name, "Length"), Cs.Value(36)), failed);
        return [NodeIf(failed, NodeThrow(NodeNew("JsonException")))];
    }
}

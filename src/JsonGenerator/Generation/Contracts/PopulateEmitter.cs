using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Model;
using static Neemle.Json.Generator.CodecApiEmitter;

namespace Neemle.Json.Generator;

static class PopulateEmitter
{
    private const string CreationAttribute =
        "System.Text.Json.Serialization.JsonObjectCreationHandlingAttribute";

    public static bool IsReadonly(IPropertySymbol property) =>
        !CodecEmitter.CanSet(property);

    internal static bool IsReadOnlyEnumerable(ITypeSymbol type) => type is INamedTypeSymbol named &&
        named.OriginalDefinition.ToDisplayString() is "System.Collections.Generic.IEnumerable<T>" or
            "System.Collections.Generic.IReadOnlyCollection<T>" or "System.Collections.Generic.IReadOnlyList<T>";

    public static bool CanHandleReadonly(IPropertySymbol property)
    {
        if (!IsReadonly(property) || property.GetMethod?.DeclaredAccessibility != Accessibility.Public ||
            property.SetMethod is not null) return false;
        int? handling = ExplicitHandling(property);
        if (handling is not null and not (0 or 1)) return false;
        if (IsReadOnlyEnumerable(property.Type)) return handling != 1;
        if (property.Type is INamedTypeSymbol collection && FrameworkCollection.AddMethod(collection) is not null &&
            !FrameworkCollection.CanDeserialize(collection)) return true;
        return CollectionElementType(property) is not null || MapTypes(property) is not null ||
            ObjectType(property) is not null || property.Type.IsValueType;
    }

    internal static INamedTypeSymbol? ObjectType(ISymbol property) => MemberType(property) is INamedTypeSymbol
    {
        TypeKind: Microsoft.CodeAnalysis.TypeKind.Class or Microsoft.CodeAnalysis.TypeKind.Struct,
        IsAbstract: false, SpecialType: SpecialType.None
    } type &&
        !type.ContainingAssembly.Identity.Name.StartsWith("System.", StringComparison.Ordinal) &&
        CollectionElementType(property) is null && MapTypes(property) is null &&
        FrameworkCollection.AddMethod(type) is null && !FrameworkCollection.IsMap(type) &&
        ConverterAttributeEmitter.Converter(type, type) is null &&
        !type.InstanceConstructors.Any(c => CodecEmitter.IsJsonConstructor(c) && c.Parameters.Length > 0) &&
        type.InstanceConstructors.Any(c => CodecEmitter.AccessibleConstructor(c) && c.Parameters.Length == 0) &&
        CodecEmitter.Properties(type).All(p => CodecEmitter.CanSet(p) || p.Type.IsValueType ||
            CollectionElementType(p) is not null || MapTypes(p) is not null) ? type : null;

    public static ITypeSymbol? CollectionElementType(ISymbol property)
    {
        if (MemberType(property) is not INamedTypeSymbol type) return null;
        if (ConverterAttributeEmitter.Converter(type, type) is not null) return null;
        if (type.OriginalDefinition.ToDisplayString() is "System.Collections.Generic.List<T>" or
            "System.Collections.Generic.HashSet<T>" or "System.Collections.Generic.SortedSet<T>" or
            "System.Collections.Generic.ICollection<T>" or "System.Collections.Generic.IList<T>" or
            "System.Collections.Generic.ISet<T>" or "System.Collections.ObjectModel.Collection<T>" or
            "System.Collections.ObjectModel.ObservableCollection<T>" or "System.Collections.Generic.Queue<T>" or
            "System.Collections.Generic.Stack<T>" or "System.Collections.Generic.LinkedList<T>" or
            "System.Collections.Concurrent.ConcurrentQueue<T>" or "System.Collections.Concurrent.ConcurrentStack<T>")
            return type.TypeArguments[0];
        string? method = FrameworkCollection.AddMethod(type);
        return method is not null && type.TypeKind != Microsoft.CodeAnalysis.TypeKind.Interface &&
            (FrameworkCollection.UseCollectionInterface(type) || method is "Enqueue" or "Push") ?
            FrameworkCollection.GenericElement(type) : null;
    }

    internal static (ITypeSymbol Key, ITypeSymbol Value)? MapTypes(ISymbol property) => MemberType(property) is
        INamedTypeSymbol type && ConverterAttributeEmitter.Converter(type, type) is null &&
        type.OriginalDefinition.ToDisplayString() is
            "System.Collections.Generic.Dictionary<TKey, TValue>" or
            "System.Collections.Generic.IDictionary<TKey, TValue>" or
            "System.Collections.Generic.SortedDictionary<TKey, TValue>" or
            "System.Collections.Generic.SortedList<TKey, TValue>" or
            "System.Collections.Concurrent.ConcurrentDictionary<TKey, TValue>" ?
            (type.TypeArguments[0], type.TypeArguments[1]) : null;

    public static void EmitValidation(List<CsNode> output, IEnumerable<IPropertySymbol> properties)
    {
        foreach (var property in properties)
        {
            if (ExplicitHandling(property) != 1) continue;
            bool converter = ConverterAttributeEmitter.Converter(property, property.Type) is not null ||
                ConverterAttributeEmitter.Converter(property.Type, property.Type) is not null;
            bool polymorphic = property.Type is INamedTypeSymbol named && PolymorphismEmitter.HasContract(named);
            if (converter || polymorphic)
            {
                output.Add(NodeIf(ValueKind("e", "Undefined"), NodeThrow(NodeNew("InvalidOperationException"))));
                continue;
            }
            if (IsReadonly(property))
                output.Add(NodeIf(NodeBinary(CsOperator.Equal, Cs.Symbol("IgnoreReadOnlyProperties", new()
                { Target = Cs.Symbol("options"), ConditionalAccess = true }), Cs.Value(true)),
                    NodeThrow(NodeNew("InvalidOperationException"))));
            if (!IsReadonly(property) || !property.Type.IsValueType) continue;
            string message = "The property '" + property.Name + "' requires a setter for populate.";
            output.Add(NodeIf(ValueKind("e", "Undefined"),
                NodeThrow(NodeNew("InvalidOperationException", Cs.Value(message)))));
            return;
        }
    }

    public static void EmitReadonlyRead(List<CsNode> output, IPropertySymbol property, int elementReadIndex,
        int? typeHandling)
    {
        if (!CanHandleReadonly(property)) throw new InvalidOperationException("Unsupported read-only property.");
        if (CollectionElementType(property) is null) return;
        if (elementReadIndex < 0) throw new ArgumentOutOfRangeException(nameof(elementReadIndex));
        CsNode condition = Condition(property);
        string member = SyntaxFacts.GetKeywordKind(property.Name) == SyntaxKind.None
            ? property.Name : "@" + property.Name;
        string input = "p_" + property.Name;
        var path = NodeBinary(CsOperator.Add, NodeBinary(CsOperator.Add, Cs.Symbol("path"), Cs.Value(".")),
            Cs.Value(property.Name));
        var read = NodeCall($"Read_{elementReadIndex}", [Cs.Symbol("item"), Cs.Symbol("options"), path,
            .. NumberHandlingEmitter.Arguments(property, typeHandling)]);
        var loop = NodeEach("item", NodeCall(input + ".EnumerateArray"), AddToCollection(property, read));
        output.Add(NodeIf(condition, NodeIf(ValueKind(input, "Array"), NodeThrow(NodeNew("JsonException"))),
            NodeVar("populateTarget", NodeMember(Cs.Symbol("result"), member)),
            NodeIf(NullTest(Cs.Symbol("populateTarget")), NodeThrow(NodeNew("JsonException"))), loop));
    }

    internal static CsNode[] WritableCollectionRead(ISymbol property, MemberAccessEmitter access, (int Element, int Type) indices,
        CsNode fallback, int? typeHandling)
    {
        string input = "p_" + property.Name;
        var array = NodeBinary(CsOperator.Equal, NodeMember(Cs.Symbol(input), "ValueKind"), NodeMember(Cs.Symbol("JsonValueKind"),
            "Array"));
        var path = NodeBinary(CsOperator.Add, NodeBinary(CsOperator.Add, Cs.Symbol("path"), Cs.Value(".")),
            CodecEmitter.JsonNameExpression(property));
        var read = NodeCall($"Read_{indices.Element}", [Cs.Symbol("item"), Cs.Symbol("options"), path,
            .. NumberHandlingEmitter.Arguments(property, typeHandling)]);
        var loop = NodeEach("item", NodeCall(input + ".EnumerateArray"), AddToCollection(property, read));
        var condition = PopulateCondition(property, array, indices.Type);
        return [NodeIf(condition, NodeVar("populateTarget", access.Read(property, "result")),
                NodeIf(NullTest(Cs.Symbol("populateTarget"), true), loop, Cs.Instruction(InstructionKind.Return))),
            fallback];
    }

    internal static CsNode[] WritableMapRead(ISymbol property, MemberAccessEmitter access,
        (int Key, int Value, int Type) indices, CsNode fallback, int? typeHandling)
    {
        string input = "p_" + property.Name;
        var isObject = NodeBinary(CsOperator.Equal, NodeMember(Cs.Symbol(input), "ValueKind"),
            NodeMember(Cs.Symbol("JsonValueKind"), "Object"));
        var condition = PopulateCondition(property, isObject, indices.Type);
        var loop = MapLoop(property, (indices.Key, indices.Value), typeHandling);
        return [NodeIf(condition, NodeVar("populateTarget", access.Read(property, "result")),
                NodeIf(NullTest(Cs.Symbol("populateTarget"), true), loop, Cs.Instruction(InstructionKind.Return))),
            fallback];
    }

    internal static CsNode ReadonlyMapRead(ISymbol property, MemberAccessEmitter access,
        (int Key, int Value) indices, int? typeHandling)
    {
        string input = "p_" + property.Name;
        return NodeIf(Condition(property), NodeIf(ValueKind(input, "Object"), NodeThrow(NodeNew("JsonException"))),
            NodeVar("populateTarget", access.Read(property, "result")),
            NodeIf(NullTest(Cs.Symbol("populateTarget")), NodeThrow(NodeNew("JsonException"))), MapLoop(property, indices, typeHandling));
    }

    internal static CsNode ReadonlyCollectionRead(ISymbol member, MemberAccessEmitter access, int elementIndex,
        int? typeHandling)
    {
        string input = "p_" + member.Name;
        var path = NodeBinary(CsOperator.Add, NodeBinary(CsOperator.Add, Cs.Symbol("path"), Cs.Value(".")),
            CodecEmitter.JsonNameExpression(member));
        var read = NodeCall($"Read_{elementIndex}", [Cs.Symbol("item"), Cs.Symbol("options"), path,
            .. NumberHandlingEmitter.Arguments(member, typeHandling)]);
        var loop = NodeEach("item", NodeCall(input + ".EnumerateArray"), AddToCollection(member, read));
        return NodeIf(Condition(member), NodeIf(ValueKind(input, "Array"), NodeThrow(NodeNew("JsonException"))),
            NodeVar("populateTarget", access.Read(member, "result")),
            NodeIf(NullTest(Cs.Symbol("populateTarget")), NodeThrow(NodeNew("JsonException"))), loop);
    }

    internal static CsNode[] ObjectRead(ISymbol property, MemberAccessEmitter access, int index,
        CsNode? fallback)
    {
        string input = "p_" + property.Name;
        var isObject = NodeBinary(CsOperator.Equal, NodeMember(Cs.Symbol(input), "ValueKind"),
            NodeMember(Cs.Symbol("JsonValueKind"), "Object"));
        var path = NodeBinary(CsOperator.Add, NodeBinary(CsOperator.Add, Cs.Symbol("path"), Cs.Value(".")),
            CodecEmitter.JsonNameExpression(property));
        var populate = NodeCall($"Populate_{index}", Cs.Symbol(input),
            Arg(ArgumentModifier.Ref, Cs.Symbol("populateTarget")), Cs.Symbol("options"), path);
        var condition = PopulateCondition(property, isObject, index);
        if (MemberType(property).IsValueType)
        {
            var branchValue = NodeIf(condition, NodeVar("populateTarget", access.Read(property, "result")), populate,
                access.Write(property, "result", Cs.Symbol("populateTarget")), Cs.Instruction(InstructionKind.Return));
            return fallback is null ? [branchValue] : [branchValue, fallback];
        }
        var branch = NodeIf(condition, NodeVar("populateTarget", access.Read(property, "result")),
            NodeIf(NullTest(Cs.Symbol("populateTarget"), true), populate, Cs.Instruction(InstructionKind.Return)));
        return fallback is null ? [branch] : [branch, fallback];
    }

    private static CsNode MapLoop(ISymbol property, (int Key, int Value) indices, int? typeHandling)
    {
        string input = "p_" + property.Name;
        var key = NodeCall($"ReadMapKey_{indices.Key}", NodeMember(Cs.Symbol("item"), "Name"), Cs.Symbol("options"));
        var path = NodeBinary(CsOperator.Add, NodeBinary(CsOperator.Add, Cs.Symbol("path"), Cs.Value(".")),
            CodecEmitter.JsonNameExpression(property));
        var value = NodeCall($"Read_{indices.Value}", [NodeMember(Cs.Symbol("item"), "Value"), Cs.Symbol("options"), path,
            .. NumberHandlingEmitter.Arguments(property, typeHandling)]);
        return NodeEach("item", NodeCall(input + ".EnumerateObject"), NodeAssign(Index(Cs.Symbol("populateTarget"), key), value));
    }

    private static CsNode Condition(ISymbol property) => (ExplicitHandling(property) ??
        ExplicitHandling(property.ContainingType)) switch
    {
        1 => Cs.Literal(true),
        0 => Cs.Literal(false),
        _ => NodeBinary(CsOperator.Equal, Cs.Symbol("PreferredObjectCreationHandling", new()
        { Target = Cs.Symbol("options"), ConditionalAccess = true }),
            NodeMember(Cs.Symbol(new()
            {
                Operation = SymbolOperation.TypeReference,
                Type = NodeType("global::System.Text.Json.Serialization.JsonObjectCreationHandling")
            }),
                "Populate"))
    };

    private static CsNode PopulateCondition(ISymbol member, CsNode kind, int index) => NodeBinary(CsOperator.AndAlso,
        NodeBinary(CsOperator.AndAlso, Condition(member), kind), Cs.Symbol(new()
        { Operator = CsOperator.Not, Parts = new CsNode[] { NodeCall($"HasCustomConverter_{index}", Cs.Symbol("options")) } }));

    private static CsNode AddToCollection(ISymbol property, CsNode item)
    {
        var type = (INamedTypeSymbol)MemberType(property);
        if (!FrameworkCollection.UseCollectionInterface(type))
            return NodeCall("populateTarget." + FrameworkCollection.AddMethod(type), item);
        string collection = "global::System.Collections.Generic.ICollection<" +
            TypeName(CollectionElementType(property)!) + ">";
        return Cs.Symbol("Add", new()
        {
            Target = Cast(collection, Cs.Symbol("populateTarget")),
            AsCall = true,
            Arguments = Cs.Compound(item)
        });
    }

    private static ITypeSymbol MemberType(ISymbol member) => member switch
    {
        IPropertySymbol property => property.Type,
        IFieldSymbol field => field.Type,
        _ => throw new ArgumentException("Expected a property or field.", nameof(member))
    };

    private static CsSymbol ValueKind(string input, string kind) => NodeBinary(CsOperator.NotEqual,
        NodeMember(Cs.Symbol(input), "ValueKind"), NodeMember(Cs.Symbol("JsonValueKind"), kind));

    private static int? ExplicitHandling(ISymbol property)
    {
        var attribute = property.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() ==
            CreationAttribute);
        if (attribute is null) return null;
        return attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is int value
            ? value : -1;
    }
}

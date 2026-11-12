using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Model;
using static CodecApiEmitter;

static class PopulateEmitter
{
    private const string CreationAttribute =
        "System.Text.Json.Serialization.JsonObjectCreationHandlingAttribute";

    public static bool IsReadonly(IPropertySymbol property) =>
        property.SetMethod is not { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false };

    public static bool CanHandleReadonly(IPropertySymbol property)
    {
        if (!IsReadonly(property) || property.GetMethod?.DeclaredAccessibility != Accessibility.Public ||
            property.SetMethod is not null) return false;
        int? handling = ExplicitHandling(property);
        if (handling is not null and not (0 or 1)) return false;
        return ListElementType(property) is not null || property.Type.IsValueType;
    }

    public static ITypeSymbol? ListElementType(IPropertySymbol property) =>
        property.Type is INamedTypeSymbol type &&
        type.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>"
            ? type.TypeArguments[0] : null;

    public static void EmitValidation(List<CsNode> output, IEnumerable<IPropertySymbol> properties)
    {
        foreach (var property in properties)
        {
            if (!IsReadonly(property) || !property.Type.IsValueType || ExplicitHandling(property) != 1) continue;
            string message = "The property '" + property.Name + "' requires a setter for populate.";
            output.Add(NodeIf(ValueKind("e", "Undefined"),
                NodeThrow(NodeNew("InvalidOperationException", Cs.Value(message)))));
            return;
        }
    }

    public static void EmitReadonlyRead(List<CsNode> output, IPropertySymbol property, int elementReadIndex)
    {
        if (!CanHandleReadonly(property)) throw new InvalidOperationException("Unsupported read-only property.");
        if (ListElementType(property) is null) return;
        if (elementReadIndex < 0) throw new ArgumentOutOfRangeException(nameof(elementReadIndex));
        CsNode condition = ExplicitHandling(property) switch
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
        string member = SyntaxFacts.GetKeywordKind(property.Name) == SyntaxKind.None
            ? property.Name : "@" + property.Name;
        string input = "p_" + property.Name;
        var path = NodeBinary(CsOperator.Add, NodeBinary(CsOperator.Add, Cs.Symbol("path"), Cs.Value(".")),
            Cs.Value(property.Name));
        var read = NodeCall($"Read_{elementReadIndex}", Cs.Symbol("item"), Cs.Symbol("options"), path);
        var loop = NodeEach("item", NodeCall(input + ".EnumerateArray"), NodeCall("populateTarget.Add", read));
        output.Add(NodeIf(condition, NodeIf(ValueKind(input, "Array"), NodeThrow(NodeNew("JsonException"))),
            NodeVar("populateTarget", NodeMember(Cs.Symbol("result"), member)),
            NodeIf(NullTest(Cs.Symbol("populateTarget")), NodeThrow(NodeNew("JsonException"))), loop));
    }

    private static CsSymbol ValueKind(string input, string kind) => NodeBinary(CsOperator.NotEqual,
        NodeMember(Cs.Symbol(input), "ValueKind"), NodeMember(Cs.Symbol("JsonValueKind"), kind));

    private static int? ExplicitHandling(IPropertySymbol property)
    {
        var attribute = property.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() ==
            CreationAttribute);
        if (attribute is null) return null;
        return attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is int value
            ? value : -1;
    }
}

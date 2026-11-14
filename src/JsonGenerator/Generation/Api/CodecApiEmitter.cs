using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Neemle.Json.Generator;

static class CodecApiEmitter
{
    private static readonly Dictionary<ITypeSymbol, string> AnonymousNames = new(SymbolEqualityComparer.Default);

    internal static void SetAnonymousName(ITypeSymbol type, string name) => AnonymousNames[type] = name;

    internal static void EmitPublicApi(int i, string name, ITypeSymbol type, TypeBuilder<FileBuilder> root)
    {
        Public(root, "string", $"Encode_{i}").Parameter(InputType(name, type.IsReferenceType), "value")
            .Parameter(Cs.Type("JsonSerializerOptions").Nullable(),
            "options", @default: Cs.Literal(null)).Body(Cs.Tree(
            NodeCall("ValidateRuntimeOptions", Cs.Symbol("options")), UsingMemoryStream(),
            Using("w", Cs.Symbol(new()
            {
                Type = Cs.Type("Utf8JsonWriter"),
                AsNew = true,
                Arguments = Cs.Compound(Cs.Symbol("ms"), WriterOptions())
            })),
            NodeCall($"Write_{i}", Cs.Symbol("w"), Cs.Symbol("value"), Cs.Symbol("options"), Cs.Value("$")),
            NodeCall("w.Flush"), Return(NodeCall("Encoding.UTF8.GetString", NodeCall("ms.ToArray"))))).End();
        foreach (string inputType in new[] { "string", "byte[]" })
        {
            CsNode jsonText = inputType == "byte[]" ? NodeCall("Encoding.UTF8.GetString", Cs.Symbol("json")) :
                Cs.Symbol("json");
            var read = new CsBody
            {
                Tree = Cs.Tree(Return(NodeCall($"Read_{i}",
                Cs.Symbol("RootElement", new() { Target = Cs.Symbol("d") }), Cs.Symbol("options"), Cs.Value("$"))))
            };
            var catchBody = new CsBody
            {
                Tree = Cs.Tree(NodeThrow(NodeCall("PositionedScalarError", Cs.Symbol("error"),
                jsonText, Cs.Symbol("options"))))
            };
            Public(root, name, $"Decode_{i}")
                .Parameter(inputType == "byte[]" ? Cs.Type("byte").Array() : Cs.Type("string"), "json")
                .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options", @default: Cs.Literal(null))
                .Body(Cs.Tree(NodeCall("ValidateRuntimeOptions", Cs.Symbol("options")),
                    Using("d", NodeCall("JsonDocument.Parse", Cs.Symbol("json"),
                    NodeCall("CreateDocumentOptions", Cs.Symbol("options"),
                        Cs.Value(DomValue.Kind(type) is "Element" or "Document" or "Node")))),
                    Cs.Try(read, new Values<CsCatch>([Cs.Catch(Cs.Type("ScalarJsonException"), catchBody, "error")])))).End();
        }
        EmitStreamApi(i, name, type.IsReferenceType, root);
        bool allowNull = type.IsReferenceType || type is INamedTypeSymbol named &&
            named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
        EmitDeclaredTypeApi(i, name, allowNull, root);
        JsonDomApiEmitter.Emit(i, name, allowNull, type.IsReferenceType, root);
    }

    internal static CsType InputType(string name, bool reference) => reference ? NodeType(name).Nullable() : NodeType(name);

    private static void EmitStreamApi(int i, string name, bool reference, TypeBuilder<FileBuilder> root)
    {
        foreach (bool declared in new[] { false, true })
        {
            var method = Public(root, "byte[]", $"EncodeBytes_{i}")
                .Parameter(declared ? NodeType("object?") : InputType(name, reference), "value");
            if (declared) method.Parameter(Cs.Type("Type"), "inputType");
            method.Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options", @default: Cs.Literal(null));
            var encoded = declared ? NodeCall($"Encode_{i}", Cs.Symbol("value"), Cs.Symbol("inputType"),
                Cs.Symbol("options")) : NodeCall($"Encode_{i}", Cs.Symbol("value"), Cs.Symbol("options"));
            method.Body(Cs.Tree(Return(NodeCall("Encoding.UTF8.GetBytes", encoded)))).End();
        }
        foreach (bool async in new[] { false, true })
        {
            var method = Public(root, async ? "Task" : "void", $"Encode{(async ? "Async" : "")}_{i}")
                .Parameter(Cs.Type("Stream"), "utf8Json").Parameter(InputType(name, reference), "value")
                .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options", @default: Cs.Literal(null));
            if (async) method.Async().Parameter(Cs.Type("CancellationToken"), "cancellationToken",
                @default: new CsExpression(ExpressionKind.Default, "", []));
            CsNode[] args = async ? [Cs.Symbol("bytes"), Cs.Symbol("cancellationToken")] : [Cs.Symbol("bytes")];
            var write = Cs.Symbol(async ? "WriteAsync" : "Write", new()
            {
                Target = Cs.Symbol("utf8Json"),
                AsCall = true,
                Await = async,
                Arguments = Cs.Compound(args)
            });
            method.Body(Cs.Tree(Cs.Symbol("bytes", new()
            {
                AsVar = true,
                Value = NodeCall($"EncodeBytes_{i}", Cs.Symbol("value"), Cs.Symbol("options"))
            }),
                write)).End();
        }
        EmitStreamReadApi(i, name, root);
    }

    private static void EmitStreamReadApi(int i, string name, TypeBuilder<FileBuilder> root)
    {
        foreach (bool async in new[] { false, true })
        {
            var method = Public(root, async ? $"ValueTask<{name}>" : name, $"Decode{(async ? "Async" : "")}_{i}")
                .Parameter(Cs.Type("Stream"), "utf8Json")
                .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options", @default: Cs.Literal(null));
            if (async) method.Async().Parameter(Cs.Type("CancellationToken"), "cancellationToken",
                @default: new CsExpression(ExpressionKind.Default, "", []));
            CsNode[] args = async ? [Cs.Symbol("ms"), Cs.Symbol("cancellationToken")] : [Cs.Symbol("ms")];
            var copy = Cs.Symbol(async ? "CopyToAsync" : "CopyTo", new()
            {
                Target = Cs.Symbol("utf8Json"),
                AsCall = true,
                Await = async,
                Arguments = Cs.Compound(args)
            });
            method.Body(Cs.Tree(UsingMemoryStream(),
                copy,
                Return(NodeCall($"Decode_{i}", NodeCall("ms.ToArray"), Cs.Symbol("options"))))).End();
        }
    }

    private static CsSymbol UsingMemoryStream() => Using("ms", Cs.Symbol(new()
    { Type = Cs.Type("MemoryStream"), AsNew = true }));

    private static CsSymbol Using(string name, CsNode value) => Cs.Symbol(name, new()
    { AsVar = true, Using = true, Value = value });

    private static void EmitDeclaredTypeApi(int i, string name, bool allowNull, TypeBuilder<FileBuilder> root)
    {
        foreach (bool encode in new[] { true, false })
        {
            string method = (encode ? "Encode_" : "Decode_") + i;
            var builder = Public(root, encode ? "string" : "object?", method)
                .Parameter(encode ? Cs.Type("object").Nullable() : Cs.Type("string"), encode ? "value" : "json")
                .Parameter(Cs.Type("Type"), "declared")
                .Parameter(Cs.Type("JsonSerializerOptions").Nullable(), "options", @default: Cs.Literal(null));
            CsNode argument = encode ? CoerceValue(name, allowNull) : Cs.Symbol("json");
            builder.Body(Cs.Tree(Cs.Instruction(InstructionKind.If, new()
            {
                Condition = NodeBinary(CsOperator.NotEqual, Cs.Symbol("declared"),
                        TypeOf(name)),
                Body = Cs.Tree(Cs.Instruction(InstructionKind.Throw, new()
                { Value = Cs.Symbol(new() { Type = Cs.Type("NotSupportedException"), AsNew = true }) }))
            }), Return(NodeCall(method, argument,
                    Cs.Symbol("options"))))).End();
        }
    }

    private static MethodBuilder<TypeBuilder<FileBuilder>> Public(TypeBuilder<FileBuilder> root, string type,
        string name)
        => root.Method(NodeType(type), name).Internal().Static();

    internal static CsSymbol WriterOptions()
    {
        CsNode Option(string name) => Cs.Symbol(name, new()
        {
            Target = Cs.Symbol("options"),
            ConditionalAccess = true
        });
        var indented = NodeBinary(CsOperator.Coalesce, Option("WriteIndented"), Cs.Value(false));
        var maxDepth = NodeBinary(CsOperator.Coalesce, Option("MaxDepth"), Cs.Value(0));
        var indent = NodeBinary(CsOperator.Coalesce, Option("IndentCharacter"), Cs.Value(' '));
        var indentSize = NodeBinary(CsOperator.Coalesce, Option("IndentSize"), Cs.Value(2));
        var newline = NodeBinary(CsOperator.Coalesce, Option("NewLine"),
            NodeMember(Cs.Symbol("Environment"), "NewLine"));
        return Cs.Symbol(new()
        {
            AsNew = true,
            Type = Cs.Type("JsonWriterOptions"),
            Initializer = Cs.Symbol(new()
            {
                AsInitializer = true,
                Parts = new CsNode[] { NodeAssign(Cs.Symbol("Indented"), indented),
            NodeAssign(Cs.Symbol("MaxDepth"), maxDepth),
            NodeAssign(Cs.Symbol("Encoder"), Option("Encoder")), NodeAssign(Cs.Symbol("IndentCharacter"), indent),
            NodeAssign(Cs.Symbol("IndentSize"), indentSize), NodeAssign(Cs.Symbol("NewLine"), newline) }
            })
        });
    }

    internal static CsInstruction Return(CsNode value) =>
        Cs.Instruction(InstructionKind.Return, new() { Value = value });

    internal static CsNode CoerceValue(string type, bool allowNull) => Cs.Symbol(new()
    {
        AsCall = true,
        Target = Cs.Symbol("CoerceValue", new() { TypeArguments = new[] { NodeType(type) } }),
        Arguments = Cs.Compound(Cs.Symbol("value"), Cs.Value(allowNull))
    });

    internal static CsSymbol NodeNew(string type, params CsNode[] args) => Cs.Symbol(new()
    { Type = NodeType(type), AsNew = true, Arguments = Cs.Compound(args) });

    internal static CsType NodeType(string name) => TypeSyntaxNode(SyntaxFactory.ParseTypeName(name));

    private static CsType TypeSyntaxNode(TypeSyntax type) => type switch
    {
        NullableTypeSyntax nullable => TypeSyntaxNode(nullable.ElementType).Nullable(),
        ArrayTypeSyntax array => array.RankSpecifiers.Aggregate(TypeSyntaxNode(array.ElementType),
            (element, rank) => element.Array(rank.Rank)),
        TupleTypeSyntax tuple => Cs.Tuple(tuple.Elements.Select(element => new TupleElement(TypeSyntaxNode(element.Type),
            element.Identifier.IsKind(SyntaxKind.None) ? null : element.Identifier.ValueText)).ToArray()),
        GenericNameSyntax generic => Cs.Type(generic.Identifier.Text)
            .Generic(generic.TypeArgumentList.Arguments.Select(TypeSyntaxNode).ToArray()),
        AliasQualifiedNameSyntax { Name: GenericNameSyntax generic } alias =>
            Cs.Type(alias.Alias.Identifier.Text + "::" + generic.Identifier.Text)
                .Generic(generic.TypeArgumentList.Arguments.Select(TypeSyntaxNode).ToArray()),
        QualifiedNameSyntax qualified => QualifiedType(qualified),
        _ => Cs.Type(type.ToString())
    };

    private static CsType QualifiedType(QualifiedNameSyntax name)
    {
        bool nested = name.Left.DescendantNodesAndSelf().OfType<GenericNameSyntax>().Any();
        string right = name.Right.Identifier.Text;
        CsType type = nested ? new CsType(right) { Form = TypeForm.Nested, Element = TypeSyntaxNode(name.Left) } :
            Cs.Type(name.Left + "." + right);
        return name.Right is GenericNameSyntax generic ?
            type.Generic(generic.TypeArgumentList.Arguments.Select(TypeSyntaxNode).ToArray()) : type;
    }

    internal static CsSymbol Index(CsNode target, params CsNode[] indices) => Cs.Symbol(new()
    { AsIndex = true, Target = target, Arguments = Cs.Compound(indices) });

    internal static CsSymbol Cast(string type, CsNode value) => Cs.Symbol(new()
    { AsCast = true, Type = NodeType(type), Value = value });

    internal static CsSymbol TypeOf(string type) => Cs.Symbol(new()
    { AsTypeOf = true, Type = NodeType(type == "object?[]" ? "object[]" : type) });

    internal static string TypeName(ITypeSymbol type) => AnonymousNames.TryGetValue(type, out var name) ? name :
        type is IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Object } ? "object?[]" :
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    internal static CsSymbol DefaultValue() => Cs.Symbol(new() { AsDefault = true });

    internal static CsSymbol NonNull(CsNode value) => Cs.Symbol(new()
    { Operation = SymbolOperation.NullForgiving, Value = value });

    internal static CsSymbol Arg(ArgumentModifier modifier, CsNode value) => Cs.Symbol(new()
    { Operation = SymbolOperation.Argument, ArgumentModifier = modifier, Value = value });

    internal static CsSymbol OutVar(string name) => Arg(ArgumentModifier.Out,
        Cs.Symbol(name, new() { Operation = SymbolOperation.Declaration, AsVar = true }));

    internal static CsSymbol TypeRef(string name) => Cs.Symbol(new()
    { Operation = SymbolOperation.TypeReference, Type = NodeType(name) });

    internal static CsSymbol NullTest(CsNode value, bool negate = false) => Cs.Symbol(new()
    {
        AsIs = true,
        Value = value,
        Pattern = negate ? Cs.NotPattern(Cs.ConstantPattern(Cs.Literal(null))) :
            Cs.ConstantPattern(Cs.Literal(null))
    });

    internal static CsSymbol NodeVar(string name, CsNode value) => Cs.Symbol(name, new()
    { AsVar = true, Value = value });

    internal static CsSymbol NodeAssign(CsNode target, CsNode value) => Cs.Symbol(new()
    { Target = target, AsAssignment = true, Value = value });

    internal static CsSymbol NodeMember(CsNode target, string name) => Cs.Symbol(name, new() { Target = target });

    internal static CsSymbol NodeBinary(CsOperator op, CsNode left, CsNode right) => Cs.Symbol(new()
    { Operator = op, Parts = new CsNode[] { left, right } });

    internal static CsInstruction NodeEach(string name, CsNode values, params CsNode[] body) =>
        Cs.Instruction(InstructionKind.ForEach, new()
        { Symbol = Cs.Symbol(name, new() { AsVar = true }), Value = values, Body = Cs.Tree(body) });

    internal static CsInstruction NodeIf(CsNode condition, params CsNode[] body) =>
        Cs.Instruction(InstructionKind.If, new() { Condition = condition, Body = Cs.Tree(body) });

    internal static CsInstruction NodeThrow(CsNode value) =>
        Cs.Instruction(InstructionKind.Throw, new() { Value = value });

    internal static CsSymbol NodeCall(string path, params CsNode[] arguments)
    {
        var parts = path.Split('.');
        CsNode? target = null;
        foreach (string part in parts.Take(parts.Length - 1)) target = Cs.Symbol(part, new() { Target = target });
        return Cs.Symbol(parts[^1], new() { Target = target, AsCall = true, Arguments = Cs.Compound(arguments) });
    }

    internal static CsExpression Invoke(string method, params CsExpression[] arguments) =>
        Cs.Call(Method(method), arguments);

    private static CsExpression Method(string path)
    {
        var parts = path.Split('.');
        return parts.Skip(1).Aggregate(Cs.Id(parts[0]), Cs.Member);
    }

}

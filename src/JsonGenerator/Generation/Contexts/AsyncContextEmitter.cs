using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Model;
using Neemle.CSharpComposer.Rendering;
using static Neemle.Json.Generator.CodecApiEmitter;

namespace Neemle.Json.Generator;

internal sealed class AsyncContextEmitter(string contextName = "GeneratedAsyncJsonContext")
{
    private readonly HashSet<ITypeSymbol> types = new(SymbolEqualityComparer.Default);
    private bool hasOptionsClassifier;
    internal IEnumerable<ITypeSymbol> KnownTypes => types;

    internal void RegisterOptionsClassifier() => hasOptionsClassifier = true;

    internal bool Register(ITypeSymbol type)
    {
        if (!ContainsAsync(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default))) return false;
        types.Add(type.WithNullableAnnotation(NullableAnnotation.None));
        return true;
    }

    internal bool RegisterUnsupported(ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol || type.IsRefLikeType || type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Error)
            return false;
        types.Add(type.WithNullableAnnotation(NullableAnnotation.None));
        return true;
    }

    private static bool ContainsAsync(ITypeSymbol type, HashSet<ITypeSymbol> visited)
    {
        if (!visited.Add(type)) return false;
        if (type is IArrayTypeSymbol array) return ContainsAsync(array.ElementType, visited);
        if (type is not INamedTypeSymbol named) return false;
        if (named.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IAsyncEnumerable<T>") return true;
        if (named.TypeArguments.Any(argument => ContainsAsync(argument, visited))) return true;
        if (named.ContainingAssembly.Identity.Name.StartsWith("System", StringComparison.Ordinal) ||
            named.SpecialType != SpecialType.None) return false;
        return named.GetMembers().OfType<IPropertySymbol>().Where(property => !property.IsStatic &&
            property.DeclaredAccessibility == Accessibility.Public).Any(property => ContainsAsync(property.Type, visited));
    }

    internal string? Generate()
    {
        if (types.Count == 0) return null;
        var document = Cs.Document(CsLanguageVersion.CSharp14).Nullable(NullableMode.Enable);
        var context = document.Class(contextName).Internal().Partial()
            .Base(Cs.Type("System.Text.Json.Serialization.JsonSerializerContext"));
        if (hasOptionsClassifier)
        {
            var classifier = new CsExpression(ExpressionKind.TypeOf, "", [])
            { Type = Cs.Type("System.Text.Json.Serialization.JsonUnionTypeStructuralClassifier") };
            var classifiers = new CsExpression(ExpressionKind.Array, "", [])
            {
                Type = Cs.Type("System.Type"),
                Initializer = new CsExpression(ExpressionKind.Initializer, "", new[] { classifier })
            };
            context.Attribute(Cs.Attribute(Cs.Type("System.Text.Json.Serialization.JsonSourceGenerationOptions"),
                new AttributeArgument(classifiers, "TypeClassifiers", false)));
        }
        var unionCases = types.OfType<INamedTypeSymbol>().Where(type =>
                type.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax().GetType().Name == "UnionDeclarationSyntax") ||
                type.GetAttributes().Any(attribute =>
                    attribute.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.UnionAttribute"))
            .SelectMany(type => type.InstanceConstructors.Where(constructor => constructor.Parameters.Length == 1)
                .Select(constructor => constructor.Parameters[0].Type.WithNullableAnnotation(NullableAnnotation.None)))
            .Distinct<ITypeSymbol>(SymbolEqualityComparer.Default).ToArray();
        var unionNames = unionCases.Select((type, index) => FrameworkCaseName(type) ?? "NeemleUnionCase" + index).ToArray();
        var options = Cs.Type("System.Text.Json.JsonSerializerOptions");
        context.Field(Cs.Generic("System.Runtime.CompilerServices.ConditionalWeakTable", options,
            Cs.Type(contextName)), "Contexts").Private().Static().ReadOnly()
            .Initialize(new CsExpression(ExpressionKind.New, "", [])).End();
        var create = Cs.Lambda(new CsBody
        {
            Tree = Cs.Tree(Return(NodeNew(contextName,
            NodeNew("System.Text.Json.JsonSerializerOptions", Cs.Symbol("value")))))
        }, Cs.LambdaParameter("value"))
            with
        { IsStatic = true };
        if (unionCases.Length != 0) EmitUnionRoots(context, unionCases, unionNames);
        CsNode Prepared(CsNode value) => unionCases.Length == 0 ? value : NodeCall("PrepareUnionRoots", value);
        context.Method(Cs.Type(contextName), "ForOptions").Internal().Static()
            .Parameter(options.Nullable(), "options").Body(Cs.Tree(NodeIf(NullTest(Cs.Symbol("options")),
                Return(Prepared(hasOptionsClassifier ? NodeNew(contextName,
                    NodeNew("System.Text.Json.JsonSerializerOptions")) : Cs.Symbol("Default")))),
                Return(Prepared(NodeCall("Contexts.GetValue", Cs.Symbol("options"), create))))).End();
        for (int i = 0; i < unionCases.Length; i++)
        {
            var typeOf = new CsExpression(ExpressionKind.TypeOf, "", [])
            { Type = CodecApiEmitter.NodeType(unionCases[i].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)) };
            var attribute = FrameworkCaseName(unionCases[i]) is null ?
                Cs.Attribute(Cs.Type("System.Text.Json.Serialization.JsonSerializable"), Cs.Arg(typeOf),
                    new AttributeArgument(Cs.Literal(unionNames[i]), "TypeInfoPropertyName", false)) :
                Cs.Attribute(Cs.Type("System.Text.Json.Serialization.JsonSerializable"), Cs.Arg(typeOf));
            context.Attribute(attribute);
        }
        int typeIndex = 0;
        foreach (var type in ExpandPolymorphicTypes().Where(type =>
                     !unionCases.Any(item => SymbolEqualityComparer.Default.Equals(item, type))))
        {
            var typeOf = new CsExpression(ExpressionKind.TypeOf, "", [])
            { Type = CodecApiEmitter.NodeType(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)) };
            context.Attribute(Cs.Attribute(Cs.Type("System.Text.Json.Serialization.JsonSerializable"), Cs.Arg(typeOf),
                new AttributeArgument(Cs.Literal("NeemleType" + typeIndex++), "TypeInfoPropertyName", false)));
        }
        context.End();
        try { return document.Render(); }
        catch (Neemle.CSharpComposer.Validation.GenValidationException)
        {
            int index = 0;
            foreach (var type in types) Console.Error.WriteLine("Generated context type " + index++ + ": " + type);
            throw;
        }
    }

    private IEnumerable<ITypeSymbol> ExpandPolymorphicTypes()
    {
        var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var pending = new Queue<ITypeSymbol>(types);
        while (pending.TryDequeue(out var type))
        {
            type = type.WithNullableAnnotation(NullableAnnotation.None);
            if (!seen.Add(type)) continue;
            yield return type;
            if (type is not INamedTypeSymbol named) continue;
            foreach (var attribute in named.GetAttributes().Where(attribute =>
                         CodecEmitter.IsJsonAttribute(attribute, "JsonDerivedTypeAttribute")))
            {
                if (attribute.ConstructorArguments.Length == 0 ||
                    attribute.ConstructorArguments[0].Kind == TypedConstantKind.Array ||
                    attribute.ConstructorArguments[0].Value is not ITypeSymbol derived) continue;
                if (derived is INamedTypeSymbol { IsUnboundGenericType: true } generic &&
                    generic.Arity == named.TypeArguments.Length)
                    derived = generic.ConstructedFrom.Construct(named.TypeArguments.ToArray());
                pending.Enqueue(derived);
            }
        }
    }

    private void EmitUnionRoots(Neemle.CSharpComposer.Builders.TypeBuilder<Neemle.CSharpComposer.Builders.FileBuilder> context,
        IReadOnlyList<ITypeSymbol> cases, IReadOnlyList<string> names)
    {
        var contextType = Cs.Type(contextName);
        context.Field(Cs.Generic("System.Runtime.CompilerServices.ConditionalWeakTable", contextType, Cs.Type("object")),
            "UnionRoots").Private().Static().ReadOnly().Initialize(new CsExpression(ExpressionKind.New, "", [])).End();
        var calls = cases.Select((type, index) => (CsNode)NodeCall("GC.KeepAlive", NodeNew(
            "System.Func<System.Text.Json.JsonSerializerOptions, System.Text.Json.Serialization.Metadata.JsonTypeInfo<" +
            TypeName(type) + ">>", NodeMember(Cs.Symbol("context"), "Create_" + names[index])))).ToList();
        var methods = new List<string>();
        foreach (CsNode[] group in calls.Chunk(3))
        {
            string name = "PreserveUnionCases_0_" + methods.Count;
            methods.Add(name);
            context.Method(Cs.Void, name).Private().Static().Parameter(contextType, "context")
                .Body(Cs.Tree(group)).End();
        }
        int level = 1;
        while (methods.Count > 3)
        {
            var next = new List<string>();
            foreach (string[] group in methods.Chunk(3))
            {
                string name = $"PreserveUnionCases_{level}_{next.Count}";
                next.Add(name);
                context.Method(Cs.Void, name).Private().Static().Parameter(contextType, "context")
                    .Body(Cs.Tree(group.Select(method => (CsNode)NodeCall(method, Cs.Symbol("context"))).ToArray())).End();
            }
            methods = next;
            level++;
        }
        calls = methods.Select(method => (CsNode)NodeCall(method, Cs.Symbol("context"))).ToList();
        calls.Add(Return(NodeNew("object")));
        context.Method(Cs.Type("object"), "CreateUnionRoots").Private().Static().Parameter(contextType, "context")
            .Body(Cs.Tree(calls.ToArray())).End();
        context.Method(contextType, "PrepareUnionRoots").Private().Static().Parameter(contextType, "context")
            .Body(Cs.Tree(NodeCall("UnionRoots.GetValue", Cs.Symbol("context"), Cs.Symbol("CreateUnionRoots")),
                Return(Cs.Symbol("context")))).End();
    }

    private static string? FrameworkCaseName(ITypeSymbol type)
    {
        if (type.SpecialType != SpecialType.None) return type.Name;
        if (type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte }) return "ByteArray";
        if (type is INamedTypeSymbol { TypeArguments.Length: 1 } memory &&
            memory.TypeArguments[0].SpecialType == SpecialType.System_Byte)
        {
            return memory.OriginalDefinition.ToDisplayString() switch
            {
                "System.Memory<T>" => "MemoryByte",
                "System.ReadOnlyMemory<T>" => "ReadOnlyMemoryByte",
                _ => null
            };
        }
        return type.ToDisplayString() is "System.DateTimeOffset" or "System.DateOnly" or "System.TimeOnly" or
            "System.TimeSpan" or "System.Guid" or "System.Half" or "System.Int128" or "System.UInt128" or
            "System.Uri" or "System.Version" or "System.Text.Json.JsonElement" or "System.Text.Json.JsonDocument" or
            "System.Numerics.BFloat16" or "System.Numerics.Decimal32" or "System.Numerics.Decimal64" or
            "System.Numerics.Decimal128" ? type.Name : null;
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Model;
using Neemle.CSharpComposer.Rendering;
using static CodecApiEmitter;
using TypeKind = Microsoft.CodeAnalysis.TypeKind;

internal static class NativeTypeMetadataEmitter
{
    private const string RegistryKey = "Neemle.JSON.NativeTypeMetadata";
    private const string RegistryType = "System.Collections.Generic.Dictionary<System.Type, System.ValueTuple<int, string>>";
    private const string PolymorphismKey = "Neemle.JSON.NativePolymorphism";
    private const string PolymorphismType = "System.Collections.Generic.Dictionary<System.Type, System.Func<System.Text.Json.Serialization.Metadata.JsonPolymorphismOptions>?>";
    private const int ValueType = 1;
    private const int Class = 2;
    private const int Interface = 4;
    private const int Abstract = 8;
    private const int Sealed = 16;
    private const int OverridesToString = 32;

    internal static void Write(string directory, IEnumerable<ITypeSymbol> candidates)
    {
        var types = candidates.Select(type => type.WithNullableAnnotation(NullableAnnotation.None))
            .Where(type => type is INamedTypeSymbol or IArrayTypeSymbol)
            .Distinct(SymbolEqualityComparer.Default).OfType<ITypeSymbol>().ToArray();
        var document = Cs.Document(CsLanguageVersion.CSharp14).Nullable(NullableMode.Enable);
        var root = document.Class("GeneratedNativeTypeMetadata").Internal().Static().Partial();
        var groups = new List<string>();
        var factories = new Dictionary<ITypeSymbol, string>(SymbolEqualityComparer.Default);
        foreach (var type in types.OfType<INamedTypeSymbol>())
        {
            var attributes = type.GetAttributes().Where(attribute => CodecEmitter.IsJsonAttribute(attribute,
                "JsonPolymorphicAttribute") || CodecEmitter.IsJsonAttribute(attribute, "JsonDerivedTypeAttribute")).ToArray();
            if (attributes.Length == 0) continue;
            string factory = "CreatePolymorphism_" + factories.Count;
            factories.Add(type, factory);
            EmitPolymorphism(root, factory, attributes);
        }
        foreach (var chunk in types.Chunk(12))
        {
            string name = "Register_" + groups.Count;
            groups.Add(name);
            var calls = new List<CsNode>();
            foreach (var type in chunk)
            {
                calls.Add(NodeCall("registry.TryAdd", TypeOf(RuntimeSyntaxType(type)),
                    NodeNew("System.ValueTuple<int, string>", Cs.Value(Flags(type)), Cs.Value(RuntimeName(type)))));
                CsNode factory = factories.TryGetValue(type, out string? method) ?
                    NodeNew("System.Func<System.Text.Json.Serialization.Metadata.JsonPolymorphismOptions>", Cs.Symbol(method)) :
                    Cs.Value(null);
                calls.Add(NodeCall("polymorphism.TryAdd", TypeOf(RuntimeSyntaxType(type)), factory));
            }
            root.Method(Cs.Void, name).Private().Static().Parameter(NodeType(RegistryType), "registry")
                .Parameter(NodeType(PolymorphismType), "polymorphism").Body(Cs.Tree(calls.ToArray())).End();
        }
        while (groups.Count > 12)
        {
            var parents = new List<string>();
            foreach (var chunk in groups.Chunk(12))
            {
                string name = "RegisterGroup_" + parents.Count + "_" + groups.Count;
                parents.Add(name);
                root.Method(Cs.Void, name).Private().Static().Parameter(NodeType(RegistryType), "registry")
                    .Parameter(NodeType(PolymorphismType), "polymorphism")
                    .Body(Cs.Tree(chunk.Select(method => (CsNode)NodeCall(method, Cs.Symbol("registry"),
                        Cs.Symbol("polymorphism"))).ToArray())).End();
            }
            groups = parents;
        }
        var body = new List<CsNode>
        {
            NodeVar("registry", NodeBinary(CsOperator.Coalesce,
                Cast(RegistryType + "?", NodeCall("System.AppContext.GetData", Cs.Value(RegistryKey))),
                NodeNew(RegistryType))),
            NodeVar("polymorphism", NodeBinary(CsOperator.Coalesce,
                Cast(PolymorphismType + "?", NodeCall("System.AppContext.GetData", Cs.Value(PolymorphismKey))),
                NodeNew(PolymorphismType)))
        };
        body.AddRange(groups.Select(method => (CsNode)NodeCall(method, Cs.Symbol("registry"), Cs.Symbol("polymorphism"))));
        body.Add(NodeCall("System.AppContext.SetData", Cs.Value(RegistryKey), Cs.Symbol("registry")));
        body.Add(NodeCall("System.AppContext.SetData", Cs.Value(PolymorphismKey), Cs.Symbol("polymorphism")));
        var locked = Cs.Instruction(InstructionKind.Lock, new()
        {
            Value = TypeOf("System.Text.Json.JsonSerializer"),
            Body = Cs.Tree(body.ToArray())
        });
        root.Method(Cs.Void, "Initialize").Internal().Static()
            .Attribute(Cs.Attribute(Cs.Type("System.Runtime.CompilerServices.ModuleInitializer")))
            .Body(Cs.Tree(locked)).End();
        root.End();
        WriteParts(directory, document.Render());
    }

    private static void WriteParts(string directory, string source)
    {
        var unit = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var declaration = unit.Members.OfType<ClassDeclarationSyntax>().Single();
        var members = new List<MemberDeclarationSyntax>();
        int lines = 4;
        int index = 0;
        foreach (var member in declaration.Members)
        {
            int count = member.NormalizeWhitespace().ToFullString().Count(character => character == '\n') + 1;
            if (count > 450) throw new InvalidOperationException("Static metadata method is too large.");
            if (lines + count > 450)
            {
                WritePart(directory, unit, declaration, members, index++);
                members.Clear();
                lines = 4;
            }
            members.Add(member);
            lines += count;
        }
        if (members.Count != 0) WritePart(directory, unit, declaration, members, index);
    }

    private static void WritePart(string directory, CompilationUnitSyntax unit, ClassDeclarationSyntax declaration,
        IEnumerable<MemberDeclarationSyntax> members, int index)
    {
        var part = unit.ReplaceNode(declaration, declaration.WithMembers(SyntaxFactory.List(members)));
        File.WriteAllText(Path.Combine(directory, "JsonCodecsNativeMetadata_" + index + ".g.cs"),
            part.NormalizeWhitespace().ToFullString());
    }

    private static void EmitPolymorphism(Neemle.CSharpComposer.Builders.TypeBuilder<Neemle.CSharpComposer.Builders.FileBuilder> root,
        string name, IReadOnlyList<AttributeData> attributes)
    {
        var body = new List<CsNode> { NodeVar("options", NodeNew("System.Text.Json.Serialization.Metadata.JsonPolymorphismOptions")) };
        foreach (var attribute in attributes)
        {
            foreach (var argument in attribute.NamedArguments)
            {
                CsNode value = Cs.Value(argument.Value.Value);
                if (argument.Key == "UnknownDerivedTypeHandling")
                    value = Cast("System.Text.Json.Serialization.JsonUnknownDerivedTypeHandling", value);
                body.Add(NodeAssign(NodeMember(Cs.Symbol("options"), argument.Key), value));
            }
            if (!CodecEmitter.IsJsonAttribute(attribute, "JsonDerivedTypeAttribute")) continue;
            if (attribute.ConstructorArguments[0].Value is not ITypeSymbol derived)
                throw new InvalidOperationException("A polymorphic type must be known at build time.");
            var args = new List<CsNode> { TypeOf(RuntimeSyntaxType(derived)) };
            if (attribute.ConstructorArguments.Length > 1 && attribute.ConstructorArguments[1].Value is { } discriminator)
                args.Add(Cs.Value(discriminator));
            body.Add(NodeCall("options.DerivedTypes.Add", NodeNew("System.Text.Json.Serialization.Metadata.JsonDerivedType",
                args.ToArray())));
        }
        body.Add(Return(Cs.Symbol("options")));
        root.Method(NodeType("System.Text.Json.Serialization.Metadata.JsonPolymorphismOptions"), name).Private().Static()
            .Body(Cs.Tree(body.ToArray())).End();
    }

    private static string RuntimeSyntaxType(ITypeSymbol type) => type.ToDisplayString(
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions &
            ~SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));

    private static int Flags(ITypeSymbol type)
    {
        int flags = type.IsValueType ? ValueType : 0;
        if (type.TypeKind is TypeKind.Class or TypeKind.Delegate || type is IArrayTypeSymbol) flags |= Class;
        if (type.TypeKind == TypeKind.Interface) flags |= Interface;
        if (type is INamedTypeSymbol { IsAbstract: true }) flags |= Abstract;
        if (type.IsValueType || type is IArrayTypeSymbol || type is INamedTypeSymbol { IsSealed: true }) flags |= Sealed;
        for (var current = type as INamedTypeSymbol; current is not null &&
            current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            if (current.GetMembers("ToString").OfType<IMethodSymbol>().Any(method => method.IsOverride &&
                method.Parameters.Length == 0)) { flags |= OverridesToString; break; }
        return flags;
    }

    private static string RuntimeName(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
            return RuntimeName(array.ElementType) + "[" + new string(',', array.Rank - 1) + "]";
        if (type is not INamedTypeSymbol named) throw new InvalidOperationException("Unsupported static type name.");
        string prefix = named.ContainingType is null ? named.ContainingNamespace.IsGlobalNamespace ? "" :
            named.ContainingNamespace.ToDisplayString() + "." : DefinitionName(named.ContainingType) + "+";
        string name = prefix + named.MetadataName;
        var arguments = new List<ITypeSymbol>();
        for (var current = named; current is not null; current = current.ContainingType)
            arguments.InsertRange(0, current.TypeArguments);
        return arguments.Count == 0 ? name : name + "[" + string.Join(",", arguments.Select(RuntimeName)) + "]";
    }

    private static string DefinitionName(INamedTypeSymbol type) => type.ContainingType is null ?
        (type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString() + ".") +
            type.MetadataName : DefinitionName(type.ContainingType) + "+" + type.MetadataName;
}

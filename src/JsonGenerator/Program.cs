using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

if (args.Length != 7 || args[0] != "--manifest")
    throw new ArgumentException(("Usage: JsonGenerator --manifest <sources> <references> " +
            "<output> <defines> <language> <nullable>"));
var sources = File.ReadAllLines(args[1]).Where(File.Exists).ToArray();
var output = args[3];
var references = File.ReadAllLines(args[2]).Where(File.Exists);
var language = LanguageVersion.Default;
var languageValue = File.ReadAllText(args[5]).Trim();
if (languageValue.Length != 0 && !LanguageVersionFacts.TryParse(languageValue, out language))
    throw new InvalidOperationException("Unsupported C# language version: " + languageValue);
var symbols = File.ReadAllLines(args[4]).SelectMany(line =>
    line.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray();
var nullable = File.ReadAllText(args[6]).Trim().ToLowerInvariant() switch
{
    "enable" => NullableContextOptions.Enable,
    "annotations" => NullableContextOptions.Annotations,
    "warnings" => NullableContextOptions.Warnings,
    _ => NullableContextOptions.Disable
};
string nullableDirectives = nullable switch
{
    NullableContextOptions.Enable => "#nullable enable\n",
    NullableContextOptions.Annotations => "#nullable enable annotations\n#nullable disable warnings\n",
    NullableContextOptions.Warnings => "#nullable disable annotations\n#nullable enable warnings\n",
    _ => "#nullable disable\n"
};
var trees = sources.Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path),
    new CSharpParseOptions(language, preprocessorSymbols: symbols), path)).ToArray();
var compilation = CSharpCompilation.Create("Input", trees,
    references.Distinct().Select(path => MetadataReference.CreateFromFile(path)),
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: nullable));
var emitter = new CodecEmitter(compilation.GetSpecialType(SpecialType.System_Object));
var asyncContexts = new AsyncContextEmitter();
var candidates = new List<ITypeSymbol>();
foreach (var tree in trees)
{
    var model = compilation.GetSemanticModel(tree);
    foreach (var node in tree.GetRoot().DescendantNodes())
    {
        ITypeSymbol? candidate = node switch
        {
            BaseObjectCreationExpressionSyntax creation => model.GetTypeInfo(creation).Type,
            BaseTypeDeclarationSyntax declaration => model.GetDeclaredSymbol(declaration) as ITypeSymbol,
            TypeOfExpressionSyntax typeOf => model.GetTypeInfo(typeOf.Type).Type,
            ExpressionSyntax expression when expression.Parent is ArgumentSyntax or EqualsValueClauseSyntax or
                AssignmentExpressionSyntax or ReturnStatementSyntax or CastExpressionSyntax or InitializerExpressionSyntax or
                ConditionalExpressionSyntax => ObjectConversion(model, expression),
            _ => null
        };
        if (candidate is INamedTypeSymbol { IsGenericType: true } named &&
            named.TypeArguments.Any(argument => argument.TypeKind == TypeKind.TypeParameter)) continue;
        if (candidate is not null && CandidateVisible(candidate, compilation.Assembly)) candidates.Add(candidate);
    }
}
foreach (var special in new[] { SpecialType.System_String, SpecialType.System_Boolean, SpecialType.System_Byte,
    SpecialType.System_SByte, SpecialType.System_Int16, SpecialType.System_UInt16, SpecialType.System_Int32,
    SpecialType.System_UInt32, SpecialType.System_Int64, SpecialType.System_UInt64, SpecialType.System_Single,
    SpecialType.System_Double, SpecialType.System_Decimal, SpecialType.System_Char })
    candidates.Add(compilation.GetSpecialType(special));
foreach (string name in new[] { "System.Guid", "System.DateTime", "System.DateTimeOffset", "System.DateOnly",
    "System.TimeOnly", "System.TimeSpan", "System.Uri", "System.Version", "System.Text.Json.JsonElement",
    "System.Text.Json.JsonDocument", "System.Text.Json.Nodes.JsonNode" })
    if (compilation.GetTypeByMetadataName(name) is { } candidate) candidates.Add(candidate);
emitter.SetDynamicCandidates(candidates.Distinct(SymbolEqualityComparer.Default).OfType<ITypeSymbol>());
Directory.CreateDirectory(output);
var safeContexts = new Dictionary<INamedTypeSymbol, bool>(SymbolEqualityComparer.Default);
for (int i = 0; i < trees.Length; i++)
{
    var rewriter = new Rewriter(compilation.GetSemanticModel(trees[i]), emitter, asyncContexts,
        symbols.Contains("NEEMLE_STATIC_NATIVE_METADATA", StringComparer.Ordinal), safeContexts);
    var path = Path.Combine(output, $"Source{i}.g.cs");
    File.WriteAllText(path, nullableDirectives + "#line 1 \"" + Path.GetFullPath(sources[i]).Replace("\\",
        "\\\\").Replace("\"", "\\\"") +
        "\"\n" + rewriter.Visit(trees[i].GetRoot())!.ToFullString());
}
GeneratedCodeLayout.Write(output, emitter.Generate());
emitter.WriteAnonymousTypes(output);
asyncContexts.Write(output);
if (symbols.Contains("NEEMLE_STATIC_NATIVE_METADATA", StringComparer.Ordinal))
    NativeTypeMetadataEmitter.Write(output, CollectMetadataTypes(
        candidates.Concat(emitter.RegisteredTypes).Concat(asyncContexts.KnownTypes), compilation.Assembly));

static IEnumerable<ITypeSymbol> CollectMetadataTypes(IEnumerable<ITypeSymbol> candidates, IAssemblySymbol assembly)
{
    var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
    var pending = new Queue<ITypeSymbol>(candidates);
    while (pending.TryDequeue(out var type))
    {
        type = type.WithNullableAnnotation(NullableAnnotation.None);
        if (!seen.Add(type) || !CandidateVisible(type, assembly)) continue;
        yield return type;
        if (type is IArrayTypeSymbol array) { pending.Enqueue(array.ElementType); continue; }
        if (type is not INamedTypeSymbol named) continue;
        foreach (var argument in named.TypeArguments) pending.Enqueue(argument);
        if (named.BaseType is not null) pending.Enqueue(named.BaseType);
        foreach (var contract in named.Interfaces) pending.Enqueue(contract);
        if (!SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, assembly)) continue;
        foreach (var member in named.GetMembers())
        {
            if (member is IPropertySymbol { IsStatic: false } property) pending.Enqueue(property.Type);
            if (member is IFieldSymbol { IsStatic: false, IsImplicitlyDeclared: false } field) pending.Enqueue(field.Type);
        }
        foreach (var attribute in named.GetAttributes())
            foreach (var argument in attribute.ConstructorArguments)
                if (argument.Value is ITypeSymbol attributeType) pending.Enqueue(attributeType);
    }
}

static bool CandidateVisible(ITypeSymbol type, IAssemblySymbol assembly)
{
    if (type is IArrayTypeSymbol array) return CandidateVisible(array.ElementType, assembly);
    if (type is not INamedTypeSymbol named || named.IsAnonymousType) return false;
    if (named.TypeArguments.Any(argument => !CandidateVisible(argument, assembly))) return false;
    for (INamedTypeSymbol? current = named; current is not null; current = current.ContainingType)
    {
        if (current.DeclaredAccessibility == Accessibility.Public) continue;
        if (current.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal &&
            SymbolEqualityComparer.Default.Equals(current.ContainingAssembly, assembly)) continue;
        return false;
    }
    return true;
}

static ITypeSymbol? ObjectConversion(SemanticModel model, ExpressionSyntax expression)
{
    var info = model.GetTypeInfo(expression);
    return info.ConvertedType?.SpecialType == SpecialType.System_Object &&
        info.Type?.SpecialType != SpecialType.System_Object ? info.Type : null;
}

sealed class Rewriter(SemanticModel semantic, CodecEmitter emitter, AsyncContextEmitter asyncContexts,
    bool staticNativeMetadata, Dictionary<INamedTypeSymbol, bool> safeContexts) : CSharpSyntaxRewriter
{
    private const string OptionsTypeName = "System.Text.Json.JsonSerializerOptions";

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        var visited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
        if (!staticNativeMetadata || !node.IsKind(SyntaxKind.AddExpression) ||
            semantic.GetTypeInfo(node).Type?.SpecialType != SpecialType.System_String) return visited;
        return visited.WithLeft(RewriteEnumDisplay(node.Left, visited.Left))
            .WithRight(RewriteEnumDisplay(node.Right, visited.Right));
    }

    public override SyntaxNode? VisitInterpolation(InterpolationSyntax node)
    {
        var visited = (InterpolationSyntax)base.VisitInterpolation(node)!;
        if (!staticNativeMetadata || semantic.GetTypeInfo(node.Expression).Type?.TypeKind != TypeKind.Enum) return visited;
        if (node.FormatClause is not null)
            throw new InvalidOperationException("Formatted enum interpolation requires generated formatting support.");
        return visited.WithExpression(RewriteEnumDisplay(node.Expression, visited.Expression));
    }

    private ExpressionSyntax RewriteEnumDisplay(ExpressionSyntax original, ExpressionSyntax visited)
    {
        var type = semantic.GetTypeInfo(original).Type;
        if (type?.TypeKind != TypeKind.Enum) return visited;
        if (!emitter.TryRegisterEnumDisplay(type, out int index))
            throw new InvalidOperationException("Enum display is not supported for " + type);
        return SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression(CodecName(index) + "EnumDisplay_" + index),
            SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(visited.WithoutTrivia()))))
            .WithTriviaFrom(visited);
    }

    public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
    {
        if (node.Declaration.Variables.Count == 1 && node.Declaration.Variables[0].Initializer?.Value is
            ObjectCreationExpressionSyntax creation && semantic.GetTypeInfo(creation).Type is INamedTypeSymbol converter &&
            (converter.ToDisplayString() == "System.Text.Json.Serialization.JsonStringEnumConverter" ||
             converter.OriginalDefinition.ToDisplayString() == "System.Text.Json.Serialization.JsonStringEnumConverter<TEnum>") &&
            SupportedConverterLocal(creation, semantic))
        {
            var rewritten = (LocalDeclarationStatementSyntax)base.VisitLocalDeclarationStatement(node)!;
            return rewritten.WithDeclaration(rewritten.Declaration.WithType(
                SyntaxFactory.IdentifierName("var").WithTriviaFrom(node.Declaration.Type)));
        }
        if (node.Declaration.Variables.Count != 1 || semantic.GetDeclaredSymbol(node.Declaration.Variables[0]) is not
            ILocalSymbol local || ContextAlias.Find(semantic, local) is not { } alias ||
            ContextTarget(alias.Declaration.Initializer!.Value, alias.Invocation) is not { } info)
            return base.VisitLocalDeclarationStatement(node);
        var variable = node.Declaration.Variables[0].WithInitializer(SyntaxFactory.EqualsValueClause(info.Options ??
            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)));
        return node.WithDeclaration(node.Declaration.WithType(SyntaxFactory.ParseTypeName(
            "global::System.Text.Json.JsonSerializerOptions").WithTriviaFrom(node.Declaration.Type)
                .WithTrailingTrivia(SyntaxFactory.Space)).WithVariables(
                SyntaxFactory.SingletonSeparatedList(variable)));
    }

    public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        if (node.Declaration.Variables.Count == 1 && node.Declaration.Variables[0].Initializer?.Value is
            ObjectCreationExpressionSyntax creation && IsEnumConverter(creation, semantic) &&
            SupportedConverterLocal(creation, semantic))
        {
            var rewritten = (FieldDeclarationSyntax)base.VisitFieldDeclaration(node)!;
            var tuple = SyntaxFactory.ParseTypeName(
                "(global::System.Text.Json.JsonNamingPolicy? NamingPolicy, bool AllowIntegerValues)");
            return rewritten.WithDeclaration(rewritten.Declaration.WithType(tuple.WithTriviaFrom(node.Declaration.Type)));
        }
        return base.VisitFieldDeclaration(node);
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        if (node.Name.Identifier.ValueText == "IsReadOnly" &&
            semantic.GetTypeInfo(node.Expression).Type?.ToDisplayString() == OptionsTypeName)
            return ContextCall("global::GeneratedJson.OptionsAreReadOnly", (ExpressionSyntax)Visit(node.Expression)!)
                .WithTriviaFrom(node);
        if (node.Name.Identifier.ValueText == "Converters" &&
            semantic.GetTypeInfo(node.Expression).Type?.ToDisplayString() == OptionsTypeName)
            return SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression("global::GeneratedJson.Converters"),
                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument((ExpressionSyntax)Visit(node.Expression)!)))).WithTriviaFrom(node);
        if (node.Parent is ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax invocation } &&
            semantic.GetOperation(invocation) is IInvocationOperation operation &&
            operation.TargetMethod.ContainingType.ToDisplayString() == "System.Text.Json.JsonSerializer" &&
            semantic.GetSymbolInfo(node).Symbol is IPropertySymbol
            { Name: "Default" or "Web" or "Strict", IsStatic: true } property &&
            property.ContainingType.ToDisplayString() == OptionsTypeName)
            return SyntaxFactory.ParseExpression(property.Name != "Default" ?
                    "new global::System.Text.Json.JsonSerializerOptions(" +
                        "global::System.Text.Json.JsonSerializerDefaults." + property.Name + ")" :
                    "new global::System.Text.Json.JsonSerializerOptions()")
                .WithTriviaFrom(node);
        return base.VisitMemberAccessExpression(node);
    }

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        if (semantic.GetSymbolInfo(node.Left).Symbol is IPropertySymbol { Name: "CustomConverter" } property &&
            property.ContainingType.ToDisplayString() == "System.Text.Json.Serialization.Metadata.JsonPropertyInfo" &&
            semantic.GetTypeInfo(node.Right).Type is INamedTypeSymbol factory && IsConverterFactory(factory))
        {
            emitter.RegisterFrameworkFactoryAdapter();
            var visited = (AssignmentExpressionSyntax)base.VisitAssignmentExpression(node)!;
            var adapter = SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression("global::GeneratedJson.WrapFactory"),
                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(visited.Right))));
            return visited.WithRight(adapter.WithTriviaFrom(visited.Right));
        }
        var target = semantic.GetTypeInfo(node.Left).Type?.WithNullableAnnotation(NullableAnnotation.None);
        var value = semantic.GetTypeInfo(node.Right).Type;
        if (node.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
            target?.ToDisplayString() == "System.Text.Json.Nodes.JsonNode" && value is not null &&
            value.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString() !=
                "System.Text.Json.Nodes.JsonNode" && !IsJsonNode(value))
        {
            if (!emitter.TryRegister(value, out int index))
                throw new InvalidOperationException("Unsupported JsonNode assignment: " + node);
            var visited = (AssignmentExpressionSyntax)base.VisitAssignmentExpression(node)!;
            return visited.WithRight(SyntaxFactory
                .ParseExpression("global::System.Text.Json.Nodes.JsonNode.Parse(" + CodecName(index) + "Encode_" +
                index + "(" + visited.Right + "))").WithTriviaFrom(visited.Right));
        }
        return base.VisitAssignmentExpression(node);
    }

    private static bool IsJsonNode(ITypeSymbol type)
    {
        for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
            if (current.ToDisplayString() == "System.Text.Json.Nodes.JsonNode") return true;
        return false;
    }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        if (semantic.GetDeclaredSymbol(node) is INamedTypeSymbol context && IsJsonContext(context) &&
            context.DeclaredAccessibility == Accessibility.Internal && ContextUsesAreDirect(context) && node.Members.Count == 0)
            return StripContextConverters(node);
        if (node.BaseList is null) return base.VisitClassDeclaration(node);
        for (int i = 0; i < node.BaseList.Types.Count; i++)
        {
            var entry = node.BaseList.Types[i];
            if (semantic.GetTypeInfo(entry.Type).Type?.ToDisplayString() ==
                "System.Text.Json.Serialization.JsonConverterAttribute")
            {
                var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
                return visited.ReplaceNode(visited.BaseList!.Types[i].Type,
                    SyntaxFactory.ParseTypeName("global::GeneratedJson.ConverterAttribute"));
            }
            if (semantic.GetTypeInfo(entry.Type).Type?.ToDisplayString() ==
                "System.Text.Json.Serialization.JsonConverterFactory")
            {
                emitter.RegisterConverterFactories();
                var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
                return visited.ReplaceNode(visited.BaseList!.Types[i].Type,
                    SyntaxFactory.ParseTypeName("global::GeneratedJson.ConverterFactory"));
            }
            if (semantic.GetTypeInfo(entry.Type).Type is INamedTypeSymbol parent &&
                parent.OriginalDefinition.ToDisplayString() == "System.Text.Json.Serialization.JsonConverter<T>")
            {
                ValidateConverterClass(node);
                var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
                return visited.ReplaceNode(visited.BaseList!.Types[i].Type, SyntaxFactory.ParseTypeName(
                    "global::" + GeneratedCodeLayout.GeneratedTypeName + ".Converter<" + parent.TypeArguments[0]
                        .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ">"));
            }
        }
        return base.VisitClassDeclaration(node);
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        if (semantic.GetDeclaredSymbol(node) is IMethodSymbol { Name: "CreateConverter", IsOverride: true } method &&
            method.ContainingType.BaseType?.ToDisplayString() is
                "System.Text.Json.Serialization.JsonConverterFactory" or
                "System.Text.Json.Serialization.JsonConverterAttribute")
            return visited.WithReturnType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword))
                .WithTriviaFrom(node.ReturnType));
        return visited;
    }

    private ClassDeclarationSyntax StripContextConverters(ClassDeclarationSyntax node)
    {
        var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
        for (int listIndex = 0; listIndex < node.AttributeLists.Count; listIndex++)
            for (int attributeIndex = 0; attributeIndex < node.AttributeLists[listIndex].Attributes.Count; attributeIndex++)
            {
                var original = node.AttributeLists[listIndex].Attributes[attributeIndex];
                if (semantic.GetSymbolInfo(original).Symbol is not IMethodSymbol constructor || constructor.ContainingType
                    .ToDisplayString() != "System.Text.Json.Serialization.JsonSourceGenerationOptionsAttribute") continue;
                var current = visited.AttributeLists[listIndex].Attributes[attributeIndex];
                if (current.ArgumentList is null) continue;
                var filtered = current.ArgumentList.Arguments.Where(argument => argument.NameEquals?.Name.Identifier.ValueText !=
                    "Converters");
                var replacement = current.WithArgumentList(current.ArgumentList.WithArguments(SyntaxFactory.SeparatedList(filtered)));
                visited = visited.ReplaceNode(current, replacement);
            }
        return visited;
    }

    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        if (semantic.GetTypeInfo(node).Type?.ToDisplayString() == "System.Text.Json.Nodes.JsonArray" &&
            node.ArgumentList is { } arrayArguments)
        {
            var args = arrayArguments.Arguments.Select(argument => argument.WithExpression(
                RewriteNodeImplicitValue(argument.Expression)));
            return node.WithArgumentList(arrayArguments.WithArguments(SyntaxFactory.SeparatedList(args)));
        }
        if (node.ArgumentList?.Arguments.Count is 1 or 2 && node.Ancestors().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault() is { } enclosing && semantic.GetDeclaredSymbol(enclosing) is INamedTypeSymbol converter &&
            (ConverterTarget(converter, false) is not null || IsConverterFactory(converter)) &&
            semantic.GetTypeInfo(node).Type?.ToDisplayString() is "System.Text.Json.JsonException" or
                "System.NotSupportedException" && semantic.GetTypeInfo(node.ArgumentList.Arguments[0].Expression)
                    .ConvertedType?.SpecialType == SpecialType.System_String)
        {
            var visited = (ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node)!;
            string name = semantic.GetTypeInfo(node).Type!.Name;
            return visited.WithType(SyntaxFactory.ParseTypeName("global::GeneratedJson.User" + name)
                .WithTriviaFrom(visited.Type));
        }
        if (semantic.GetTypeInfo(node).Type?.ToDisplayString() == OptionsTypeName && node.Initializer is { } initializer &&
            initializer.Expressions.OfType<AssignmentExpressionSyntax>().FirstOrDefault(assignment =>
                assignment.Left is IdentifierNameSyntax { Identifier.ValueText: "Converters" } &&
                assignment.Right is InitializerExpressionSyntax) is { } converters)
        {
            var visited = (ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node)!;
            var remaining = visited.Initializer!.Expressions.Where((_, index) =>
                initializer.Expressions[index] != converters).ToArray();
            visited = visited.WithInitializer(remaining.Length == 0 ? null :
                visited.Initializer.WithExpressions(SyntaxFactory.SeparatedList(remaining)));
            if (visited.ArgumentList is null && visited.Initializer is null)
                visited = visited.WithArgumentList(SyntaxFactory.ArgumentList());
            var arguments = new[] { SyntaxFactory.Argument(visited) }.Concat(
                ((InitializerExpressionSyntax)converters.Right).Expressions.Select(expression =>
                    SyntaxFactory.Argument((ExpressionSyntax)Visit(expression)!)));
            return SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression("global::GeneratedJson.WithConverters"),
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments))).WithTriviaFrom(node);
        }
        if (semantic.GetTypeInfo(node).Type?.ToDisplayString() == OptionsTypeName && node.ArgumentList?.Arguments is
            { Count: 1 } copyArguments && copyArguments[0].Expression is IdentifierNameSyntax source &&
            semantic.GetTypeInfo(source).Type?.ToDisplayString() == OptionsTypeName)
        {
            var visited = (ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node)!;
            return ContextCall("global::GeneratedJson.CopyOptions", source, visited).NormalizeWhitespace()
                .WithTriviaFrom(node);
        }
        var enumConverterType = semantic.GetTypeInfo(node).Type as INamedTypeSymbol;
        bool allEnums = enumConverterType?.ToDisplayString() == "System.Text.Json.Serialization.JsonStringEnumConverter";
        bool genericEnum = enumConverterType?.OriginalDefinition.ToDisplayString() ==
            "System.Text.Json.Serialization.JsonStringEnumConverter<TEnum>";
        if (!(allEnums || genericEnum) ||
            !SupportedConverterLocal(node, semantic) || node.Parent is not EqualsValueClauseSyntax
            { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Variables.Count: 1 } } })
            return base.VisitObjectCreationExpression(node);
        if (semantic.GetOperation(node) is not IObjectCreationOperation operation)
            return base.VisitObjectCreationExpression(node);
        var explicitArguments = operation.Arguments.Where(argument => !argument.IsImplicit).ToArray();
        var fields = new List<ArgumentSyntax>();
        foreach (var argument in explicitArguments)
        {
            int ordinal = argument.Parameter!.Ordinal;
            var value = (ExpressionSyntax)Visit(((ArgumentSyntax)argument.Syntax).Expression)!;
            if (ordinal == 0) value = SyntaxFactory.CastExpression(
                SyntaxFactory.ParseTypeName("global::System.Text.Json.JsonNamingPolicy?"),
                SyntaxFactory.ParenthesizedExpression(value));
            fields.Add(SyntaxFactory.Argument(value).WithNameColon(SyntaxFactory.NameColon(
                ordinal == 0 ? "NamingPolicy" : "AllowIntegerValues")));
        }
        if (!explicitArguments.Any(argument => argument.Parameter!.Ordinal == 0))
            fields.Add(SyntaxFactory.Argument(SyntaxFactory.ParseExpression("(global::System.Text.Json.JsonNamingPolicy?)null"))
                .WithNameColon(SyntaxFactory.NameColon("NamingPolicy")));
        if (!explicitArguments.Any(argument => argument.Parameter!.Ordinal == 1))
            fields.Add(SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression))
                .WithNameColon(SyntaxFactory.NameColon("AllowIntegerValues")));
        if (allEnums) emitter.RegisterAllEnums();
        return SyntaxFactory.TupleExpression(SyntaxFactory.SeparatedList(fields)).NormalizeWhitespace().WithTriviaFrom(node);
    }

    private void ValidateConverterClass(ClassDeclarationSyntax node)
    {
        if (semantic.GetDeclaredSymbol(node) is not INamedTypeSymbol || HasUnsupportedConverterNodes(node))
            throw new InvalidOperationException(("Only local JSON converters used directly in " +
            "registration are supported: ") + node.Identifier);
    }

    private bool HasUnsupportedConverterNodes(ClassDeclarationSyntax node) =>
        node.DescendantNodes().Any(n => n is ThisExpressionSyntax && n.Parent is not MemberAccessExpressionSyntax ||
            n is BaseExpressionSyntax && n.Parent is not MemberAccessExpressionSyntax
            { Name.Identifier.ValueText: "CanConvert" or "HandleNull" or "ReadAsPropertyName" or "WriteAsPropertyName" });

    private static bool SupportedConverterOptionRead(IdentifierNameSyntax identifier) =>
        identifier.Parent is MemberAccessExpressionSyntax member && member.Expression == identifier &&
        SupportedProperty(member.Name.Identifier.ValueText) &&
        !(member.Parent is AssignmentExpressionSyntax assignment && assignment.Left == member);

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (staticNativeMetadata && node.ArgumentList.Arguments.Count == 0 &&
            node.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ToString" } enumCall &&
            semantic.GetTypeInfo(enumCall.Expression).Type?.TypeKind == TypeKind.Enum)
            return RewriteEnumDisplay(enumCall.Expression, (ExpressionSyntax)Visit(enumCall.Expression)!);
        if (RewriteMetadataApi(node) is { } metadata) return metadata;
        if (semantic.GetOperation(node) is IInvocationOperation { TargetMethod.Name: "Add" } add &&
            add.TargetMethod.ContainingType.ToDisplayString() == "System.Text.Json.Nodes.JsonArray" &&
            node.ArgumentList.Arguments.Count == 1)
        {
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
            var argument = visited.ArgumentList.Arguments[0].WithExpression(
                RewriteNodeImplicitValue(node.ArgumentList.Arguments[0].Expression, true));
            return visited.WithArgumentList(visited.ArgumentList.WithArguments(
                SyntaxFactory.SingletonSeparatedList(argument)));
        }
        if (node.Expression is MemberAccessExpressionSyntax member && NodeValueIndex(node) is int index)
            return RewriteNodeValue(member.Expression, index, false, node);
        if (GetConverterCollection(node) is { } converters)
            return RewriteRegistration(node, converters);
        return RewriteJsonInvocation(node);
    }

    private SyntaxNode? RewriteMetadataApi(InvocationExpressionSyntax node)
    {
        if (node.Expression is not MemberAccessExpressionSyntax member) return null;
        var call = semantic.GetOperation(node) as IInvocationOperation;
        string? owner = call?.TargetMethod.ContainingType.ToDisplayString();
        if (owner == "System.Text.Json.Serialization.Metadata.JsonTypeInfo" &&
            call!.TargetMethod.Name == "CreateJsonTypeInfo" && call.TargetMethod.TypeArguments.Length == 1 &&
            node.ArgumentList.Arguments.Count == 1)
        {
            var type = call.TargetMethod.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
            return visited.WithExpression(SyntaxFactory.ParseExpression("global::GeneratedJson.CreateStaticObjectInfo<" +
                type + ">")).WithTriviaFrom(node);
        }
        if ((owner == "System.Text.Json.Serialization.Metadata.JsonTypeInfo" || owner is null) &&
            member.Name.Identifier.ValueText == "CreateJsonPropertyInfo" && node.ArgumentList.Arguments.Count == 2 &&
            node.ArgumentList.Arguments[0].Expression is TypeOfExpressionSyntax typeOf &&
            semantic.GetTypeInfo(typeOf.Type).Type is { } propertyType)
        {
            var type = propertyType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return ContextCall("global::GeneratedJson.CreateStaticPropertyInfo<" + type + ">",
                (ExpressionSyntax)Visit(member.Expression)!,
                (ExpressionSyntax)Visit(node.ArgumentList.Arguments[1].Expression)!).NormalizeWhitespace().WithTriviaFrom(node);
        }
        if (owner == OptionsTypeName && call!.TargetMethod.Name == "GetConverter" &&
            node.ArgumentList.Arguments.Count == 1 && node.ArgumentList.Arguments[0].Expression is TypeOfExpressionSyntax)
        {
            var options = (ExpressionSyntax)Visit(member.Expression)!;
            var type = (ExpressionSyntax)Visit(node.ArgumentList.Arguments[0].Expression)!;
            var info = SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression, options, SyntaxFactory.IdentifierName("GetTypeInfo")),
                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(type))));
            return SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, info,
                SyntaxFactory.IdentifierName("Converter")).NormalizeWhitespace().WithTriviaFrom(node);
        }
        return null;
    }

    private ExpressionSyntax RewriteNodeImplicitValue(ExpressionSyntax expression, bool force = false)
    {
        var valueType = semantic.GetTypeInfo(expression).Type;
        if (valueType is null || IsJsonNode(valueType) || expression.IsKind(SyntaxKind.NullLiteralExpression) ||
            !force && semantic.GetTypeInfo(expression).ConvertedType?.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString() !=
                "System.Text.Json.Nodes.JsonNode")
            return (ExpressionSyntax)Visit(expression)!;
        if (!emitter.TryRegister(valueType, out int index))
            throw new InvalidOperationException("Unsupported JSON node value: " + expression);
        var encode = SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression(CodecName(index) + "Encode_" + index),
            SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(
                (ExpressionSyntax)Visit(expression)!))));
        return ContextCall("global::System.Text.Json.Nodes.JsonNode.Parse", encode).WithTriviaFrom(expression);
    }

    public override SyntaxNode? VisitConditionalAccessExpression(ConditionalAccessExpressionSyntax node)
    {
        if (node.WhenNotNull is ConditionalAccessExpressionSyntax nested &&
            nested.Expression is ElementBindingExpressionSyntax && nested.WhenNotNull is InvocationExpressionSyntax call &&
            NodeValueIndex(call) is int nestedIndex)
            return RewriteNodeValue(SyntaxFactory.ConditionalAccessExpression(node.Expression, nested.Expression),
                nestedIndex, true, node);
        if (node.WhenNotNull is InvocationExpressionSyntax invocation && NodeValueIndex(invocation) is int index)
            return RewriteNodeValue(node.Expression, index, true, node);
        return base.VisitConditionalAccessExpression(node);
    }

    private int? NodeValueIndex(InvocationExpressionSyntax invocation)
    {
        if (semantic.GetOperation(invocation) is not IInvocationOperation operation ||
            operation.TargetMethod.Name != "GetValue" || operation.TargetMethod.ContainingType.ToDisplayString() !=
            "System.Text.Json.Nodes.JsonNode" || operation.TargetMethod.TypeArguments.Length != 1) return null;
        if (!emitter.TryRegister(operation.TargetMethod.TypeArguments[0], out int index))
            throw new InvalidOperationException("Unsupported JsonNode.GetValue type: " + invocation);
        emitter.RegisterNodeValue(index);
        return index;
    }

    private SyntaxNode RewriteNodeValue(ExpressionSyntax value, int index, bool conditional, SyntaxNode original)
    {
        var argument = SyntaxFactory.Argument((ExpressionSyntax)Visit(value)!);
        return SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression(CodecName(index) +
            (conditional ? "NullableNodeGetValue_" : "NodeGetValue_") + index),
            SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(argument))).WithTriviaFrom(original);
    }

    private MemberAccessExpressionSyntax? GetConverterCollection(InvocationExpressionSyntax node)
    {
        if (node.Expression is MemberAccessExpressionSyntax add && add.Name.Identifier.ValueText == "Add" &&
            add.Expression is MemberAccessExpressionSyntax converters && converters.Name.Identifier
                .ValueText == "Converters" &&
            semantic.GetTypeInfo(converters.Expression).Type?.ToDisplayString() ==
                OptionsTypeName)
            return converters;
        return null;
    }

    private SyntaxNode RewriteRegistration(InvocationExpressionSyntax node, MemberAccessExpressionSyntax converters)
    {
        var argumentType = node.ArgumentList.Arguments.Count == 1 ? semantic.GetTypeInfo(
            node.ArgumentList.Arguments[0].Expression).Type as INamedTypeSymbol : null;
        if (argumentType is not null && argumentType.ToDisplayString() !=
            "System.Text.Json.Serialization.JsonStringEnumConverter" && argumentType.OriginalDefinition.ToDisplayString() !=
            "System.Text.Json.Serialization.JsonStringEnumConverter<TEnum>")
            return base.VisitInvocationExpression(node)!;
        if (node.ArgumentList.Arguments.Count != 1 || semantic.GetTypeInfo(node.ArgumentList.Arguments[0].Expression)
            .Type is not INamedTypeSymbol converterType)
            throw new InvalidOperationException("Unsupported converter registration.");
        var creation = node.ArgumentList.Arguments[0].Expression as ObjectCreationExpressionSyntax;
        if (converterType.ToDisplayString() == "System.Text.Json.Serialization.JsonStringEnumConverter")
        {
            emitter.RegisterAllEnums();
            var factoryOptions = (ExpressionSyntax)Visit(converters.Expression)!;
            IEnumerable<ArgumentSyntax> settings;
            if (creation is not null) settings = creation.ArgumentList?.Arguments.Select(argument =>
                (ArgumentSyntax)Visit(argument)!) ?? [];
            else if (ConverterSettings(node.ArgumentList.Arguments[0].Expression) is { } configured)
                settings = configured;
            else throw new InvalidOperationException("Unsupported enum converter registration.");
            var factoryArguments = SyntaxFactory.SeparatedList(new[] { SyntaxFactory.Argument(factoryOptions) }.Concat(settings));
            return SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression("global::GeneratedJson.RegisterAllEnums"),
                SyntaxFactory.ArgumentList(factoryArguments)).WithTriviaFrom(node);
        }
        bool isEnum = converterType.OriginalDefinition.ToDisplayString() ==
            "System.Text.Json.Serialization.JsonStringEnumConverter<TEnum>";
        IEnumerable<ArgumentSyntax> enumArguments = [];
        if (isEnum && creation is not null)
            enumArguments = creation.ArgumentList?.Arguments.Select(argument => (ArgumentSyntax)Visit(argument)!) ?? [];
        else if (isEnum && ConverterSettings(node.ArgumentList.Arguments[0].Expression) is { } configured)
            enumArguments = configured;
        else if (isEnum)
            throw new InvalidOperationException("Unsupported enum converter registration.");
        ITypeSymbol? converted = ConverterTarget(converterType, isEnum);
        if (converted is null || !emitter.TryRegister(converted, out int converterIndex))
            throw new InvalidOperationException("Unsupported converter type: " + converterType);
        var options = (ExpressionSyntax)Visit(converters.Expression)!;
        var arguments = isEnum ? SyntaxFactory.SeparatedList(new[] { SyntaxFactory.Argument(options) }.Concat(
            enumArguments)) :
            SyntaxFactory.SeparatedList(new[] { SyntaxFactory.Argument(options),
                (ArgumentSyntax)Visit(node.ArgumentList.Arguments[0])! });
        return SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression(CodecName(converterIndex) +
            "Register" + (isEnum ? "Enum_" : "_") + converterIndex), SyntaxFactory.ArgumentList(arguments))
            .WithTriviaFrom(node);
    }

    private IEnumerable<ArgumentSyntax>? ConverterSettings(ExpressionSyntax expression)
    {
        var symbol = semantic.GetSymbolInfo(expression).Symbol;
        if (symbol is not ILocalSymbol && symbol is not IFieldSymbol) return null;
        if (symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not VariableDeclaratorSyntax
            {
                Initializer.Value: ObjectCreationExpressionSyntax initializer,
                Parent: VariableDeclarationSyntax { Variables.Count: 1 }
            } || !SupportedConverterLocal(initializer, semantic.Compilation.GetSemanticModel(initializer.SyntaxTree))) return null;
        var target = (ExpressionSyntax)Visit(expression)!;
        return new[] { "NamingPolicy", "AllowIntegerValues" }.Select(name => SyntaxFactory.Argument(
            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, target.WithoutTrivia(),
                SyntaxFactory.IdentifierName(name))));
    }

    private static bool IsConverterFactory(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
            if (current.ToDisplayString() == "System.Text.Json.Serialization.JsonConverterFactory") return true;
        return false;
    }

    private static ITypeSymbol? ConverterTarget(INamedTypeSymbol converterType, bool isEnum)
    {
        INamedTypeSymbol? baseType = converterType;
        while (baseType is not null && baseType.OriginalDefinition.ToDisplayString() !=
            "System.Text.Json.Serialization.JsonConverter<T>")
            baseType = baseType.BaseType;
        return isEnum ? converterType.TypeArguments[0] : baseType?.TypeArguments[0];
    }

    private SyntaxNode? RewriteJsonInvocation(InvocationExpressionSyntax node)
    {
        if (TryRewriteDefaultContext(node) is { } rewrittenContext) return rewrittenContext;
        if (semantic.GetOperation(node) is not IInvocationOperation operation ||
            operation.TargetMethod.ContainingType.ToDisplayString() != "System.Text.Json.JsonSerializer")
            return base.VisitInvocationExpression(node);
        var method = operation.TargetMethod;
        if (method.Name is not ("Serialize" or "Deserialize" or "SerializeToUtf8Bytes" or "SerializeAsync"
            or "DeserializeAsync" or "DeserializeAsyncEnumerable" or "SerializeToElement" or
                "SerializeToDocument" or "SerializeToNode"))
            return base.VisitInvocationExpression(node);
        if (method.Parameters.Any(p => p.Type.ToDisplayString()
            .StartsWith("System.Text.Json.Serialization.Metadata.JsonTypeInfo", StringComparison.Ordinal) ||
            p.Type.ToDisplayString() == "System.Text.Json.Serialization.JsonSerializerContext"))
            return base.VisitInvocationExpression(node);
        var optionArguments = ValidateOptions(operation);
        ITypeSymbol? target = method.TypeArguments.Length == 1 ? method.TypeArguments[0] :
            node.ArgumentList.Arguments.Select(a => a.Expression).OfType<TypeOfExpressionSyntax>()
                .Select(a => semantic.GetTypeInfo(a.Type).Type).FirstOrDefault();
        if (method.Name == "Serialize" && node.ArgumentList.Arguments.Count == 1 &&
            method.ReturnType.SpecialType == SpecialType.System_String && target is INamedTypeSymbol invalidTarget &&
            HasInvalidIgnore(invalidTarget))
        {
            var name = SyntaxFactory.ParseExpression("global::GeneratedJson.InvalidEncodeContract<" +
                target.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ">");
            return node.WithExpression(name);
        }
        if (method.Name == "Deserialize" && node.ArgumentList.Arguments.Count == 1 &&
            method.Parameters[0].Type.SpecialType == SpecialType.System_String && target is INamedTypeSymbol namedTarget &&
            HasInvalidContract(namedTarget))
        {
            var name = SyntaxFactory.ParseExpression("global::GeneratedJson.InvalidContract<" +
                target.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ">");
            return node.WithExpression(name);
        }
        if (method.Name == "Deserialize" && node.ArgumentList.Arguments.Count == 1 &&
            method.Parameters[0].Type.SpecialType == SpecialType.System_String && target is INamedTypeSymbol populateTarget &&
            HasParameterizedPopulate(populateTarget))
        {
            var name = SyntaxFactory.ParseExpression("global::GeneratedJson.InvalidPopulateContract<" +
                target.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ">");
            return node.WithExpression(name);
        }
        if (target is not null && method.Name is "Serialize" or "Deserialize" && optionArguments.Length == 1 &&
            optionArguments[0].Syntax is ArgumentSyntax resolverArgument && UsesMetadataResolver(resolverArgument.Expression))
            return RewriteResolverInvocation(node, resolverArgument, target);
        if (method.Name == "SerializeAsync" && target is INamedTypeSymbol sequence &&
            AsyncElement(sequence) is { } element && emitter.TryRegister(element, out int elementIndex))
        {
            bool declared = method.TypeArguments.Length == 0;
            if (declared && sequence.OriginalDefinition.ToDisplayString() !=
                "System.Collections.Generic.IAsyncEnumerable<T>")
                throw new InvalidOperationException("A declared asynchronous sequence type must be its interface.");
            emitter.RegisterAsyncWriter(elementIndex);
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
            if (method.Parameters[0].Type.ToDisplayString() == "System.IO.Pipelines.PipeWriter")
                visited = RewritePipe(node, visited, operation);
            return visited.WithExpression(SyntaxFactory.ParseExpression(CodecName(elementIndex) +
                (declared ? "EncodeDeclaredAsyncSequence_" : "EncodeAsyncSequence_") + elementIndex));
        }
        if (target is not null && method.Name is "Serialize" or "Deserialize" or "SerializeAsync" or
            "DeserializeAsync" && asyncContexts.Register(target))
            return RewriteAsyncContextInvocation(node, operation, target, optionArguments);
        int typePosition = method.Parameters.IndexOf(method.Parameters.FirstOrDefault(parameter =>
            parameter.Type.ToDisplayString() == "System.Type")!);
        bool runtimeType = target is null && typePosition >= 0;
        bool stringInput = method.Parameters[0].Type.SpecialType == SpecialType.System_String;
        bool bytesInput = method.Parameters[0].Type.ToDisplayString() == "System.ReadOnlySpan<byte>";
        bool charsInput = method.Parameters[0].Type.ToDisplayString() == "System.ReadOnlySpan<char>";
        string firstType = method.Parameters[0].Type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString();
        bool domInput = firstType is "System.Text.Json.JsonElement" or "System.Text.Json.JsonDocument" or
            "System.Text.Json.Nodes.JsonNode";
        bool readerInput = firstType == "System.Text.Json.Utf8JsonReader";
        bool writerInput = firstType == "System.Text.Json.Utf8JsonWriter";
        bool streamInput = method.Parameters[0].Type.ToDisplayString() is "System.IO.Stream" or
            "System.IO.Pipelines.PipeReader" or "System.IO.Pipelines.PipeWriter";
        if (runtimeType && (typePosition == 1 && (method.Name is "Serialize" or "SerializeToUtf8Bytes" or
            "SerializeToElement" or "SerializeToDocument" or "SerializeToNode" ||
            method.Name == "Deserialize" && (stringInput || bytesInput || charsInput || streamInput || domInput || readerInput) ||
            method.Name == "DeserializeAsync" && streamInput) || typePosition == 2 && streamInput &&
            method.Name is "Serialize" or "SerializeAsync" || typePosition == 2 && writerInput &&
            method.Name == "Serialize"))
        {
            emitter.TryRegister(semantic.Compilation.GetSpecialType(SpecialType.System_Object), out _);
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
            if (method.Parameters[0].Type.ToDisplayString() is "System.IO.Pipelines.PipeReader" or
                "System.IO.Pipelines.PipeWriter") visited = RewritePipe(node, visited, operation);
            string runtimeMethod = method.Name switch
            {
                "SerializeAsync" => "EncodeRuntimeStreamAsync",
                "DeserializeAsync" => "DecodeRuntimeStreamAsync",
                "Serialize" when streamInput => "EncodeRuntimeStream",
                "Serialize" when writerInput => "EncodeRuntimeWriter",
                "Deserialize" when streamInput => "DecodeRuntimeStream",
                "Deserialize" when readerInput => "DecodeRuntimeReader",
                "Serialize" => "EncodeRuntime",
                "SerializeToUtf8Bytes" => "EncodeRuntimeBytes",
                "SerializeToElement" => "EncodeRuntimeElement",
                "SerializeToDocument" => "EncodeRuntimeDocument",
                "SerializeToNode" => "EncodeRuntimeNode",
                "Deserialize" when firstType.EndsWith("JsonElement", StringComparison.Ordinal) => "DecodeRuntimeElement",
                "Deserialize" when firstType.EndsWith("JsonDocument", StringComparison.Ordinal) => "DecodeRuntimeDocument",
                "Deserialize" when domInput => "DecodeRuntimeNode",
                _ when bytesInput => "DecodeRuntimeBytes",
                _ when charsInput => "DecodeRuntimeChars",
                _ => "DecodeRuntime"
            };
            return visited.WithExpression(SyntaxFactory.ParseExpression("global::GeneratedJson." + runtimeMethod));
        }
        if (target is not null && !emitter.TryRegister(target, out _) &&
            method.Name is "Serialize" or "Deserialize" or "SerializeAsync" or "DeserializeAsync" &&
            asyncContexts.RegisterUnsupported(target))
            return RewriteAsyncContextInvocation(node, operation, target, optionArguments);
        if (target is null || !emitter.TryRegister(target, out int index))
            throw new InvalidOperationException("Unsupported JSON operation or type: " + target + " at " +
                node.GetLocation().GetLineSpan() + ": " + node);
        if (method.Name == "DeserializeAsyncEnumerable") emitter.RegisterAsyncSequence(index);
        if (optionArguments.Any(a => MayPopulate(((ArgumentSyntax)a.Syntax).Expression)) && !emitter
            .SupportsPopulate(target))
            throw new InvalidOperationException("Populate options are not supported for this JSON type: " + target);
        var rewritten = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
        if (target is INamedTypeSymbol { IsAnonymousType: true } anonymous && method.Name == "Serialize")
            rewritten = RewriteAnonymousValue(rewritten, anonymous, index);
        if (method.Parameters[0].Type.ToDisplayString() is "System.IO.Pipelines.PipeReader" or
            "System.IO.Pipelines.PipeWriter") rewritten = RewritePipe(node, rewritten, operation);
        string operationName = method.Name == "DeserializeAsyncEnumerable" && method.Parameters.Any(parameter =>
            parameter.Name == "topLevelValues") ? "DecodeTopLevelSequenceAsync_" : OperationName(method.Name);
        return rewritten.WithExpression(SyntaxFactory.ParseExpression(CodecName(index) + operationName + index));
    }

    private static bool HasInvalidContract(INamedTypeSymbol type)
    {
        if (ConverterAttributeEmitter.Converter(type, type) is not null) return false;
        bool multipleConstructors = type.InstanceConstructors.Count(constructor => constructor.GetAttributes().Any(
            attribute => attribute.AttributeClass?.ToDisplayString() ==
                "System.Text.Json.Serialization.JsonConstructorAttribute")) > 1;
        bool invalidExtension = CodecEmitter.HierarchyMembers(type).OfType<IPropertySymbol>().Any(property =>
            property.Type.SpecialType != SpecialType.None && property.GetAttributes().Any(attribute =>
                attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonExtensionDataAttribute"));
        var extension = CodecEmitter.HierarchyMembers(type).OfType<IPropertySymbol>().FirstOrDefault(property =>
            property.GetAttributes().Any(attribute => CodecEmitter.IsJsonAttribute(attribute, "JsonExtensionDataAttribute")));
        bool extensionParameter = extension is not null && SelectedConstructor(type) is { } constructor &&
            constructor.Parameters.Any(parameter => string.Equals(parameter.Name, extension.Name, StringComparison.OrdinalIgnoreCase) &&
                SymbolEqualityComparer.Default.Equals(parameter.Type, extension.Type));
        return multipleConstructors || invalidExtension || extensionParameter;
    }

    private static bool HasInvalidIgnore(INamedTypeSymbol type) =>
        ConverterAttributeEmitter.Converter(type, type) is null &&
        (CodecEmitter.Properties(type).Any(property => InvalidIgnore(property, property.Type)) ||
            CodecEmitter.Fields(type).Any(field => InvalidIgnore(field, field.Type)));

    private static bool InvalidIgnore(ISymbol member, ITypeSymbol type) => CodecEmitter.IgnoreCondition(member) == 3 &&
        type.IsValueType && type is not INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

    private static bool HasParameterizedPopulate(INamedTypeSymbol type)
    {
        if (ConverterAttributeEmitter.Converter(type, type) is not null) return false;
        if (SelectedConstructor(type) is not { Parameters.Length: > 0 }) return false;
        return CodecEmitter.HierarchyMembers(type).Any(member => member is IPropertySymbol or IFieldSymbol &&
            (PopulateEmitter.CollectionElementType(member) is not null || PopulateEmitter.MapTypes(member) is not null ||
                PopulateEmitter.ObjectType(member) is not null) && member.GetAttributes().Any(attribute =>
                CodecEmitter.IsJsonAttribute(attribute, "JsonObjectCreationHandlingAttribute") &&
                attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is 1));
    }

    private static IMethodSymbol? SelectedConstructor(INamedTypeSymbol type)
    {
        var marked = type.InstanceConstructors.Where(CodecEmitter.IsJsonConstructor).ToArray();
        if (marked.Length != 0) return marked.Length == 1 ? marked[0] : null;
        var accessible = type.InstanceConstructors.Where(constructor =>
            constructor.DeclaredAccessibility == Accessibility.Public).ToArray();
        return accessible.FirstOrDefault(constructor => constructor.Parameters.Length == 0) ??
            (accessible.Length == 1 ? accessible[0] : null);
    }

    private static InvocationExpressionSyntax RewriteAnonymousValue(InvocationExpressionSyntax node,
        INamedTypeSymbol type, int index)
    {
        var original = node.ArgumentList.Arguments[0];
        var properties = CodecEmitter.Properties(type);
        var arguments = properties.Select(property => SyntaxFactory.Argument(SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("value"),
            SyntaxFactory.IdentifierName(property.Name))));
        var creation = SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("GeneratedAnonymous_" + index))
            .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));
        var projection = SyntaxFactory.SimpleLambdaExpression(SyntaxFactory.Parameter(SyntaxFactory.Identifier("value")),
            creation).WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.StaticKeyword)));
        var call = SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression("global::GeneratedJson.Project"),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] { SyntaxFactory.Argument(original.Expression),
                SyntaxFactory.Argument(projection) })));
        return node.WithArgumentList(node.ArgumentList.WithArguments(node.ArgumentList.Arguments.Replace(original,
            original.WithExpression(call)))).NormalizeWhitespace().WithTriviaFrom(node);
    }

    private SyntaxNode RewriteAsyncContextInvocation(InvocationExpressionSyntax node, IInvocationOperation operation,
        ITypeSymbol target, IArgumentOperation[] options)
    {
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
        ExpressionSyntax context = options.Length == 0 ? ContextCall("global::GeneratedAsyncJsonContext.ForOptions",
            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)) :
            ContextCall("global::GeneratedAsyncJsonContext.ForOptions",
                (ExpressionSyntax)Visit(((ArgumentSyntax)options[0].Syntax).Expression)!);
        var type = SyntaxFactory.ParseTypeName(target.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        var lookup = SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression, context, SyntaxFactory.IdentifierName("GetTypeInfo")),
            SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(
                SyntaxFactory.TypeOfExpression(type)))));
        var metadataType = SyntaxFactory.ParseTypeName("global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<" +
            target.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ">");
        var nonNullLookup = SyntaxFactory.PostfixUnaryExpression(SyntaxKind.SuppressNullableWarningExpression, lookup);
        var metadata = SyntaxFactory.Argument(SyntaxFactory.ParenthesizedExpression(
            SyntaxFactory.CastExpression(metadataType, nonNullLookup)));
        if (options.Length != 0)
        {
            int position = node.ArgumentList.Arguments.IndexOf((ArgumentSyntax)options[0].Syntax);
            if (visited.ArgumentList.Arguments[position].NameColon is not null)
                metadata = metadata.WithNameColon(SyntaxFactory.NameColon("jsonTypeInfo"));
            return visited.WithArgumentList(visited.ArgumentList.WithArguments(visited.ArgumentList.Arguments.Replace(
                visited.ArgumentList.Arguments[position], metadata))).NormalizeWhitespace().WithTriviaFrom(node);
        }
        int insertion = operation.TargetMethod.Name.StartsWith("Serialize", StringComparison.Ordinal) &&
            operation.TargetMethod.Parameters[0].Type.ToDisplayString() == "System.IO.Stream" ? 2 : 1;
        return visited.WithArgumentList(visited.ArgumentList.WithArguments(
            visited.ArgumentList.Arguments.Insert(insertion, metadata))).NormalizeWhitespace().WithTriviaFrom(node);
    }

    private bool UsesMetadataResolver(ExpressionSyntax expression)
    {
        if (semantic.GetSymbolInfo(expression).Symbol is not ILocalSymbol local ||
            local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not VariableDeclaratorSyntax declaration)
            return false;
        if (declaration.Initializer?.Value is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Options" })
            return true;
        if (declaration.Initializer?.Value.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>()
            .Any(assignment => assignment.Left is IdentifierNameSyntax { Identifier.ValueText: "TypeInfoResolver" }) == true)
            return true;
        var scope = declaration.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
        return scope?.DescendantNodes().OfType<IdentifierNameSyntax>().Any(identifier =>
            SymbolEqualityComparer.Default.Equals(semantic.GetSymbolInfo(identifier).Symbol, local) &&
            identifier.Parent is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText:
                "TypeInfoResolver" or "TypeInfoResolverChain"
            }) == true;
    }

    private SyntaxNode RewriteResolverInvocation(InvocationExpressionSyntax node, ArgumentSyntax argument,
        ITypeSymbol target)
    {
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
        int position = node.ArgumentList.Arguments.IndexOf(argument);
        var options = visited.ArgumentList.Arguments[position].Expression;
        var getInfo = SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression, options.WithoutTrivia(), SyntaxFactory.IdentifierName("GetTypeInfo")),
            SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(
                SyntaxFactory.TypeOfExpression(SyntaxFactory.ParseTypeName(target.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat)))))));
        var metadataType = SyntaxFactory.ParseTypeName("global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<" +
            target.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ">");
        var metadata = SyntaxFactory.CastExpression(metadataType,
            SyntaxFactory.PostfixUnaryExpression(SyntaxKind.SuppressNullableWarningExpression, getInfo));
        var replacement = SyntaxFactory.Argument(metadata).WithTriviaFrom(visited.ArgumentList.Arguments[position]);
        return visited.WithArgumentList(visited.ArgumentList.WithArguments(visited.ArgumentList.Arguments.Replace(
            visited.ArgumentList.Arguments[position], replacement)));
    }

    private SyntaxNode? TryRewriteDefaultContext(InvocationExpressionSyntax node)
    {
        if (node.Expression is not MemberAccessExpressionSyntax call ||
            semantic.GetSymbolInfo(call.Expression).Symbol is not INamedTypeSymbol serializer ||
            serializer.ToDisplayString() != "System.Text.Json.JsonSerializer") return null;
        for (int position = 0; position < node.ArgumentList.Arguments.Count; position++)
        {
            var expression = node.ArgumentList.Arguments[position].Expression;
            var info = ContextTarget(expression, node);
            if (info is null) continue;
            if (call.Name.Identifier.ValueText is not ("Serialize" or "Deserialize" or "SerializeToUtf8Bytes" or
                "SerializeToElement" or "SerializeToDocument" or "SerializeToNode" or "SerializeAsync" or "DeserializeAsync"))
                throw new InvalidOperationException("Unsupported JSON context operation: " + node);
            if (!emitter.TryRegister(info.Value.Type, out int index))
                throw new InvalidOperationException("Unsupported JSON context type: " + info.Value.Type);
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
            var original = visited.ArgumentList.Arguments[position];
            var replacement = SyntaxFactory.Argument(info.Value.Options ?? SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
            if (original.NameColon is not null) replacement = replacement.WithNameColon(SyntaxFactory.NameColon("options"));
            var arguments = visited.ArgumentList.Arguments.Replace(original, replacement.WithTriviaFrom(original));
            return visited.WithArgumentList(visited.ArgumentList.WithArguments(arguments))
                .WithExpression(SyntaxFactory.ParseExpression(CodecName(index) + OperationName(call.Name.Identifier.ValueText) + index));
        }
        return null;
    }

    private (ITypeSymbol Type, ExpressionSyntax? Options)? ContextTarget(ExpressionSyntax expression,
        InvocationExpressionSyntax invocation)
    {
        if (expression is IdentifierNameSyntax && semantic.GetSymbolInfo(expression).Symbol is ILocalSymbol local &&
            ContextAlias.Find(semantic, local) is { } alias &&
            ContextTarget(alias.Declaration.Initializer!.Value, invocation) is { } resolved)
            return (resolved.Type, expression);
        if (expression is not MemberAccessExpressionSyntax access) return null;
        bool typeInfo = access.Name.Identifier.ValueText != "Default";
        var defaultAccess = typeInfo ? access.Expression as MemberAccessExpressionSyntax : access;
        if (defaultAccess?.Name.Identifier.ValueText != "Default" || semantic.GetSymbolInfo(defaultAccess.Expression)
            .Symbol is not INamedTypeSymbol context || !IsJsonContext(context)) return null;
        if (context.InstanceConstructors
            .Any(constructor => !constructor.IsImplicitlyDeclared && constructor.Locations.Any(location => location.IsInSource)) ||
            context.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax()).OfType<TypeDeclarationSyntax>()
                .Any(declaration => declaration.Members.Count != 0) || context.DeclaringSyntaxReferences.Length == 0 ||
            context.DeclaredAccessibility != Accessibility.Internal || !ContextUsesAreDirect(context))
            return null;
        var roots = context.GetAttributes().Where(attribute => attribute.AttributeClass?.ToDisplayString() ==
            "System.Text.Json.Serialization.JsonSerializableAttribute").ToArray();
        if (roots.Any(attribute => attribute.NamedArguments.Any(argument => argument.Key == "GenerationMode" &&
            argument.Value.Value is int value && value is not (0 or 1 or 3))))
            return null;
        var explicitType = invocation.ArgumentList.Arguments.Select(argument => argument.Expression)
            .OfType<TypeOfExpressionSyntax>().Select(item => semantic.GetTypeInfo(item.Type).Type).FirstOrDefault();
        var types = ContextTypeGraph.Collect(roots, context);
        var matches = types.Where(item => typeInfo ? item.Name == access.Name.Identifier.ValueText :
            explicitType is null ? roots.Length == 1 && SymbolEqualityComparer.Default.Equals(item.Type,
                roots[0].ConstructorArguments[0].Value as ITypeSymbol) : SymbolEqualityComparer.Default.Equals(item.Type,
                    explicitType)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("JSON context type cannot be determined at build time.");
        var selected = matches[0].Type;
        var options = ContextOptionsEmitter.Create(context);
        foreach (var converter in ContextOptionsEmitter.Converters(context))
            options = ConfigureContextConverter(options, converter);
        if (ContextOptionsEmitter.StringEnums(context))
        {
            emitter.RegisterAllEnums();
            options = SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression("global::GeneratedJson.ConfigureAllEnums"),
                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(options))));
        }
        emitter.RegisterContextOptions();
        var key = SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal(context.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        var factory = SyntaxFactory.ParenthesizedLambdaExpression(options);
        options = ContextCall("global::GeneratedJson.GetContextOptions", key, factory);
        if (ContextOptionsEmitter.Populates(context) && !emitter.SupportsPopulate(selected))
            throw new InvalidOperationException("Context populate options are not supported for this JSON type.");
        return (selected, options);
    }

    private ExpressionSyntax ConfigureContextConverter(ExpressionSyntax options, INamedTypeSymbol converter)
    {
        if (IsConverterFactory(converter) && converter.DeclaringSyntaxReferences.Length != 0)
        {
            emitter.RegisterConverterFactories();
            var factory = SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName(converter.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat))).WithArgumentList(SyntaxFactory.ArgumentList()).NormalizeWhitespace();
            return ContextCall("global::GeneratedJson.WithConverters", options, factory);
        }
        bool globalEnum = converter.ToDisplayString() == "System.Text.Json.Serialization.JsonStringEnumConverter";
        bool genericEnum = converter.OriginalDefinition.ToDisplayString() ==
            "System.Text.Json.Serialization.JsonStringEnumConverter<TEnum>";
        if (globalEnum)
        {
            emitter.RegisterAllEnums();
            return ContextCall("global::GeneratedJson.ConfigureAllEnums", options);
        }
        var target = ConverterTarget(converter, genericEnum);
        if (target is null || !emitter.TryRegister(target, out int index))
            throw new InvalidOperationException("Unsupported JSON context converter: " + converter);
        if (genericEnum) return ContextCall(CodecName(index) + "ConfigureEnum_" + index, options);
        if (converter.DeclaringSyntaxReferences.Length == 0 || !converter.InstanceConstructors.Any(constructor =>
            constructor.Parameters.Length == 0 && (constructor.DeclaredAccessibility is Accessibility.Public or
                Accessibility.Internal or Accessibility.ProtectedOrInternal)))
            throw new InvalidOperationException("JSON context converters must be locally constructible.");
        var creation = SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName(converter.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat))).WithArgumentList(SyntaxFactory.ArgumentList()).NormalizeWhitespace();
        return ContextCall(CodecName(index) + "ConfigureConverter_" + index, options, creation);
    }

    private static ExpressionSyntax ContextCall(string name, params ExpressionSyntax[] arguments) =>
        SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression(name), SyntaxFactory.ArgumentList(
            SyntaxFactory.SeparatedList(arguments.Select(SyntaxFactory.Argument))));

    private bool ContextUsesAreDirect(INamedTypeSymbol context)
    {
        if (safeContexts.TryGetValue(context, out bool result)) return result;
        result = semantic.Compilation.SyntaxTrees.All(tree => ContextTreeUsesAreDirect(tree, context));
        safeContexts.Add(context, result);
        return result;
    }

    private bool ContextTreeUsesAreDirect(SyntaxTree tree, INamedTypeSymbol context)
    {
        var model = semantic.Compilation.GetSemanticModel(tree);
        foreach (var access in tree.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(item => item.Name.Identifier.ValueText == "Default"))
        {
            if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(access.Expression).Symbol, context)) continue;
            SyntaxNode selected = access.Parent is MemberAccessExpressionSyntax member ? member : access;
            if (selected.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declaration } &&
                model.GetDeclaredSymbol(declaration) is ILocalSymbol local && ContextAlias.Find(model, local) is not null)
                continue;
            if (selected.Parent is not ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax invocation } ||
                invocation.Expression is not MemberAccessExpressionSyntax call || model.GetSymbolInfo(call.Expression).Symbol is not
                    INamedTypeSymbol serializer || serializer.ToDisplayString() != "System.Text.Json.JsonSerializer") return false;
        }
        foreach (var identifier in tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (identifier.IsVar || !SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier).Symbol, context)) continue;
            if (!identifier.Ancestors().OfType<MemberAccessExpressionSyntax>().Any(access =>
                access.Name.Identifier.ValueText == "Default" && access.Expression.Span.Contains(identifier.Span) &&
                SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(access.Expression).Symbol, context))) return false;
        }
        return true;
    }

    private static bool IsJsonContext(INamedTypeSymbol context)
    {
        for (INamedTypeSymbol? current = context; current is not null; current = current.BaseType)
            if (current.ToDisplayString() == "System.Text.Json.Serialization.JsonSerializerContext") return true;
        return false;
    }

    private static ITypeSymbol? AsyncElement(INamedTypeSymbol type)
    {
        var contract = type.OriginalDefinition.ToDisplayString() ==
            "System.Collections.Generic.IAsyncEnumerable<T>" ? type : type.AllInterfaces.FirstOrDefault(item =>
                item.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IAsyncEnumerable<T>");
        return contract?.TypeArguments[0];
    }

    private static InvocationExpressionSyntax RewritePipe(InvocationExpressionSyntax original,
        InvocationExpressionSyntax rewritten, IInvocationOperation operation)
    {
        var argument = operation.Arguments.First(item => item.Parameter?.Ordinal == 0 && !item.IsImplicit);
        int position = original.ArgumentList.Arguments.IndexOf((ArgumentSyntax)argument.Syntax);
        var selected = rewritten.ArgumentList.Arguments[position];
        var error = SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("global::System.ArgumentNullException"))
            .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(
                SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal("utf8Json"))))));
        var checkedValue = SyntaxFactory.ParenthesizedExpression(SyntaxFactory.BinaryExpression(
            SyntaxKind.CoalesceExpression, selected.Expression.WithoutTrivia(), SyntaxFactory.ThrowExpression(error)));
        var call = SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression, checkedValue, SyntaxFactory.IdentifierName("AsStream")),
            SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(
                SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression)))));
        return rewritten.ReplaceNode(selected, selected.WithExpression(call.NormalizeWhitespace()
            .WithTriviaFrom(selected.Expression)));
    }

    private static string CodecName(int index) => "global::GeneratedJsonCodec_" + index + ".";

    private static string OperationName(string name) => name switch
    {
        "Serialize" => "Encode_",
        "Deserialize" => "Decode_",
        "SerializeToUtf8Bytes" => "EncodeBytes_",
        "SerializeToElement" => "EncodeElement_",
        "SerializeToDocument" => "EncodeDocument_",
        "SerializeToNode" => "EncodeNode_",
        "SerializeAsync" => "EncodeAsync_",
        "DeserializeAsyncEnumerable" => "DecodeSequenceAsync_",
        _ => "DecodeAsync_"
    };

    private IArgumentOperation[] ValidateOptions(IInvocationOperation operation)
    {
        var arguments = operation.Arguments.Where(a => !a.IsImplicit && a.Parameter?.Type
            .WithNullableAnnotation(NullableAnnotation.None).ToDisplayString() ==
                OptionsTypeName && a.Syntax is ArgumentSyntax).ToArray();
        return arguments;
    }

    private bool OptionsSupported(ExpressionSyntax expression)
    {
        if (expression.IsKind(SyntaxKind.NullLiteralExpression)) return true;
        if (semantic.GetSymbolInfo(expression).Symbol is IPropertySymbol
            { Name: "Default" or "Web" or "Strict", IsStatic: true } property &&
            property.ContainingType.ToDisplayString() == OptionsTypeName) return true;
        if (expression is ObjectCreationExpressionSyntax creation) return SupportedCreation(creation
            .ArgumentList, creation.Initializer);
        if (expression is ImplicitObjectCreationExpressionSyntax implicitCreation) return
            SupportedCreation(implicitCreation.ArgumentList, implicitCreation.Initializer);
        if (semantic.GetSymbolInfo(expression).Symbol is not ILocalSymbol local ||
            local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not VariableDeclaratorSyntax declaration ||
            declaration.Initializer?.Value is not ExpressionSyntax initializer) return false;
        bool supported = initializer switch
        {
            ObjectCreationExpressionSyntax c => SupportedCreation(c.ArgumentList, c.Initializer),
            ImplicitObjectCreationExpressionSyntax c => SupportedCreation(c.ArgumentList, c.Initializer),
            _ => false
        };
        if (!supported) return false;
        var scope = declaration.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault() as
            SyntaxNode ?? declaration.SyntaxTree.GetRoot();
        return OptionsReferencesSupported(scope, local);
    }

    private bool OptionsReferencesSupported(SyntaxNode scope, ILocalSymbol local)
    {
        var references = scope.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(reference => SymbolEqualityComparer.Default.Equals(semantic.GetSymbolInfo(reference).Symbol, local))
            .ToArray();
        if (references.Any(reference => !IsSerializerArgument(reference)) &&
            (references.Any(reference => IsSerializerArgument(reference) && HasUncertainLifetime(reference, scope)) ||
             scope.DescendantNodes().Any(node => node is GotoStatementSyntax or LabeledStatementSyntax))) return false;
        int firstUse = references.Where(IsSerializerArgument).Select(reference => reference.SpanStart)
            .DefaultIfEmpty(int.MaxValue).Min();
        foreach (var reference in references)
        {
            if (IsSerializerArgument(reference)) continue;
            if (reference.SpanStart < firstUse && OptionsMemberSupported(reference) &&
                !HasUncertainLifetime(reference, scope)) continue;
            return false;
        }
        return true;
    }

    private bool IsSerializerArgument(IdentifierNameSyntax reference) =>
        reference.Parent is ArgumentSyntax argument && argument.Parent?.Parent is
            InvocationExpressionSyntax invocation && semantic.GetOperation(invocation) is IInvocationOperation call &&
        call.TargetMethod.ContainingType.ToDisplayString() == "System.Text.Json.JsonSerializer";

    private static bool HasUncertainLifetime(SyntaxNode node, SyntaxNode scope) =>
        node.Ancestors().TakeWhile(parent => parent != scope).Any(parent => parent is ForStatementSyntax or
            ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax or AnonymousFunctionExpressionSyntax
            or LocalFunctionStatementSyntax);

    private bool OptionsMemberSupported(IdentifierNameSyntax reference)
    {
        if (reference.Parent is not MemberAccessExpressionSyntax member || member.Expression != reference) return false;
        if (member.Name.Identifier.ValueText == "Converters" && member.Parent is MemberAccessExpressionSyntax add &&
            add.Name.Identifier.ValueText == "Add" && add.Parent is InvocationExpressionSyntax registration &&
            registration.ArgumentList.Arguments.Count == 1) return true;
        return member.Parent is AssignmentExpressionSyntax assignment && assignment.Left == member &&
            SupportedAssignment(member.Name.Identifier.ValueText, assignment.Right);
    }

    private bool SupportedCreation(ArgumentListSyntax? arguments, InitializerExpressionSyntax? initializer) =>
        (arguments is null || arguments.Arguments.Count == 0 || arguments.Arguments.Count == 1 &&
            IsSerializerDefaults(arguments.Arguments[0].Expression)) &&
        (initializer is null || initializer.Expressions.All(e => e is AssignmentExpressionSyntax a &&
            a.Left is IdentifierNameSyntax name && SupportedAssignment(name.Identifier.ValueText, a.Right)));

    private bool IsSerializerDefaults(ExpressionSyntax expression) => semantic.GetSymbolInfo(expression).Symbol is
        IFieldSymbol { Name: "General" or "Web" or "Strict" } field &&
        field.ContainingType.ToDisplayString() == "System.Text.Json.JsonSerializerDefaults";

    private static bool SupportedProperty(string name) => name is "PropertyNamingPolicy" or "WriteIndented"
        or "PreferredObjectCreationHandling" or "Encoder" or "PropertyNameCaseInsensitive"
        or "AllowTrailingCommas" or "ReadCommentHandling" or "MaxDepth" or "DefaultIgnoreCondition"
        or "DictionaryKeyPolicy" or "UnmappedMemberHandling" or "IndentCharacter" or "IndentSize" or "NewLine"
        or "IgnoreReadOnlyProperties" or "IgnoreReadOnlyFields" or "IncludeFields" or "AllowDuplicateProperties"
        or "NumberHandling" or "RespectRequiredConstructorParameters" or "RespectNullableAnnotations"
        or "DefaultBufferSize" or "UnknownTypeHandling" or "AllowOutOfOrderMetadataProperties" or "ReferenceHandler";

    private bool SupportedAssignment(string name, ExpressionSyntax value)
    {
        if (!SupportedProperty(name)) return false;
        return true;
    }

    private bool MayPopulate(ExpressionSyntax expression)
    {
        var nodes = expression.DescendantNodesAndSelf();
        if (semantic.GetSymbolInfo(expression).Symbol is ILocalSymbol local &&
            local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax declaration)
            nodes = (declaration.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault() as
                SyntaxNode ?? declaration.SyntaxTree.GetRoot()).DescendantNodes();
        return nodes.OfType<AssignmentExpressionSyntax>().Any(a => a.Left switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText == "PreferredObjectCreationHandling",
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText ==
                "PreferredObjectCreationHandling" &&
                semantic.GetSymbolInfo(expression).Symbol is ILocalSymbol selected && SymbolEqualityComparer
                    .Default.Equals(semantic.GetSymbolInfo(member.Expression).Symbol, selected),
            _ => false
        });
    }

    private bool UsedBySerializer(ExpressionSyntax expression)
    {
        if (semantic.GetSymbolInfo(expression).Symbol is not ILocalSymbol local ||
            local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not VariableDeclaratorSyntax
                declaration) return false;
        var scope = declaration.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault() as
            SyntaxNode ?? declaration.SyntaxTree.GetRoot();
        return scope.DescendantNodes().OfType<IdentifierNameSyntax>().Any(reference =>
            SymbolEqualityComparer.Default.Equals(semantic.GetSymbolInfo(reference).Symbol, local) &&
            reference.Parent is ArgumentSyntax argument && argument.Parent?.Parent is
                InvocationExpressionSyntax invocation &&
            semantic.GetOperation(invocation) is IInvocationOperation call &&
            call.TargetMethod.ContainingType.ToDisplayString() == "System.Text.Json.JsonSerializer");
    }

    private bool ConverterUsesSupported(INamedTypeSymbol converter)
    {
        foreach (var tree in semantic.Compilation.SyntaxTrees)
        {
            var model = semantic.Compilation.GetSemanticModel(tree);
            foreach (var identifier in tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier).Symbol, converter)) continue;
                if (identifier.IsVar && identifier.Parent is VariableDeclarationSyntax) continue;
                if (identifier.Parent is TypeOfExpressionSyntax typeOf && typeOf.Ancestors()
                    .OfType<AttributeSyntax>().FirstOrDefault() is { } attribute &&
                    model.GetSymbolInfo(attribute).Symbol is IMethodSymbol constructor &&
                    constructor.ContainingType.ToDisplayString() ==
                        "System.Text.Json.Serialization.JsonConverterAttribute") continue;
                if (identifier.Parent is TypeOfExpressionSyntax contextTypeOf && contextTypeOf.Ancestors()
                    .OfType<AttributeSyntax>().FirstOrDefault() is { } contextAttribute &&
                    model.GetSymbolInfo(contextAttribute).Symbol is IMethodSymbol contextConstructor &&
                    contextConstructor.ContainingType.ToDisplayString() ==
                        "System.Text.Json.Serialization.JsonSourceGenerationOptionsAttribute") continue;
                if (identifier.Parent is ObjectCreationExpressionSyntax creation && creation.Type == identifier &&
                    creation.Parent is ArgumentSyntax argument && argument.Parent?.Parent is
                        InvocationExpressionSyntax invocation &&
                    invocation.Expression is MemberAccessExpressionSyntax
                    {
                        Name.Identifier.ValueText:
                        "Add", Expression: MemberAccessExpressionSyntax
                        {
                            Name.Identifier.ValueText:
                        "Converters"
                        }
                    }) continue;
                if (identifier.Parent is ObjectCreationExpressionSyntax localCreation && localCreation.Type == identifier &&
                    SupportedConverterLocal(localCreation, model)) continue;
                if (identifier.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault() is { } declaration &&
                    model.GetDeclaredSymbol(declaration) is INamedTypeSymbol enclosing && IsConverterFactory(enclosing))
                    continue;
                return false;
            }
        }
        return true;
    }

    private static bool SupportedConverterLocal(ObjectCreationExpressionSyntax creation, SemanticModel model)
    {
        if (creation.Parent is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declaration } ||
            model.GetDeclaredSymbol(declaration) is not { } symbol) return false;
        if (symbol is ILocalSymbol)
        {
            var scope = declaration.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault() as SyntaxNode ??
                declaration.SyntaxTree.GetRoot();
            return scope.DescendantNodes().OfType<IdentifierNameSyntax>().Where(reference =>
                SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(reference).Symbol, symbol))
                .All(IsConverterRegistration);
        }
        if (symbol is not IFieldSymbol { DeclaredAccessibility: Accessibility.Private } field) return false;
        foreach (var part in field.ContainingType.DeclaringSyntaxReferences)
        {
            var scope = part.GetSyntax();
            var treeModel = model.Compilation.GetSemanticModel(scope.SyntaxTree);
            if (scope.DescendantNodes().OfType<IdentifierNameSyntax>().Where(reference =>
                SymbolEqualityComparer.Default.Equals(treeModel.GetSymbolInfo(reference).Symbol, symbol))
                .Any(reference => !IsConverterRegistration(reference))) return false;
        }
        return true;
    }

    private static bool IsConverterRegistration(IdentifierNameSyntax reference)
    {
        SyntaxNode expression = reference.Parent is MemberAccessExpressionSyntax member && member.Name == reference ?
            member : reference;
        return expression.Parent is ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax invocation } &&
            invocation.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "Add",
                Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Converters" }
            };
    }

    private static bool IsEnumConverter(ObjectCreationExpressionSyntax creation, SemanticModel model) =>
        model.GetTypeInfo(creation).Type is INamedTypeSymbol converter &&
        (converter.ToDisplayString() == "System.Text.Json.Serialization.JsonStringEnumConverter" ||
         converter.OriginalDefinition.ToDisplayString() == "System.Text.Json.Serialization.JsonStringEnumConverter<TEnum>");
}

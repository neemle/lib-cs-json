using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

if (args.Length != 6 || args[0] != "--manifest")
    throw new ArgumentException(("Usage: JsonGenerator --manifest <sources> <references> " +
            "<output> <defines> <language>"));
var sources = File.ReadAllLines(args[1]).Where(File.Exists).ToArray();
var output = args[3];
var references = File.ReadAllLines(args[2]).Where(File.Exists);
var language = LanguageVersion.Default;
var languageValue = File.ReadAllText(args[5]).Trim();
if (languageValue.Length != 0 && !LanguageVersionFacts.TryParse(languageValue, out language))
    throw new InvalidOperationException("Unsupported C# language version: " + languageValue);
var symbols = File.ReadAllLines(args[4]).SelectMany(line =>
    line.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray();
var trees = sources.Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path),
    new CSharpParseOptions(language, preprocessorSymbols: symbols), path)).ToArray();
var compilation = CSharpCompilation.Create("Input", trees,
    references.Distinct().Select(path => MetadataReference.CreateFromFile(path)),
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
var emitter = new CodecEmitter();
Directory.CreateDirectory(output);
for (int i = 0; i < trees.Length; i++)
{
    var rewriter = new Rewriter(compilation.GetSemanticModel(trees[i]), emitter);
    var path = Path.Combine(output, $"Source{i}.g.cs");
    File.WriteAllText(path, "#nullable enable\n#line 1 \"" + Path.GetFullPath(sources[i]).Replace("\\",
        "\\\\").Replace("\"", "\\\"") +
        "\"\n" + rewriter.Visit(trees[i].GetRoot())!.ToFullString());
}
GeneratedCodeLayout.Write(output, emitter.Generate());

sealed class Rewriter(SemanticModel semantic, CodecEmitter emitter) : CSharpSyntaxRewriter
{
    private const string OptionsTypeName = "System.Text.Json.JsonSerializerOptions";

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        var target = semantic.GetTypeInfo(node.Left).Type?.WithNullableAnnotation(NullableAnnotation.None);
        var value = semantic.GetTypeInfo(node.Right).Type;
        if (node.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
            target?.ToDisplayString() == "System.Text.Json.Nodes.JsonNode" && value is not null &&
            value.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString() !=
                "System.Text.Json.Nodes.JsonNode")
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

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        if (node.BaseList is null) return base.VisitClassDeclaration(node);
        for (int i = 0; i < node.BaseList.Types.Count; i++)
        {
            var entry = node.BaseList.Types[i];
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

    private void ValidateConverterClass(ClassDeclarationSyntax node)
    {
        if (semantic.GetDeclaredSymbol(node) is not INamedTypeSymbol converter ||
            converter.DeclaredAccessibility != Accessibility.Internal || !converter.IsSealed ||
            HasUnsupportedConverterNodes(node) || !ConverterUsesSupported(converter))
            throw new InvalidOperationException(("Only local sealed JSON converters used directly in " +
            "registration are supported: ") + node.Identifier);
    }

    private bool HasUnsupportedConverterNodes(ClassDeclarationSyntax node) =>
        node.DescendantNodes().Any(n => n is ThisExpressionSyntax or BaseExpressionSyntax) ||
        node.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Any(m =>
            m.Name.Identifier.ValueText == "Converters" &&
            semantic.GetTypeInfo(m.Expression).Type?.WithNullableAnnotation(NullableAnnotation.None)
                .ToDisplayString() == OptionsTypeName) ||
        node.DescendantNodes().OfType<IdentifierNameSyntax>().Any(id =>
            semantic.GetSymbolInfo(id).Symbol is IParameterSymbol parameter &&
            parameter.Type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString() ==
                OptionsTypeName);

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (GetConverterCollection(node) is { } converters)
            return RewriteRegistration(node, converters);
        return RewriteJsonInvocation(node);
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
        if (!OptionsSupported(converters.Expression) || !UsedBySerializer(converters.Expression))
            throw new InvalidOperationException(("Converter registration outside a supported JSON " +
            "serialization path is not supported."));
        if (node.ArgumentList.Arguments.Count != 1 || node.ArgumentList.Arguments[0].Expression is not
            ObjectCreationExpressionSyntax creation ||
            semantic.GetTypeInfo(creation).Type is not INamedTypeSymbol converterType)
            throw new InvalidOperationException("Unsupported converter registration; expected direct construction.");
        bool isEnum = converterType.OriginalDefinition.ToDisplayString() ==
            "System.Text.Json.Serialization.JsonStringEnumConverter<TEnum>";
        if (isEnum && creation.ArgumentList is { Arguments.Count: > 0 })
            throw new InvalidOperationException("JsonStringEnumConverter constructor options are not supported.");
        ITypeSymbol? converted = ConverterTarget(converterType, isEnum);
        if (converted is null || !emitter.TryRegister(converted, out int converterIndex))
            throw new InvalidOperationException("Unsupported converter type: " + converterType);
        var options = (ExpressionSyntax)Visit(converters.Expression)!;
        var arguments = isEnum ? SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(options)) :
            SyntaxFactory.SeparatedList(new[] { SyntaxFactory.Argument(options),
                (ArgumentSyntax)Visit(node.ArgumentList.Arguments[0])! });
        return SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression(CodecName(converterIndex) +
            "Register" + (isEnum ? "Enum_" : "_") + converterIndex), SyntaxFactory.ArgumentList(arguments))
            .WithTriviaFrom(node);
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
        if (semantic.GetOperation(node) is not IInvocationOperation operation ||
            operation.TargetMethod.ContainingType.ToDisplayString() != "System.Text.Json.JsonSerializer")
            return base.VisitInvocationExpression(node);
        var method = operation.TargetMethod;
        if (method.Name is not ("Serialize" or "Deserialize" or "SerializeToUtf8Bytes" or "SerializeAsync"
            or "DeserializeAsync"))
            return base.VisitInvocationExpression(node);
        if (method.Parameters.Any(p => p.Type.ToDisplayString()
            .StartsWith("System.Text.Json.Serialization.Metadata.JsonTypeInfo", StringComparison.Ordinal)))
            throw new InvalidOperationException(("JsonTypeInfo overloads require reflection metadata in the " +
            "stock runtime and are not supported: ") + node);
        var optionArguments = ValidateOptions(operation);
        ITypeSymbol? target = method.TypeArguments.Length == 1 ? method.TypeArguments[0] :
            node.ArgumentList.Arguments.Select(a => a.Expression).OfType<TypeOfExpressionSyntax>()
                .Select(a => semantic.GetTypeInfo(a.Type).Type).FirstOrDefault();
        if (target is null || !emitter.TryRegister(target, out int index))
            throw new InvalidOperationException("Unsupported JSON operation or type: " + node);
        if (optionArguments.Any(a => MayPopulate(((ArgumentSyntax)a.Syntax).Expression)) && !emitter
            .SupportsPopulate(target))
            throw new InvalidOperationException("Populate options are not supported for this JSON type: " + target);
        var rewritten = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
        return rewritten.WithExpression(SyntaxFactory.ParseExpression(CodecName(index) +
            OperationName(method.Name) + index));
    }

    private static string CodecName(int index) => "global::GeneratedJsonCodec_" + index + ".";

    private static string OperationName(string name) => name switch
    {
        "Serialize" => "Encode_", "Deserialize" => "Decode_", "SerializeToUtf8Bytes" => "EncodeBytes_",
        "SerializeAsync" => "EncodeAsync_", _ => "DecodeAsync_"
    };

    private IArgumentOperation[] ValidateOptions(IInvocationOperation operation)
    {
        var arguments = operation.Arguments.Where(a => !a.IsImplicit && a.Parameter?.Type
            .WithNullableAnnotation(NullableAnnotation.None).ToDisplayString() ==
                OptionsTypeName && a.Syntax is ArgumentSyntax).ToArray();
        foreach (var argument in arguments)
        {
            var syntax = (ArgumentSyntax)argument.Syntax;
            if (!OptionsSupported(syntax.Expression))
                throw new InvalidOperationException("Unsupported JsonSerializerOptions usage: " + syntax);
        }
        return arguments;
    }

    private bool OptionsSupported(ExpressionSyntax expression)
    {
        if (expression.IsKind(SyntaxKind.NullLiteralExpression)) return true;
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
            registration.ArgumentList.Arguments.Count == 1 && registration.ArgumentList.Arguments[0]
                .Expression is ObjectCreationExpressionSyntax) return true;
        return member.Parent is AssignmentExpressionSyntax assignment && assignment.Left == member &&
            SupportedAssignment(member.Name.Identifier.ValueText, assignment.Right);
    }

    private bool SupportedCreation(ArgumentListSyntax? arguments, InitializerExpressionSyntax? initializer) =>
        (arguments is null || arguments.Arguments.Count == 0) &&
        (initializer is null || initializer.Expressions.All(e => e is AssignmentExpressionSyntax a &&
            a.Left is IdentifierNameSyntax name && SupportedAssignment(name.Identifier.ValueText, a.Right)));

    private static bool SupportedProperty(string name) => name is "PropertyNamingPolicy" or "WriteIndented"
        or "PreferredObjectCreationHandling" or "Encoder";

    private bool SupportedAssignment(string name, ExpressionSyntax value)
    {
        if (!SupportedProperty(name)) return false;
        if (name != "PropertyNamingPolicy" || value.IsKind(SyntaxKind.NullLiteralExpression)) return true;
        return semantic.GetSymbolInfo(value).Symbol is IPropertySymbol property && property.Name == "CamelCase" &&
            property.ContainingType.ToDisplayString() == "System.Text.Json.JsonNamingPolicy";
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
                if (identifier.Parent is ObjectCreationExpressionSyntax creation && creation.Type == identifier &&
                    creation.Parent is ArgumentSyntax argument && argument.Parent?.Parent is
                        InvocationExpressionSyntax invocation &&
                    invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText:
                        "Add", Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText:
                        "Converters" } }) continue;
                return false;
            }
        }
        return true;
    }
}

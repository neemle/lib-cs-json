using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Neemle.Json.Generator;

sealed partial class Rewriter(SemanticModel semantic, CodecEmitter emitter, AsyncContextEmitter asyncContexts,
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
        return visited.WithExpression(SyntaxFactory.ParenthesizedExpression(
            RewriteEnumDisplay(node.Expression, visited.Expression).WithoutTrivia()).WithTriviaFrom(visited.Expression));
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
}

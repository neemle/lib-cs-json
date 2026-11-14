using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Neemle.Json.Generator;

sealed partial class Rewriter
{
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

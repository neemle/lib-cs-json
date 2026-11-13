using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

internal static class ContextAlias
{
    internal static (VariableDeclaratorSyntax Declaration, InvocationExpressionSyntax Invocation)? Find(
        SemanticModel model, ILocalSymbol local)
    {
        if (local.Type.TypeKind != Microsoft.CodeAnalysis.TypeKind.Error && !IsContextType(local.Type) &&
            !local.Type.ToDisplayString().StartsWith("System.Text.Json.Serialization.Metadata.JsonTypeInfo",
                StringComparison.Ordinal)) return null;
        if (local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not VariableDeclaratorSyntax declaration ||
            declaration.Initializer?.Value is not MemberAccessExpressionSyntax initializer ||
            initializer.Name.Identifier.ValueText != "Default" && initializer.Expression is not
                MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Default" } ||
            declaration.Parent is not VariableDeclarationSyntax { Variables.Count: 1 } ||
            declaration.Parent.Parent is not LocalDeclarationStatementSyntax) return null;
        var uses = declaration.SyntaxTree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(identifier => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier).Symbol, local))
            .ToArray();
        if (uses.Length == 0) return null;
        if (uses.Any(identifier => !IsSerializerArgument(model, identifier))) return null;
        return (declaration, (InvocationExpressionSyntax)uses[0].Parent!.Parent!.Parent!);
    }

    private static bool IsContextType(ITypeSymbol type)
    {
        for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
            if (current.ToDisplayString() == "System.Text.Json.Serialization.JsonSerializerContext") return true;
        return false;
    }

    private static bool IsSerializerArgument(SemanticModel model, IdentifierNameSyntax identifier)
    {
        if (identifier.Parent is not ArgumentSyntax { RefKindKeyword.RawKind: 0,
            Parent.Parent: InvocationExpressionSyntax call } argument) return false;
        if (model.GetSymbolInfo(call).Symbol is IMethodSymbol method)
            return method.ContainingType.ToDisplayString() == "System.Text.Json.JsonSerializer" &&
                model.GetOperation(argument) is IArgumentOperation operation && operation.Parameter is { } parameter &&
                (IsContextType(parameter.Type) || parameter.Type.ToDisplayString().StartsWith(
                    "System.Text.Json.Serialization.Metadata.JsonTypeInfo", StringComparison.Ordinal));
        return call.Expression is MemberAccessExpressionSyntax member && model.GetSymbolInfo(member.Expression).Symbol is
            INamedTypeSymbol serializer && serializer.ToDisplayString() == "System.Text.Json.JsonSerializer" &&
            call.ArgumentList.Arguments.Count >= 2 && call.ArgumentList.Arguments.Last() == argument;
    }
}

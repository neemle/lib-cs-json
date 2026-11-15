using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Neemle.Json.Generator;

internal static class HttpJsonRoots
{
    internal static IReadOnlyList<ITypeSymbol> Find(Compilation compilation, CancellationToken cancellationToken)
    {
        if (compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Builder.WebApplication") is null) return [];
        var roots = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var invocation in tree.GetRoot(cancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method ||
                    method.Name is not ("MapGet" or "MapPost" or "MapPut" or "MapPatch" or "MapDelete" or "MapMethods" or
                        "Ok" or "Json" or "ReadFromJsonAsync" or "WriteAsJsonAsync")) continue;
                string owner = method.ContainingNamespace.ToDisplayString();
                if (!owner.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) &&
                    !owner.StartsWith("System.Net.Http.Json", StringComparison.Ordinal)) continue;
                foreach (var type in method.TypeArguments) Add(type, compilation.Assembly, roots);
                foreach (var argument in invocation.ArgumentList.Arguments)
                {
                    Add(model.GetTypeInfo(argument.Expression, cancellationToken).Type, compilation.Assembly, roots);
                    if (model.GetSymbolInfo(argument.Expression, cancellationToken).Symbol is IMethodSymbol handler)
                    {
                        Add(handler.ReturnType, compilation.Assembly, roots);
                        foreach (var parameter in handler.Parameters) Add(parameter.Type, compilation.Assembly, roots);
                    }
                    foreach (var lambda in argument.DescendantNodesAndSelf().OfType<LambdaExpressionSyntax>())
                        if (lambda.ExpressionBody is { } expression)
                            Add(model.GetTypeInfo(expression, cancellationToken).Type, compilation.Assembly, roots);
                    foreach (var statement in argument.DescendantNodes().OfType<ReturnStatementSyntax>())
                        if (statement.Expression is { } expression)
                            Add(model.GetTypeInfo(expression, cancellationToken).Type, compilation.Assembly, roots);
                    foreach (var creation in argument.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
                        Add(model.GetTypeInfo(creation, cancellationToken).Type, compilation.Assembly, roots);
                    foreach (var parameter in argument.DescendantNodes().OfType<ParameterSyntax>())
                        Add((model.GetDeclaredSymbol(parameter, cancellationToken) as IParameterSymbol)?.Type, compilation.Assembly, roots);
                }
            }
        }
        return roots.ToArray();
    }

    private static void Add(ITypeSymbol? type, IAssemblySymbol assembly, HashSet<ITypeSymbol> roots)
    {
        if (type is IArrayTypeSymbol array && MetadataTypes.CandidateVisible(array, assembly))
        {
            roots.Add(array);
            Add(array.ElementType, assembly, roots);
            return;
        }
        if (type is not INamedTypeSymbol named || named.IsAnonymousType || named.IsUnboundGenericType ||
            named.SpecialType == SpecialType.System_Void ||
            named.TypeArguments.Any(argument => argument is ITypeParameterSymbol) ||
            !MetadataTypes.CandidateVisible(named, assembly)) return;
        string owner = named.ContainingNamespace.ToDisplayString();
        if (owner.StartsWith("System", StringComparison.Ordinal) || owner.StartsWith("Microsoft", StringComparison.Ordinal))
        {
            if (named.SpecialType != SpecialType.None || owner.StartsWith("System.Collections", StringComparison.Ordinal))
                roots.Add(named);
            foreach (var argument in named.TypeArguments) Add(argument, assembly, roots);
            return;
        }
        if (named.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Enum or TypeKind.Interface) roots.Add(named);
    }
}

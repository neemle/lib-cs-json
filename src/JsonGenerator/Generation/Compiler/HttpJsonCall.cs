using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Neemle.Json.Generator;

internal static class HttpJsonCall
{
    internal static bool TryCreate(SemanticModel model, InvocationExpressionSyntax invocation, out IMethodSymbol method)
    {
        method = null!;
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol candidate || candidate.TypeArguments.Length != 1 ||
            candidate.TypeArguments[0] is ITypeParameterSymbol || model.GetInterceptableLocation(invocation) is null) return false;
        string owner = candidate.ContainingType.ToDisplayString();
        bool result = candidate.Name == "Json" && owner is ("Microsoft.AspNetCore.Http.Results" or
            "Microsoft.AspNetCore.Http.TypedResults") && candidate.Parameters.Length == 4 &&
            candidate.Parameters[1].Type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString() ==
                "System.Text.Json.JsonSerializerOptions";
        bool extension = candidate.ReducedFrom is not null && candidate.Name is ("ReadFromJsonAsync" or "WriteAsJsonAsync") &&
            owner is ("Microsoft.AspNetCore.Http.HttpRequestJsonExtensions" or
                "Microsoft.AspNetCore.Http.HttpResponseJsonExtensions" or
                "System.Net.Http.Json.HttpContentJsonExtensions") &&
            !candidate.Parameters.Any(parameter => parameter.Type.ToDisplayString().StartsWith(
                "System.Text.Json.Serialization.Metadata.JsonTypeInfo", StringComparison.Ordinal));
        if (!result && !extension) return false;
        method = candidate;
        return true;
    }
}

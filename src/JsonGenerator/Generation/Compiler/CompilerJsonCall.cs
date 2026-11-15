using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Neemle.Json.Generator;

internal readonly record struct CompilerJsonCall(IMethodSymbol Method, ITypeSymbol Type, string Operation, bool RuntimeType)
{
    internal static bool TryCreate(SemanticModel model, InvocationExpressionSyntax call, out CompilerJsonCall result)
    {
        result = default;
        if (model.GetSymbolInfo(call).Symbol is not IMethodSymbol method ||
            method.ContainingType.ToDisplayString() != "System.Text.Json.JsonSerializer" ||
            !method.Parameters.Any(parameter => parameter.Type.WithNullableAnnotation(NullableAnnotation.None)
                .ToDisplayString() == "System.Text.Json.JsonSerializerOptions")) return false;
        string? operation = method.Name switch
        {
            "Serialize" => "Encode", "Deserialize" => "Decode", "SerializeToUtf8Bytes" => "EncodeBytes",
            "SerializeToElement" => "EncodeElement", "SerializeToDocument" => "EncodeDocument",
            "SerializeToNode" => "EncodeNode", "SerializeAsync" => "EncodeAsync",
            "DeserializeAsync" => "DecodeAsync",
            "SerializeAsyncEnumerable" => "Framework",
            "DeserializeAsyncEnumerable" when method.Parameters.Any(parameter => parameter.Name == "topLevelValues") =>
                "DecodeTopLevelSequenceAsync",
            "DeserializeAsyncEnumerable" => "DecodeSequenceAsync", _ => null
        };
        if (operation is null) return false;
        var type = method.TypeArguments.Length == 1 ? method.TypeArguments[0] :
            call.ArgumentList.Arguments.Select(argument => argument.Expression).OfType<TypeOfExpressionSyntax>()
                .Select(expression => model.GetTypeInfo(expression.Type).Type).FirstOrDefault();
        bool runtimeType = type is null && method.Parameters.Any(parameter => parameter.Type.ToDisplayString() == "System.Type");
        if (runtimeType) type = model.Compilation.GetSpecialType(SpecialType.System_Object);
        if (type is ITypeParameterSymbol && method.Parameters[0].Type.ToDisplayString() is not
            ("System.Text.Json.Utf8JsonReader" or "System.Text.Json.Utf8JsonWriter")) return false;
        if (type is null or { TypeKind: TypeKind.Error } || model.GetInterceptableLocation(call) is null) return false;
        result = new CompilerJsonCall(method, type, operation, runtimeType);
        return true;
    }
}

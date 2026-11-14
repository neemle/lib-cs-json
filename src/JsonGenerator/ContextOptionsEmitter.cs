using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class ContextOptionsEmitter
{
    private const string AttributeName = "System.Text.Json.Serialization.JsonSourceGenerationOptionsAttribute";

    internal static ExpressionSyntax Create(INamedTypeSymbol context)
    {
        var attribute = context.GetAttributes().FirstOrDefault(item => item.AttributeClass?.ToDisplayString() == AttributeName);
        if (attribute is null) return SyntaxFactory.ObjectCreationExpression(
            SyntaxFactory.ParseTypeName("global::System.Text.Json.JsonSerializerOptions"))
            .WithArgumentList(SyntaxFactory.ArgumentList()).NormalizeWhitespace();
        var arguments = attribute.ConstructorArguments.Length == 0 ? SyntaxFactory.ArgumentList() :
            SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(
                Constant(attribute.ConstructorArguments[0]))));
        var assignments = attribute.NamedArguments.Select(Assignment).Where(item => item is not null).ToArray();
        return SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("global::System.Text.Json.JsonSerializerOptions"))
            .WithArgumentList(arguments).WithInitializer(SyntaxFactory.InitializerExpression(SyntaxKind.ObjectInitializerExpression,
                SyntaxFactory.SeparatedList(assignments.OfType<ExpressionSyntax>()))).NormalizeWhitespace();
    }

    internal static bool Populates(INamedTypeSymbol context) => context.GetAttributes().FirstOrDefault(item =>
        item.AttributeClass?.ToDisplayString() == AttributeName)?.NamedArguments.Any(argument =>
            argument.Key == "PreferredObjectCreationHandling" && argument.Value.Value is int value && value != 0) == true;

    internal static bool StringEnums(INamedTypeSymbol context) => context.GetAttributes().FirstOrDefault(item =>
        item.AttributeClass?.ToDisplayString() == AttributeName)?.NamedArguments.Any(argument =>
            argument.Key == "UseStringEnumConverter" && argument.Value.Value is true) == true;

    internal static bool IncludesFields(INamedTypeSymbol context) => context.GetAttributes().FirstOrDefault(item =>
        item.AttributeClass?.ToDisplayString() == AttributeName)?.NamedArguments.Any(argument =>
            argument.Key == "IncludeFields" && argument.Value.Value is true) == true;

    internal static INamedTypeSymbol[] Converters(INamedTypeSymbol context)
    {
        var value = context.GetAttributes().FirstOrDefault(item => item.AttributeClass?.ToDisplayString() == AttributeName)?
            .NamedArguments.FirstOrDefault(argument => argument.Key == "Converters").Value;
        if (value is null || value.Value.IsNull || value.Value.Kind != TypedConstantKind.Array) return [];
        return value.Value.Values.Select(item => item.Value as INamedTypeSymbol ??
            throw new InvalidOperationException("Unsupported JSON context converter type.")).ToArray();
    }

    private static ExpressionSyntax? Assignment(KeyValuePair<string, TypedConstant> argument)
    {
        if (argument.Key == "GenerationMode")
        {
            if (argument.Value.Value is int mode && mode is not (0 or 1 or 2 or 3))
                throw new InvalidOperationException("Configured JSON context generation modes are not supported.");
            return null;
        }
        if (argument.Key == "UseStringEnumConverter") return null;
        if (argument.Key == "Converters") return null;
        ExpressionSyntax value = argument.Key switch
        {
            "PropertyNamingPolicy" or "DictionaryKeyPolicy" => KnownPolicy(argument.Value),
            "ReferenceHandler" => KnownReferenceHandler(argument.Value),
            "AllowOutOfOrderMetadataProperties" or "AllowTrailingCommas" or "DefaultBufferSize" or
                "DefaultIgnoreCondition" or "IgnoreReadOnlyFields" or "IgnoreReadOnlyProperties" or "IncludeFields" or
                "MaxDepth" or "NumberHandling" or "PreferredObjectCreationHandling" or "PropertyNameCaseInsensitive" or
                "ReadCommentHandling" or "RespectNullableAnnotations" or "RespectRequiredConstructorParameters" or
                "UnknownTypeHandling" or "UnmappedMemberHandling" or "WriteIndented" or "IndentCharacter" or
                "IndentSize" or "NewLine" or "AllowDuplicateProperties" => Constant(argument.Value),
            _ => throw new InvalidOperationException("Unsupported JSON context option: " + argument.Key)
        };
        return SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
            SyntaxFactory.IdentifierName(argument.Key), value);
    }

    private static ExpressionSyntax KnownPolicy(TypedConstant value) => (int?)value.Value switch
    {
        0 => SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression),
        1 => SyntaxFactory.ParseExpression("global::System.Text.Json.JsonNamingPolicy.CamelCase"),
        2 => SyntaxFactory.ParseExpression("global::System.Text.Json.JsonNamingPolicy.SnakeCaseLower"),
        3 => SyntaxFactory.ParseExpression("global::System.Text.Json.JsonNamingPolicy.SnakeCaseUpper"),
        4 => SyntaxFactory.ParseExpression("global::System.Text.Json.JsonNamingPolicy.KebabCaseLower"),
        5 => SyntaxFactory.ParseExpression("global::System.Text.Json.JsonNamingPolicy.KebabCaseUpper"),
        _ => throw new InvalidOperationException("Unsupported JSON context naming policy.")
    };

    private static ExpressionSyntax KnownReferenceHandler(TypedConstant value) => (int?)value.Value switch
    {
        0 => SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression),
        1 => SyntaxFactory.ParseExpression("global::System.Text.Json.Serialization.ReferenceHandler.Preserve"),
        2 => SyntaxFactory.ParseExpression("global::System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles"),
        _ => throw new InvalidOperationException("Unsupported JSON context reference handler.")
    };

    private static ExpressionSyntax Constant(TypedConstant constant)
    {
        if (constant.IsNull) return SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
        if (constant.Kind == TypedConstantKind.Enum)
            return SyntaxFactory.CastExpression(SyntaxFactory.ParseTypeName(constant.Type!.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat)), SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(Convert.ToInt32(constant.Value, System.Globalization.CultureInfo.InvariantCulture))));
        return constant.Value switch
        {
            bool value => SyntaxFactory.LiteralExpression(value ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression),
            int value => SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(value)),
            char value => SyntaxFactory.LiteralExpression(SyntaxKind.CharacterLiteralExpression, SyntaxFactory.Literal(value)),
            string value => SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(value)),
            _ => throw new InvalidOperationException("Unsupported JSON context option value.")
        };
    }
}

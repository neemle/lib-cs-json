using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Neemle.Json.Generator;

sealed partial class Rewriter
{
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
        if (node.WhenNotNull is ConditionalAccessExpressionSyntax member &&
            member.Expression is MemberBindingExpressionSyntax &&
            member.WhenNotNull is ConditionalAccessExpressionSyntax element &&
            element.Expression is ElementBindingExpressionSyntax && element.WhenNotNull is InvocationExpressionSyntax nestedCall &&
            NodeValueIndex(nestedCall) is int valueIndex)
            return RewriteNodeValue(SyntaxFactory.ConditionalAccessExpression(node.Expression,
                SyntaxFactory.ConditionalAccessExpression(member.Expression, element.Expression)), valueIndex, true, node);
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
}

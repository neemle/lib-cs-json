using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Neemle.Json.Generator;

sealed partial class Rewriter
{
    private SyntaxNode? RewriteJsonInvocation(InvocationExpressionSyntax node)
    {
        if (TryRewriteDefaultContext(node) is { } rewrittenContext) return rewrittenContext;
        if (semantic.GetOperation(node) is not IInvocationOperation operation ||
            operation.TargetMethod.ContainingType.ToDisplayString() != "System.Text.Json.JsonSerializer")
            return base.VisitInvocationExpression(node);
        var method = operation.TargetMethod;
        if (method.Name is not ("Serialize" or "Deserialize" or "SerializeToUtf8Bytes" or "SerializeAsync"
            or "DeserializeAsync" or "DeserializeAsyncEnumerable" or "SerializeAsyncEnumerable" or "SerializeToElement" or
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
        if (method.Name == "SerializeAsyncEnumerable" && target is not null && asyncContexts.RegisterUnsupported(target))
            return RewriteAsyncContextInvocation(node, operation, target, optionArguments);
        if (target is ITypeParameterSymbol && method.Parameters[0].Type.ToDisplayString() is
            "System.Text.Json.Utf8JsonReader" or "System.Text.Json.Utf8JsonWriter")
        {
            bool decode = method.Name == "Deserialize";
            if (!decode && method.Name != "Serialize")
                throw new InvalidOperationException("Unsupported generic JSON operation: " + node);
            emitter.TryRegister(semantic.Compilation.GetSpecialType(SpecialType.System_Object), out _);
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
            var type = SyntaxFactory.Argument(SyntaxFactory.TypeOfExpression(SyntaxFactory.ParseTypeName(target.Name)));
            var arguments = visited.ArgumentList.Arguments.Insert(decode ? 1 : 2, type);
            var call = visited.WithArgumentList(visited.ArgumentList.WithArguments(arguments)).WithExpression(
                SyntaxFactory.ParseExpression(decode ? "global::GeneratedJson.DecodeRuntimeReader" :
                    "global::GeneratedJson.EncodeRuntimeWriter"));
            return decode ? SyntaxFactory.ParenthesizedExpression(SyntaxFactory.CastExpression(
                SyntaxFactory.ParseTypeName(target.Name), SyntaxFactory.PostfixUnaryExpression(
                    SyntaxKind.SuppressNullableWarningExpression, call))) : call;
        }
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
}

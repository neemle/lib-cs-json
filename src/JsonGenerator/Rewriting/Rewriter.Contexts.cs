using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Neemle.Json.Generator;

sealed partial class Rewriter
{
    private SyntaxNode RewriteAsyncContextInvocation(InvocationExpressionSyntax node, IInvocationOperation operation,
        ITypeSymbol target, IArgumentOperation[] options)
    {
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
        ExpressionSyntax context = options.Length == 0 ? ContextCall("global::GeneratedAsyncJsonContext.ForOptions",
            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)) :
            ContextCall("global::GeneratedAsyncJsonContext.ForOptions",
                (ExpressionSyntax)Visit(((ArgumentSyntax)options[0].Syntax).Expression)!);
        var type = SyntaxFactory.ParseTypeName(target.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        var lookup = SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression, context, SyntaxFactory.IdentifierName("GetTypeInfo")),
            SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(
                SyntaxFactory.TypeOfExpression(type)))));
        var metadataType = SyntaxFactory.ParseTypeName("global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<" +
            target.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ">");
        var nonNullLookup = SyntaxFactory.PostfixUnaryExpression(SyntaxKind.SuppressNullableWarningExpression, lookup);
        var metadata = SyntaxFactory.Argument(SyntaxFactory.ParenthesizedExpression(
            SyntaxFactory.CastExpression(metadataType, nonNullLookup)));
        if (options.Length != 0)
        {
            int position = node.ArgumentList.Arguments.IndexOf((ArgumentSyntax)options[0].Syntax);
            if (visited.ArgumentList.Arguments[position].NameColon is not null)
                metadata = metadata.WithNameColon(SyntaxFactory.NameColon("jsonTypeInfo"));
            return visited.WithArgumentList(visited.ArgumentList.WithArguments(visited.ArgumentList.Arguments.Replace(
                visited.ArgumentList.Arguments[position], metadata))).NormalizeWhitespace().WithTriviaFrom(node);
        }
        int insertion = operation.TargetMethod.Name.StartsWith("Serialize", StringComparison.Ordinal) &&
            operation.TargetMethod.Parameters[0].Type.ToDisplayString() == "System.IO.Stream" ? 2 : 1;
        return visited.WithArgumentList(visited.ArgumentList.WithArguments(
            visited.ArgumentList.Arguments.Insert(insertion, metadata))).NormalizeWhitespace().WithTriviaFrom(node);
    }

    private bool UsesMetadataResolver(ExpressionSyntax expression)
    {
        if (semantic.GetSymbolInfo(expression).Symbol is not ILocalSymbol local ||
            local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not VariableDeclaratorSyntax declaration)
            return false;
        if (declaration.Initializer?.Value is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Options" })
            return true;
        if (declaration.Initializer?.Value.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>()
            .Any(assignment => assignment.Left is IdentifierNameSyntax { Identifier.ValueText: "TypeInfoResolver" }) == true)
            return true;
        var scope = declaration.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
        return scope?.DescendantNodes().OfType<IdentifierNameSyntax>().Any(identifier =>
            SymbolEqualityComparer.Default.Equals(semantic.GetSymbolInfo(identifier).Symbol, local) &&
            identifier.Parent is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText:
                "TypeInfoResolver" or "TypeInfoResolverChain"
            }) == true;
    }

    private SyntaxNode RewriteResolverInvocation(InvocationExpressionSyntax node, ArgumentSyntax argument,
        ITypeSymbol target)
    {
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
        int position = node.ArgumentList.Arguments.IndexOf(argument);
        var options = visited.ArgumentList.Arguments[position].Expression;
        var getInfo = SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression, options.WithoutTrivia(), SyntaxFactory.IdentifierName("GetTypeInfo")),
            SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(
                SyntaxFactory.TypeOfExpression(SyntaxFactory.ParseTypeName(target.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat)))))));
        var metadataType = SyntaxFactory.ParseTypeName("global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<" +
            target.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ">");
        var metadata = SyntaxFactory.CastExpression(metadataType,
            SyntaxFactory.PostfixUnaryExpression(SyntaxKind.SuppressNullableWarningExpression, getInfo));
        var replacement = SyntaxFactory.Argument(metadata).WithTriviaFrom(visited.ArgumentList.Arguments[position]);
        return visited.WithArgumentList(visited.ArgumentList.WithArguments(visited.ArgumentList.Arguments.Replace(
            visited.ArgumentList.Arguments[position], replacement)));
    }

    private SyntaxNode? TryRewriteDefaultContext(InvocationExpressionSyntax node)
    {
        if (node.Expression is not MemberAccessExpressionSyntax call ||
            semantic.GetSymbolInfo(call.Expression).Symbol is not INamedTypeSymbol serializer ||
            serializer.ToDisplayString() != "System.Text.Json.JsonSerializer") return null;
        for (int position = 0; position < node.ArgumentList.Arguments.Count; position++)
        {
            var expression = node.ArgumentList.Arguments[position].Expression;
            var info = ContextTarget(expression, node);
            if (info is null) continue;
            if (call.Name.Identifier.ValueText is not ("Serialize" or "Deserialize" or "SerializeToUtf8Bytes" or
                "SerializeToElement" or "SerializeToDocument" or "SerializeToNode" or "SerializeAsync" or "DeserializeAsync"))
                throw new InvalidOperationException("Unsupported JSON context operation: " + node);
            if (!emitter.TryRegister(info.Value.Type, out int index))
                throw new InvalidOperationException("Unsupported JSON context type: " + info.Value.Type);
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
            var original = visited.ArgumentList.Arguments[position];
            var replacement = SyntaxFactory.Argument(info.Value.Options ?? SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
            if (original.NameColon is not null) replacement = replacement.WithNameColon(SyntaxFactory.NameColon("options"));
            var arguments = visited.ArgumentList.Arguments.Replace(original, replacement.WithTriviaFrom(original));
            return visited.WithArgumentList(visited.ArgumentList.WithArguments(arguments))
                .WithExpression(SyntaxFactory.ParseExpression(CodecName(index) + OperationName(call.Name.Identifier.ValueText) + index));
        }
        return null;
    }

    private (ITypeSymbol Type, ExpressionSyntax? Options)? ContextTarget(ExpressionSyntax expression,
        InvocationExpressionSyntax invocation)
    {
        if (expression is IdentifierNameSyntax && semantic.GetSymbolInfo(expression).Symbol is ILocalSymbol local &&
            ContextAlias.Find(semantic, local) is { } alias &&
            ContextTarget(alias.Declaration.Initializer!.Value, invocation) is { } resolved)
            return (resolved.Type, expression);
        if (expression is not MemberAccessExpressionSyntax access) return null;
        bool typeInfo = access.Name.Identifier.ValueText != "Default";
        var defaultAccess = typeInfo ? access.Expression as MemberAccessExpressionSyntax : access;
        if (defaultAccess?.Name.Identifier.ValueText != "Default" || semantic.GetSymbolInfo(defaultAccess.Expression)
            .Symbol is not INamedTypeSymbol context || !IsJsonContext(context)) return null;
        if (context.InstanceConstructors
            .Any(constructor => !constructor.IsImplicitlyDeclared && constructor.Locations.Any(location => location.IsInSource)) ||
            context.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax()).OfType<TypeDeclarationSyntax>()
                .Any(declaration => declaration.Members.Count != 0) || context.DeclaringSyntaxReferences.Length == 0 ||
            context.DeclaredAccessibility != Accessibility.Internal || !ContextUsesAreDirect(context))
            return null;
        var roots = context.GetAttributes().Where(attribute => attribute.AttributeClass?.ToDisplayString() ==
            "System.Text.Json.Serialization.JsonSerializableAttribute").ToArray();
        if (roots.Any(attribute => attribute.NamedArguments.Any(argument => argument.Key == "GenerationMode" &&
            argument.Value.Value is int value && value is not (0 or 1 or 3))))
            return null;
        var explicitType = invocation.ArgumentList.Arguments.Select(argument => argument.Expression)
            .OfType<TypeOfExpressionSyntax>().Select(item => semantic.GetTypeInfo(item.Type).Type).FirstOrDefault();
        var types = ContextTypeGraph.Collect(roots, context);
        var matches = types.Where(item => typeInfo ? item.Name == access.Name.Identifier.ValueText :
            explicitType is null ? roots.Length == 1 && SymbolEqualityComparer.Default.Equals(item.Type,
                roots[0].ConstructorArguments[0].Value as ITypeSymbol) : SymbolEqualityComparer.Default.Equals(item.Type,
                    explicitType)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("JSON context type cannot be determined at build time.");
        var selected = matches[0].Type;
        var options = ContextOptionsEmitter.Create(context);
        foreach (var converter in ContextOptionsEmitter.Converters(context))
            options = ConfigureContextConverter(options, converter);
        if (ContextOptionsEmitter.StringEnums(context))
        {
            emitter.RegisterAllEnums();
            options = SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression("global::GeneratedJson.ConfigureAllEnums"),
                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(options))));
        }
        emitter.RegisterContextOptions();
        var key = SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal(context.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        var factory = SyntaxFactory.ParenthesizedLambdaExpression(options);
        options = ContextCall("global::GeneratedJson.GetContextOptions", key, factory);
        if (ContextOptionsEmitter.Populates(context) && !emitter.SupportsPopulate(selected))
            throw new InvalidOperationException("Context populate options are not supported for this JSON type.");
        return (selected, options);
    }

    private ExpressionSyntax ConfigureContextConverter(ExpressionSyntax options, INamedTypeSymbol converter)
    {
        if (IsConverterFactory(converter) && converter.DeclaringSyntaxReferences.Length != 0)
        {
            emitter.RegisterConverterFactories();
            var factory = SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName(converter.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat))).WithArgumentList(SyntaxFactory.ArgumentList()).NormalizeWhitespace();
            return ContextCall("global::GeneratedJson.WithConverters", options, factory);
        }
        bool globalEnum = converter.ToDisplayString() == "System.Text.Json.Serialization.JsonStringEnumConverter";
        bool genericEnum = converter.OriginalDefinition.ToDisplayString() ==
            "System.Text.Json.Serialization.JsonStringEnumConverter<TEnum>";
        if (globalEnum)
        {
            emitter.RegisterAllEnums();
            return ContextCall("global::GeneratedJson.ConfigureAllEnums", options);
        }
        var target = ConverterTarget(converter, genericEnum);
        if (target is null || !emitter.TryRegister(target, out int index))
            throw new InvalidOperationException("Unsupported JSON context converter: " + converter);
        if (genericEnum) return ContextCall(CodecName(index) + "ConfigureEnum_" + index, options);
        if (converter.DeclaringSyntaxReferences.Length == 0 || !converter.InstanceConstructors.Any(constructor =>
            constructor.Parameters.Length == 0 && (constructor.DeclaredAccessibility is Accessibility.Public or
                Accessibility.Internal or Accessibility.ProtectedOrInternal)))
            throw new InvalidOperationException("JSON context converters must be locally constructible.");
        var creation = SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName(converter.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat))).WithArgumentList(SyntaxFactory.ArgumentList()).NormalizeWhitespace();
        return ContextCall(CodecName(index) + "ConfigureConverter_" + index, options, creation);
    }

    private static ExpressionSyntax ContextCall(string name, params ExpressionSyntax[] arguments) =>
        SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression(name), SyntaxFactory.ArgumentList(
            SyntaxFactory.SeparatedList(arguments.Select(SyntaxFactory.Argument))));

    private bool ContextUsesAreDirect(INamedTypeSymbol context)
    {
        if (safeContexts.TryGetValue(context, out bool result)) return result;
        result = semantic.Compilation.SyntaxTrees.All(tree => ContextTreeUsesAreDirect(tree, context));
        safeContexts.Add(context, result);
        return result;
    }

    private bool ContextTreeUsesAreDirect(SyntaxTree tree, INamedTypeSymbol context)
    {
        var model = semantic.Compilation.GetSemanticModel(tree);
        foreach (var access in tree.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(item => item.Name.Identifier.ValueText == "Default"))
        {
            if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(access.Expression).Symbol, context)) continue;
            SyntaxNode selected = access.Parent is MemberAccessExpressionSyntax member ? member : access;
            if (selected.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declaration } &&
                model.GetDeclaredSymbol(declaration) is ILocalSymbol local && ContextAlias.Find(model, local) is not null)
                continue;
            if (selected.Parent is not ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax invocation } ||
                invocation.Expression is not MemberAccessExpressionSyntax call || model.GetSymbolInfo(call.Expression).Symbol is not
                    INamedTypeSymbol serializer || serializer.ToDisplayString() != "System.Text.Json.JsonSerializer") return false;
        }
        foreach (var identifier in tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (identifier.IsVar || !SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier).Symbol, context)) continue;
            if (!identifier.Ancestors().OfType<MemberAccessExpressionSyntax>().Any(access =>
                access.Name.Identifier.ValueText == "Default" && access.Expression.Span.Contains(identifier.Span) &&
                SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(access.Expression).Symbol, context))) return false;
        }
        return true;
    }

    private static bool IsJsonContext(INamedTypeSymbol context)
    {
        for (INamedTypeSymbol? current = context; current is not null; current = current.BaseType)
            if (current.ToDisplayString() == "System.Text.Json.Serialization.JsonSerializerContext") return true;
        return false;
    }

    private static ITypeSymbol? AsyncElement(INamedTypeSymbol type)
    {
        var contract = type.OriginalDefinition.ToDisplayString() ==
            "System.Collections.Generic.IAsyncEnumerable<T>" ? type : type.AllInterfaces.FirstOrDefault(item =>
                item.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IAsyncEnumerable<T>");
        return contract?.TypeArguments[0];
    }

    private static InvocationExpressionSyntax RewritePipe(InvocationExpressionSyntax original,
        InvocationExpressionSyntax rewritten, IInvocationOperation operation)
    {
        var argument = operation.Arguments.First(item => item.Parameter?.Ordinal == 0 && !item.IsImplicit);
        int position = original.ArgumentList.Arguments.IndexOf((ArgumentSyntax)argument.Syntax);
        var selected = rewritten.ArgumentList.Arguments[position];
        var error = SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("global::System.ArgumentNullException"))
            .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(
                SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal("utf8Json"))))));
        var checkedValue = SyntaxFactory.ParenthesizedExpression(SyntaxFactory.BinaryExpression(
            SyntaxKind.CoalesceExpression, selected.Expression.WithoutTrivia(), SyntaxFactory.ThrowExpression(error)));
        var call = SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression, checkedValue, SyntaxFactory.IdentifierName("AsStream")),
            SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(
                SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression)))));
        return rewritten.ReplaceNode(selected, selected.WithExpression(call.NormalizeWhitespace()
            .WithTriviaFrom(selected.Expression)));
    }

    private static string CodecName(int index) => "global::GeneratedJsonCodec_" + index + ".";

    private static string OperationName(string name) => name switch
    {
        "Serialize" => "Encode_",
        "Deserialize" => "Decode_",
        "SerializeToUtf8Bytes" => "EncodeBytes_",
        "SerializeToElement" => "EncodeElement_",
        "SerializeToDocument" => "EncodeDocument_",
        "SerializeToNode" => "EncodeNode_",
        "SerializeAsync" => "EncodeAsync_",
        "DeserializeAsyncEnumerable" => "DecodeSequenceAsync_",
        _ => "DecodeAsync_"
    };
}

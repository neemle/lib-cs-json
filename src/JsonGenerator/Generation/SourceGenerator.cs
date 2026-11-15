using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Neemle.Json.Generator;

internal static class SourceGenerator
{
    internal static void Generate(string sourcesFile, string referencesFile, string output,
        string definesFile, string languageFile, string nullableFile, string assemblyName)
    {
        var sources = File.ReadAllLines(sourcesFile).Where(File.Exists).ToArray();
        var references = File.ReadAllLines(referencesFile).Where(File.Exists);
        var language = LanguageVersion.Default;
        var languageValue = File.ReadAllText(languageFile).Trim();
        if (languageValue.Length != 0 && !LanguageVersionFacts.TryParse(languageValue, out language))
            throw new InvalidOperationException("Unsupported C# language version: " + languageValue);
        var symbols = File.ReadAllLines(definesFile).SelectMany(line =>
            line.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray();
        var nullable = File.ReadAllText(nullableFile).Trim().ToLowerInvariant() switch
        {
            "enable" => NullableContextOptions.Enable,
            "annotations" => NullableContextOptions.Annotations,
            "warnings" => NullableContextOptions.Warnings,
            _ => NullableContextOptions.Disable
        };
        string nullableDirectives = nullable switch
        {
            NullableContextOptions.Enable => "#nullable enable\n",
            NullableContextOptions.Annotations => "#nullable enable annotations\n#nullable disable warnings\n",
            NullableContextOptions.Warnings => "#nullable disable annotations\n#nullable enable warnings\n",
            _ => "#nullable disable\n"
        };
        var trees = sources.Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path),
            new CSharpParseOptions(language, preprocessorSymbols: symbols), path)).ToArray();
        var compilation = CSharpCompilation.Create("Input", trees,
            references.Distinct().Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: nullable));
        var emitter = new CodecEmitter(compilation.GetSpecialType(SpecialType.System_Object));
        var asyncContexts = new AsyncContextEmitter();
        var candidates = new List<ITypeSymbol>();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                ITypeSymbol? candidate = node switch
                {
                    BaseObjectCreationExpressionSyntax creation => model.GetTypeInfo(creation).Type,
                    BaseTypeDeclarationSyntax declaration => model.GetDeclaredSymbol(declaration) as ITypeSymbol,
                    TypeOfExpressionSyntax typeOf => model.GetTypeInfo(typeOf.Type).Type,
                    ExpressionSyntax expression when expression.Parent is ArgumentSyntax or EqualsValueClauseSyntax or
                        AssignmentExpressionSyntax or ReturnStatementSyntax or CastExpressionSyntax or InitializerExpressionSyntax or
                        ConditionalExpressionSyntax => ObjectConversion(model, expression),
                    _ => null
                };
                if (candidate is INamedTypeSymbol { IsGenericType: true } named &&
                    named.TypeArguments.Any(argument => argument.TypeKind == TypeKind.TypeParameter)) continue;
                if (candidate is not null && CandidateVisible(candidate, compilation.Assembly)) candidates.Add(candidate);
            }
        }
        foreach (var special in new[] { SpecialType.System_String, SpecialType.System_Boolean, SpecialType.System_Byte,
    SpecialType.System_SByte, SpecialType.System_Int16, SpecialType.System_UInt16, SpecialType.System_Int32,
    SpecialType.System_UInt32, SpecialType.System_Int64, SpecialType.System_UInt64, SpecialType.System_Single,
    SpecialType.System_Double, SpecialType.System_Decimal, SpecialType.System_Char })
            candidates.Add(compilation.GetSpecialType(special));
        foreach (string name in new[] { "System.Guid", "System.DateTime", "System.DateTimeOffset", "System.DateOnly",
    "System.TimeOnly", "System.TimeSpan", "System.Uri", "System.Version", "System.Text.Json.JsonElement",
    "System.Text.Json.JsonDocument", "System.Text.Json.Nodes.JsonNode" })
            if (compilation.GetTypeByMetadataName(name) is { } candidate) candidates.Add(candidate);
        emitter.SetDynamicCandidates(candidates.Distinct(SymbolEqualityComparer.Default).OfType<ITypeSymbol>());
        Directory.CreateDirectory(output);
        var safeContexts = new Dictionary<INamedTypeSymbol, bool>(SymbolEqualityComparer.Default);
        for (int i = 0; i < trees.Length; i++)
        {
            var rewriter = new Rewriter(compilation.GetSemanticModel(trees[i]), emitter, asyncContexts,
                symbols.Contains("NEEMLE_STATIC_NATIVE_METADATA", StringComparer.Ordinal), safeContexts);
            var path = Path.Combine(output, $"Source{i}.g.cs");
            File.WriteAllText(path, nullableDirectives + "#line 1 \"" + Path.GetFullPath(sources[i]).Replace("\\",
                "\\\\").Replace("\"", "\\\"") +
                "\"\n" + rewriter.Visit(trees[i].GetRoot())!.ToFullString());
        }
        GeneratedCodeLayout.Write(output, emitter.Generate());
        emitter.WriteAnonymousTypes(output);
        asyncContexts.Write(output);
        if (symbols.Contains("NEEMLE_STATIC_NATIVE_METADATA", StringComparer.Ordinal))
            NativeTypeMetadataEmitter.Write(output, CollectMetadataTypes(
                candidates.Concat(emitter.RegisteredTypes).Concat(asyncContexts.KnownTypes), compilation.Assembly));

        GeneratedAssemblyNames.Rewrite(output, assemblyName, new CSharpParseOptions(language, preprocessorSymbols: symbols));

    }

    static IEnumerable<ITypeSymbol> CollectMetadataTypes(IEnumerable<ITypeSymbol> candidates, IAssemblySymbol assembly)
    {
        var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var pending = new Queue<ITypeSymbol>(candidates);
        while (pending.TryDequeue(out var type))
        {
            type = type.WithNullableAnnotation(NullableAnnotation.None);
            if (!seen.Add(type) || !CandidateVisible(type, assembly)) continue;
            yield return type;
            if (type is IArrayTypeSymbol array) { pending.Enqueue(array.ElementType); continue; }
            if (type is not INamedTypeSymbol named) continue;
            foreach (var argument in named.TypeArguments) pending.Enqueue(argument);
            if (named.BaseType is not null) pending.Enqueue(named.BaseType);
            foreach (var contract in named.Interfaces) pending.Enqueue(contract);
            if (!SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, assembly)) continue;
            foreach (var member in named.GetMembers())
            {
                if (member is IPropertySymbol { IsStatic: false } property) pending.Enqueue(property.Type);
                if (member is IFieldSymbol { IsStatic: false, IsImplicitlyDeclared: false } field) pending.Enqueue(field.Type);
            }
            foreach (var attribute in named.GetAttributes())
                foreach (var argument in attribute.ConstructorArguments)
                    if (argument.Value is ITypeSymbol attributeType) pending.Enqueue(attributeType);
        }
    }

    static bool CandidateVisible(ITypeSymbol type, IAssemblySymbol assembly)
    {
        if (type is IArrayTypeSymbol array) return CandidateVisible(array.ElementType, assembly);
        if (type is not INamedTypeSymbol named || named.IsAnonymousType) return false;
        if (named.TypeArguments.Any(argument => !CandidateVisible(argument, assembly))) return false;
        for (INamedTypeSymbol? current = named; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility == Accessibility.Public) continue;
            if (current.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal &&
                SymbolEqualityComparer.Default.Equals(current.ContainingAssembly, assembly)) continue;
            return false;
        }
        return true;
    }

    static ITypeSymbol? ObjectConversion(SemanticModel model, ExpressionSyntax expression)
    {
        var info = model.GetTypeInfo(expression);
        return info.ConvertedType?.SpecialType == SpecialType.System_Object &&
            info.Type?.SpecialType != SpecialType.System_Object ? info.Type : null;
    }

}

using Microsoft.CodeAnalysis;

namespace Neemle.Json.Generator;

internal static class MetadataTypes
{
    internal static IEnumerable<ITypeSymbol> CollectMetadataTypes(IEnumerable<ITypeSymbol> candidates, IAssemblySymbol assembly)
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
            if (!SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, assembly) &&
                named.ContainingAssembly.Identity.Name.StartsWith("System", StringComparison.Ordinal)) continue;
            foreach (var member in named.GetMembers())
            {
                if (CodecEmitter.IgnoreCondition(member) == 1) continue;
                if (member is IPropertySymbol { IsStatic: false, IsIndexer: false } property &&
                    (property.DeclaredAccessibility == Accessibility.Public || Included(property))) pending.Enqueue(property.Type);
                if (member is IFieldSymbol { IsStatic: false, IsImplicitlyDeclared: false } field &&
                    (field.DeclaredAccessibility == Accessibility.Public || Included(field))) pending.Enqueue(field.Type);
            }
            foreach (var attribute in named.GetAttributes().Where(attribute =>
                         CodecEmitter.IsJsonAttribute(attribute, "JsonDerivedTypeAttribute") ||
                         CodecEmitter.IsJsonAttribute(attribute, "JsonConverterAttribute") ||
                         CodecEmitter.IsJsonAttribute(attribute, "JsonSerializableAttribute")))
            {
                foreach (var argument in attribute.ConstructorArguments)
                    EnqueueArgument(argument, pending);
                foreach (var argument in attribute.NamedArguments)
                    EnqueueArgument(argument.Value, pending);
            }
        }
    }

    private static bool Included(ISymbol member) => member.GetAttributes().Any(attribute =>
        CodecEmitter.IsJsonAttribute(attribute, "JsonIncludeAttribute"));

    private static void EnqueueArgument(TypedConstant argument, Queue<ITypeSymbol> pending)
    {
        if (argument.Kind == TypedConstantKind.Array)
        {
            if (argument.IsNull) return;
            foreach (var item in argument.Values) EnqueueArgument(item, pending);
        }
        else if (argument.Value is ITypeSymbol type) pending.Enqueue(type);
    }

    internal static bool CandidateVisible(ITypeSymbol type, IAssemblySymbol assembly)
    {
        if (type is IArrayTypeSymbol array) return CandidateVisible(array.ElementType, assembly);
        if (type is not INamedTypeSymbol named || named.IsAnonymousType || named.IsUnboundGenericType) return false;
        if (named.TypeArguments.Any(argument => !CandidateVisible(argument, assembly))) return false;
        if (named.ContainingType is not null && !CandidateVisible(named.ContainingType, assembly)) return false;
        for (INamedTypeSymbol? current = named; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility == Accessibility.Public) continue;
            if (current.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal &&
                SymbolEqualityComparer.Default.Equals(current.ContainingAssembly, assembly)) continue;
            return false;
        }
        return true;
    }

}

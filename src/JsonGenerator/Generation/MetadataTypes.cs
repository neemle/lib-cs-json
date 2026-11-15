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

    internal static bool CandidateVisible(ITypeSymbol type, IAssemblySymbol assembly)
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

}

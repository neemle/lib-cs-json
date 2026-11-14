using Microsoft.CodeAnalysis;

namespace Neemle.Json.Generator;

internal static class ContextTypeGraph
{
    internal static (ITypeSymbol Type, string Name)[] Collect(AttributeData[] roots, INamedTypeSymbol context)
    {
        var found = new List<(ITypeSymbol Type, string Name)>();
        var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var pending = new Queue<(ITypeSymbol Type, string Name)>();
        foreach (var root in roots)
            if (root.ConstructorArguments.FirstOrDefault().Value is ITypeSymbol type)
                pending.Enqueue((type, root.NamedArguments.FirstOrDefault(argument => argument.Key == "TypeInfoPropertyName")
                    .Value.Value as string ?? Name(type)));
        while (pending.TryDequeue(out var item))
        {
            if (!seen.Add(item.Type)) continue;
            found.Add(item);
            foreach (var dependency in Dependencies(item.Type, context)) pending.Enqueue((dependency, Name(dependency)));
        }
        return found.ToArray();
    }

    private static IEnumerable<ITypeSymbol> Dependencies(ITypeSymbol type, INamedTypeSymbol context)
    {
        if (type is IArrayTypeSymbol array) { yield return array.ElementType; yield break; }
        if (type is not INamedTypeSymbol named) yield break;
        foreach (var argument in named.TypeArguments) yield return argument;
        foreach (var derived in named.GetAttributes().Where(attribute => CodecEmitter.IsJsonAttribute(attribute,
            "JsonDerivedTypeAttribute")))
            if (derived.ConstructorArguments.FirstOrDefault().Value is ITypeSymbol derivedType) yield return derivedType;
        string assembly = named.ContainingAssembly.Identity.Name;
        if (named.SpecialType != SpecialType.None || assembly == "mscorlib" || assembly == "System" ||
            assembly.StartsWith("System.", StringComparison.Ordinal)) yield break;
        foreach (var member in CodecEmitter.HierarchyMembers(named))
        {
            if (CodecEmitter.IgnoreCondition(member) == 1) continue;
            if (member is IPropertySymbol { IsStatic: false, IsIndexer: false } property &&
                (property.DeclaredAccessibility == Accessibility.Public || Included(property))) yield return property.Type;
            if (member is IFieldSymbol { IsStatic: false } field && (Included(field) ||
                field.DeclaredAccessibility == Accessibility.Public && ContextOptionsEmitter.IncludesFields(context)))
                yield return field.Type;
        }
    }

    private static bool Included(ISymbol member) => member.GetAttributes().Any(attribute =>
        CodecEmitter.IsJsonAttribute(attribute, "JsonIncludeAttribute"));

    internal static string Name(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol { Rank: 1 } array => Name(array.ElementType) + "Array",
        INamedTypeSymbol { IsGenericType: true } named => named.Name + string.Concat(named.TypeArguments.Select(Name)),
        _ => type.Name
    };
}

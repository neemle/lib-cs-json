using Microsoft.CodeAnalysis;
using Neemle.CSharpComposer;
using Neemle.CSharpComposer.Builders;
using Neemle.CSharpComposer.Model;
using static CodecApiEmitter;

internal sealed class MemberAccessEmitter
{
    private readonly Dictionary<ISymbol, string> getters = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ISymbol, string> setters = new(SymbolEqualityComparer.Default);

    internal void Emit(TypeBuilder<FileBuilder> root, IEnumerable<IPropertySymbol> properties,
        IEnumerable<IFieldSymbol> fields, int index)
    {
        getters.Clear();
        setters.Clear();
        int position = 0;
        foreach (var property in properties)
        {
            if (NeedsAccessor(property, property.GetMethod))
                Add(root, property, property.Type, (property.GetMethod!.MetadataName, "get", index, position, false));
            if (CodecEmitter.CanSet(property) && NeedsAccessor(property, property.SetMethod))
                Add(root, property, property.Type, (property.SetMethod!.MetadataName, "set", index, position, false));
            position++;
        }
        foreach (var field in fields)
        {
            if (!Direct(field.DeclaredAccessibility))
                Add(root, field, field.Type, (field.MetadataName, "field", index, position, true));
            position++;
        }
    }

    internal static void EmitConstructor(TypeBuilder<FileBuilder> root, IMethodSymbol constructor, int index)
    {
        if (constructor.ContainingType.IsValueType && constructor.IsImplicitlyDeclared && constructor.Parameters.Length == 0) return;
        if (!NeedsConstructorAccessor(constructor)) return;
        var attribute = Cs.Attribute(Cs.Type("UnsafeAccessor"),
            Cs.Arg(Cs.Member(Cs.Id("UnsafeAccessorKind"), "Constructor"), null!));
        var method = root.Method(NodeType(TypeName(constructor.ContainingType)), "Access_constructor_" + index)
            .Private().Static().Extern().Attribute(attribute);
        for (int i = 0; i < constructor.Parameters.Length; i++)
            method.Parameter(NodeType(TypeName(constructor.Parameters[i].Type)), "arg" + i);
        method.End();
    }

    private void Add(TypeBuilder<FileBuilder> root, ISymbol member, ITypeSymbol type,
        (string Name, string Kind, int Index, int Position, bool Field) spec)
    {
        string method = $"Access_{spec.Kind}_{spec.Index}_{spec.Position}";
        bool setter = spec.Kind == "set";
        if (setter) setters[member] = method; else getters[member] = method;
        var attr = Cs.Attribute(Cs.Type("UnsafeAccessor"),
            Cs.Arg(Cs.Member(Cs.Id("UnsafeAccessorKind"), spec.Field ? "Field" : "Method"), null!),
            Cs.Arg(Cs.Literal(spec.Name), "Name"));
        var returnType = setter ? Cs.Void : NodeType(TypeName(type));
        if (spec.Field) returnType = returnType.Ref(false);
        var builder = root.Method(returnType, method).Private().Static().Extern()
            .Attribute(attr);
        builder.Parameter(NodeType(TypeName(member.ContainingType)), "instance",
            member.ContainingType.IsValueType ? "ref" : null);
        if (setter) builder.Parameter(NodeType(TypeName(type)), "value");
        builder.End();
    }

    internal CsNode Read(ISymbol member, string instance) => getters.TryGetValue(member, out string? method) ?
        NodeCall(method, Instance(member, instance)) : NodeMember(Cs.Symbol(instance), member.Name);

    internal CsNode Write(ISymbol member, string instance, CsNode value)
    {
        if (setters.TryGetValue(member, out string? setter)) return NodeCall(setter, Instance(member, instance), value);
        if (member is IFieldSymbol && getters.ContainsKey(member)) return NodeCall("AssignPrivateField",
            Arg(ArgumentModifier.Ref, Read(member, instance)), value);
        return NodeAssign(Read(member, instance), value);
    }

    private static CsNode Instance(ISymbol member, string name) => member.ContainingType.IsValueType ?
        Arg(ArgumentModifier.Ref, Cs.Symbol(name)) : Cs.Symbol(name);

    private static bool NeedsAccessor(IPropertySymbol property, IMethodSymbol? accessor) => accessor is not null &&
        (!Direct(property.DeclaredAccessibility) || !Direct(accessor.DeclaredAccessibility) || accessor.IsInitOnly);

    internal static bool NeedsConstructorAccessor(IMethodSymbol constructor) => !Direct(constructor.DeclaredAccessibility) ||
        CodecEmitter.HierarchyMembers(constructor.ContainingType).Any(member => member is IPropertySymbol { IsRequired: true } or
            IFieldSymbol { IsRequired: true });

    internal static bool Direct(Accessibility accessibility) => accessibility is Accessibility.Public or
        Accessibility.Internal or Accessibility.ProtectedOrInternal;
}

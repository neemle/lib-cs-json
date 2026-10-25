// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Neemle.DBAL.Tools;

/// <summary>Resolves complete closed virtual slots from selected implementation metadata.</summary>
internal sealed class NativeVirtualGetterDispatch
{
    internal sealed record Arm(Type Receiver, MethodInfo Getter, NativeConstantGetterBody? Body = null);
    private readonly Assembly core;
    private readonly Type[] types;
    private readonly Dictionary<(Guid Module, int Token, string Receiver), Arm[]?> cache = new();

    internal NativeVirtualGetterDispatch(Assembly core, IEnumerable<Assembly> selected)
    {
        this.core = core;
        types = selected.Distinct().SelectMany(assembly => assembly.GetTypes()).ToArray();
    }

    internal Arm[]? Resolve(PropertyInfo property)
    {
        var getter = property.GetMethod!;
        if (!getter.IsVirtual || getter.DeclaringType!.Assembly != core
            || property.PropertyType != core.GetType("System.String")) return null;
        var contracted = HasContract(property, getter);
        if (contracted && !OnlyFileContract(property, getter)) return null;
        if (getter.IsFinal)
            return contracted && NativeConstantGetterBody.Infer(core, getter.DeclaringType!, getter) is { } body
                ? [new Arm(getter.DeclaringType!, getter, body)] : null;
        var slot = Root(getter);
        if (slot == null) return null;
        var key = (slot.Module.ModuleVersionId, slot.MetadataToken, getter.DeclaringType.AssemblyQualifiedName!);
        if (cache.TryGetValue(key, out var known)) return known;
        var candidates = types.Where(type => getter.DeclaringType.IsAssignableFrom(type)).ToArray();
        // An unbound generic implementation can acquire closed instances that
        // metadata definitions alone cannot enumerate. Preserve the original
        // virtual call and all its diagnostics in that case.
        if (candidates.Any(type => type.ContainsGenericParameters)) return cache[key] = null;
        var arms = new List<Arm>();
        foreach (var type in candidates.Where(type => !type.IsAbstract && !type.IsInterface))
        {
            if (HasExplicitSlotMap(type, getter.Name)) return cache[key] = null;
            var matches = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.IsVirtual && Root(method) is { } root && Same(root, slot)).ToArray();
            if (matches.Length != 1) return cache[key] = null;
            var target = matches[0];
            if (target.ContainsGenericParameters) return cache[key] = null;
            var defining = target.DeclaringType!.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .SingleOrDefault(candidate => candidate.GetMethod is { } method && Same(method, target));
            if (defining == null) return cache[key] = null;
            NativeConstantGetterBody? lowered = null;
            if (HasContract(defining, target))
            {
                if (!OnlyFileContract(defining, target) || (lowered = NativeConstantGetterBody.Infer(core, type, target)) == null)
                    return cache[key] = null;
            }
            arms.Add(new Arm(type, target, lowered));
        }
        // Never disguise a call to the same annotated/intrinsic base operation
        // as inferred dispatch. Each arm must reach an actual implementation.
        if (arms.Count == 0 || arms.Any(arm => Same(arm.Getter, getter) && arm.Body == null)) return cache[key] = null;
        return cache[key] = arms.OrderBy(arm => arm.Receiver.AssemblyQualifiedName, StringComparer.Ordinal).ToArray();
    }

    private readonly Dictionary<(Guid Module, int Token, string Getter), bool> explicitMaps = new();
    private bool HasExplicitSlotMap(Type type, string getter)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var key = (current.Module.ModuleVersionId, current.MetadataToken, getter);
            if (!explicitMaps.TryGetValue(key, out var mapped))
            {
                using var stream = File.OpenRead(current.Assembly.Location);
                using var pe = new PEReader(stream);
                var metadata = pe.GetMetadataReader();
                var definition = metadata.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.Handle(current.MetadataToken));
                foreach (var handle in definition.GetMethodImplementations())
                {
                    var declaration = metadata.GetMethodImplementation(handle).MethodDeclaration;
                    var name = declaration.Kind switch
                    {
                        HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)declaration).Name,
                        HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)declaration).Name,
                        _ => default
                    };
                    if (name.IsNil || metadata.GetString(name) == getter) { mapped = true; break; }
                }
                explicitMaps[key] = mapped;
            }
            if (mapped) return true;
        }
        return false;
    }

    // MetadataLoadContext deliberately has no execution-engine vtable. Resolve
    // only ordinary zero-argument string slots whose reuse/name/signature is
    // completely represented by metadata. MethodImpl/interface/generic or
    // ambiguous shapes conservatively keep the original virtual operation.
    internal static MethodInfo? Root(MethodInfo method)
    {
        if (!method.IsVirtual) return null;
        while ((method.Attributes & MethodAttributes.NewSlot) == 0)
        {
            MethodInfo? inherited = null;
            for (var parent = method.DeclaringType!.BaseType; parent != null; parent = parent.BaseType)
            {
                var candidates = parent.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(candidate => candidate.IsVirtual && !candidate.IsPrivate && candidate.Name == method.Name
                        && candidate.ReturnType == method.ReturnType
                        && candidate.GetParameters().Select(parameter => parameter.ParameterType)
                            .SequenceEqual(method.GetParameters().Select(parameter => parameter.ParameterType))
                        && candidate.ReturnParameter.GetRequiredCustomModifiers().SequenceEqual(method.ReturnParameter.GetRequiredCustomModifiers())
                        && candidate.ReturnParameter.GetOptionalCustomModifiers().SequenceEqual(method.ReturnParameter.GetOptionalCustomModifiers())).ToArray();
                if (candidates.Length > 1) return null;
                if (candidates.Length == 1) { inherited = candidates[0]; break; }
            }
            if (inherited == null) return null;
            method = inherited;
        }
        return method;
    }

    private bool HasContract(PropertyInfo property, MethodInfo getter)
        => property.GetCustomAttributesData().Concat(getter.GetCustomAttributesData())
            .Concat(getter.DeclaringType!.GetCustomAttributesData())
            .Concat(getter.ReturnParameter.GetCustomAttributesData()).Any(attribute => attribute.AttributeType.Assembly == core
                && attribute.AttributeType.FullName is "System.Diagnostics.CodeAnalysis.RequiresAssemblyFilesAttribute"
                    or "System.Diagnostics.CodeAnalysis.RequiresUnreferencedCodeAttribute"
                    or "System.Diagnostics.CodeAnalysis.RequiresDynamicCodeAttribute"
                    or "System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembersAttribute");

    private bool OnlyFileContract(PropertyInfo property, MethodInfo getter)
        => !property.GetCustomAttributesData().Concat(getter.GetCustomAttributesData())
            .Concat(getter.DeclaringType!.GetCustomAttributesData()).Concat(getter.ReturnParameter.GetCustomAttributesData())
            .Any(attribute => attribute.AttributeType.Assembly == core && attribute.AttributeType.FullName is
                "System.Diagnostics.CodeAnalysis.RequiresUnreferencedCodeAttribute" or "System.Diagnostics.CodeAnalysis.RequiresDynamicCodeAttribute"
                or "System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembersAttribute");

    private static bool Same(MethodInfo left, MethodInfo right)
        => left.Module.ModuleVersionId == right.Module.ModuleVersionId && left.MetadataToken == right.MetadataToken;
}

// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Neemle.DBAL.Tools;

/// <summary>Plans the selected CLR's attribute inheritance without constructing application attributes.</summary>
internal sealed class NativeAttributeInheritance(MetadataLoadContext context) : IDisposable
{
    internal sealed record Construction(CustomAttributeData Data, bool Include);
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private readonly Dictionary<Type, (bool Inherited, bool Multiple)> usages = new();
    private readonly Dictionary<Assembly, PEReader> images = new();
    private readonly Dictionary<MethodInfo, MethodInfo?> parentMethods = new();

    public void Dispose()
    {
        foreach (var image in images.Values) image.Dispose();
    }

    internal IReadOnlyList<Construction> Plan(object element, Type requested, bool inherit)
    {
        if (element is Type type)
        {
            if (type.HasElementType) return [];
            element = type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;
        }
        if (element is MethodInfo { IsConstructedGenericMethod: true } method)
            element = method.GetGenericMethodDefinition();

        var own = Attributes(element).Where(data => requested.IsAssignableFrom(data.AttributeType)).ToArray();
        var result = own.Select(data => new Construction(data, true)).ToList();
        if (!inherit || element is Assembly or Module or ConstructorInfo or FieldInfo) return result;
        var constructBeforeFiltering = element is PropertyInfo or EventInfo or ParameterInfo;
        if (!constructBeforeFiltering && requested.IsSealed && !Usage(requested).Inherited) return result;
        var seen = own.Select(data => data.AttributeType).ToHashSet();
        for (var parent = Parent(element); parent != null; parent = Parent(parent))
        {
            // RuntimeType's inheritance walk excludes Object, except when it is the original receiver.
            if (element is Type && parent is Type parentType && parentType == context.CoreAssembly!.GetType("System.Object")) break;
            // RuntimeType snapshots the previously derived levels before examining a
            // declaration. Duplicate blobs on this same level do not hide each other.
            var derived = constructBeforeFiltering ? seen : seen.ToHashSet();
            foreach (var data in Attributes(parent).Where(data => requested.IsAssignableFrom(data.AttributeType)))
            {
                var usage = Usage(data.AttributeType);
                var include = usage.Inherited && (usage.Multiple || !derived.Contains(data.AttributeType));
                if (include || constructBeforeFiltering) result.Add(new Construction(data, include));
                // Property/event and parameter APIs construct matching base attributes even when
                // AttributeUsage later discards them. Preserve their constructor/error side effects.
                if (constructBeforeFiltering || include) seen.Add(data.AttributeType);
            }
        }
        return result;
    }

    private static IList<CustomAttributeData> Attributes(object element)
        => element switch
        {
            MemberInfo member => member.GetCustomAttributesData(),
            ParameterInfo parameter => parameter.GetCustomAttributesData(),
            Assembly assembly => assembly.GetCustomAttributesData(),
            Module module => module.GetCustomAttributesData(),
            _ => throw new NotSupportedException("No native attribute owner contract was inferred for " + element)
        };

    private (bool Inherited, bool Multiple) Usage(Type type)
    {
        if (usages.TryGetValue(type, out var result)) return result;
        var usageType = context.CoreAssembly!.GetType("System.AttributeUsageAttribute", true)!;
        var attributes = type.GetCustomAttributesData().Where(data => data.AttributeType == usageType).ToArray();
        if (attributes.Length > 1) throw new FormatException("Multiple AttributeUsage declarations on " + type);
        var usage = attributes.SingleOrDefault();
        return usages[type] = (usage == null || !usage.NamedArguments.Any(value => value.MemberName == "Inherited" && value.TypedValue.Value is false),
            usage != null && usage.NamedArguments.Any(value => value.MemberName == "AllowMultiple" && value.TypedValue.Value is true));
    }

    private object? Parent(object element)
    {
        if (element is Type type) return type.BaseType;
        if (element is MethodInfo method) return ParentMethod(method);
        if (element is PropertyInfo property)
        {
            var accessor = property.GetMethod ?? property.SetMethod;
            var parent = accessor == null ? null : ParentMethod(accessor);
            return parent?.DeclaringType!.GetProperty(property.Name, Declared & ~BindingFlags.Static,
                null, property.PropertyType, property.GetIndexParameters().Select(parameter => parameter.ParameterType).ToArray(), null);
        }
        if (element is EventInfo @event)
        {
            var parent = @event.AddMethod == null ? null : ParentMethod(@event.AddMethod);
            return parent?.DeclaringType!.GetEvent(@event.Name, Declared & ~BindingFlags.Static);
        }
        if (element is ParameterInfo parameter && parameter.Member is MethodInfo parameterMethod)
        {
            var parent = ParentMethod(parameterMethod);
            return parent == null ? null : parameter.Position == -1 ? parent.ReturnParameter : parent.GetParameters()[parameter.Position];
        }
        return null;
    }

    private MethodInfo? ParentMethod(MethodInfo method)
    {
        if (parentMethods.TryGetValue(method, out var cached)) return cached;
        var parent = FindParentMethod(method);
        parentMethods.Add(method, parent);
        return parent;
    }

    private MethodInfo? FindParentMethod(MethodInfo method)
    {
        if (!method.IsVirtual || method.IsStatic || method.DeclaringType is not { IsInterface: false } declaring) return null;
        // CoreCLR walks the physical vtable slot, not MethodImpl dispatch aliases.
        // Covariant C# overrides introduce a new slot and therefore have no parent
        // attribute declaration even though their MethodImpl dispatches a base slot.
        if ((method.Attributes & MethodAttributes.NewSlot) != 0) return null;
        if (!images.TryGetValue(declaring.Assembly, out var image))
            images.Add(declaring.Assembly, image = new PEReader(File.OpenRead(declaring.Assembly.Location)));
        var reader = image.GetMetadataReader();
        var definition = reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(declaring.MetadataToken & 0x00ffffff));
        foreach (var handle in definition.GetMethodImplementations())
        {
            var implementation = reader.GetMethodImplementation(handle);
            if (MetadataTokens.GetToken(implementation.MethodBody) != method.MetadataToken) continue;
            var parent = ResolveDeclaration(implementation.MethodDeclaration, declaring, reader);
            if (parent?.DeclaringType?.IsInterface == false) return parent;
        }
        for (var current = declaring.BaseType; current != null; current = current.BaseType)
        {
            var candidates = current.GetMethods(Declared).Where(candidate => candidate.IsVirtual && !candidate.IsPrivate
                && candidate.Name == method.Name && candidate.GetGenericArguments().Length == method.GetGenericArguments().Length
                && SameParameters(candidate, method)).ToArray();
            if (candidates.Length == 1) return candidates[0];
            if (candidates.Length > 1) throw new NotSupportedException("Ambiguous native attribute inheritance slot: " + method);
        }
        return null;
    }

    private MethodInfo? ResolveDeclaration(EntityHandle handle, Type declaring, MetadataReader reader)
    {
        if (handle.Kind == HandleKind.MethodDefinition)
        {
            var token = MetadataTokens.GetToken(handle);
            for (var current = declaring.BaseType; current != null; current = current.BaseType)
                if (current.Assembly == declaring.Assembly)
                {
                    var found = current.GetMethods(Declared).SingleOrDefault(method => method.MetadataToken == token);
                    if (found != null) return found;
                }
            return null;
        }
        if (handle.Kind != HandleKind.MemberReference) throw new NotSupportedException("Unsupported native attribute MethodImpl declaration: " + handle.Kind);
        var reference = reader.GetMemberReference((MemberReferenceHandle)handle);
        var signature = new NativeParameterMetadata.SignatureTypes(context, declaring.Assembly);
        var arguments = new NativeParameterMetadata.GenericContext(declaring.GetGenericArguments().Cast<Type?>().ToImmutableArray(), []);
        var owner = reference.Parent.Kind switch
        {
            HandleKind.TypeDefinition => signature.GetTypeFromDefinition(reader, (TypeDefinitionHandle)reference.Parent, 0),
            HandleKind.TypeReference => signature.GetTypeFromReference(reader, (TypeReferenceHandle)reference.Parent, 0),
            HandleKind.TypeSpecification => signature.GetTypeFromSpecification(reader, arguments, (TypeSpecificationHandle)reference.Parent, 0),
            _ => null
        };
        if (owner == null || owner.IsInterface) return null;
        var expected = reference.DecodeMethodSignature(signature, arguments);
        return owner.GetMethods(Declared).SingleOrDefault(method => method.Name == reader.GetString(reference.Name)
            && SameType(method.ReturnType, expected.ReturnType!)
            && SameTypes(method.GetParameters().Select(parameter => parameter.ParameterType), expected.ParameterTypes.Select(type => type!)));
    }

    private static bool SameParameters(MethodInfo left, MethodInfo right)
    {
        var a = left.GetParameters();
        var b = right.GetParameters();
        return a.Length == b.Length && a.Zip(b).All(pair => SameType(pair.First.ParameterType, pair.Second.ParameterType)
            && SameTypes(pair.First.GetRequiredCustomModifiers(), pair.Second.GetRequiredCustomModifiers())
            && SameTypes(pair.First.GetOptionalCustomModifiers(), pair.Second.GetOptionalCustomModifiers()));
    }

    private static bool SameTypes(IEnumerable<Type> left, IEnumerable<Type> right)
    {
        var a = left.ToArray();
        var b = right.ToArray();
        return a.Length == b.Length && a.Zip(b).All(pair => SameType(pair.First, pair.Second));
    }

    private static bool SameType(Type left, Type right)
    {
        if (left == right) return true;
        if (left.IsGenericParameter || right.IsGenericParameter)
            return left.IsGenericParameter && right.IsGenericParameter && left.GenericParameterPosition == right.GenericParameterPosition
                && (left.DeclaringMethod == null) == (right.DeclaringMethod == null);
        if (left.HasElementType || right.HasElementType)
            return left.HasElementType && right.HasElementType && left.IsArray == right.IsArray && left.IsByRef == right.IsByRef
                && left.IsPointer == right.IsPointer && (!left.IsArray || left.GetArrayRank() == right.GetArrayRank())
                && SameType(left.GetElementType()!, right.GetElementType()!);
        return left.IsConstructedGenericType && right.IsConstructedGenericType
            && left.GetGenericTypeDefinition() == right.GetGenericTypeDefinition() && SameTypes(left.GenericTypeArguments, right.GenericTypeArguments);
    }
}

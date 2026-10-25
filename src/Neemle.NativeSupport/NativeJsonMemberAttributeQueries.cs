// Copyright (c) Neemle contributors. Licensed under the MIT License.
#nullable enable
using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Neemle.DBAL.Runtime;

// Keep JSON's raw virtual and static Attribute APIs distinct. The generated
// endpoint owns only advertised serialization contracts, not reflection roots.
internal static class NativeJsonMemberAttributeQueries
{
    internal static object[] ReadFiltered<TAttribute>(MemberInfo element, bool inherit)
        where TAttribute : Attribute
        => RuntimeFeature.IsDynamicCodeSupported
            ? element.GetCustomAttributes(typeof(TAttribute), inherit)
            : ReadJsonAttributes(null, element, typeof(TAttribute), inherit, false, false);

    internal static TAttribute? ReadSingle<TAttribute>(MemberInfo element, bool inherit)
        where TAttribute : Attribute
        => RuntimeFeature.IsDynamicCodeSupported
            ? element.GetCustomAttribute<TAttribute>(inherit)
            : First<TAttribute>(ReadJsonAttributes(null, element, typeof(TAttribute), inherit, true, true));

    internal static object[] ReadAll(MemberInfo element, bool inherit)
        => RuntimeFeature.IsDynamicCodeSupported
            ? element.GetCustomAttributes(inherit)
            : ReadJsonAttributes(null, element, typeof(Attribute), inherit, false, false);

    internal static object[] ReadRuntimeFiltered(Type element, Type attributeType, bool inherit)
        => RuntimeFeature.IsDynamicCodeSupported
            ? element.GetCustomAttributes(attributeType, inherit)
            : ReadJsonAttributes(null, element, attributeType, inherit, false, false);

    private static TAttribute? First<TAttribute>(object[] attributes)
        where TAttribute : Attribute
        => attributes.Length == 0 ? null : (TAttribute)attributes[0];

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "ReadJsonAttributes")]
    private static extern object[] ReadJsonAttributes(
        [UnsafeAccessorType("Neemle.DBAL.Inferred.PropertyAccessors,Neemle.DBAL.TypeMetadataProbe")] object? target,
        object element,
        Type attributeType,
        bool inherit,
        bool single,
        bool staticSemantics);
}

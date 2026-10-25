// Copyright (c) Neemle contributors. Licensed under the MIT License.
#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Neemle.DBAL.Runtime;

/// <summary>Shares inferred attribute construction without depending on EF or a provider.</summary>
internal static class NativeAttributeQueries
{
    internal static TAttribute? ReadAttribute<TAttribute>(this Assembly element)
        where TAttribute : Attribute
        => RuntimeFeature.IsDynamicCodeSupported
            ? element.GetCustomAttribute<TAttribute>()
            : Single<TAttribute>(ReadAttributes(null, element, typeof(TAttribute), false, true));

    internal static IEnumerable<TAttribute> ReadAttributes<TAttribute>(this Assembly element)
        where TAttribute : Attribute
        => RuntimeFeature.IsDynamicCodeSupported
            ? element.GetCustomAttributes<TAttribute>()
            : (IEnumerable<TAttribute>)ReadAttributes(null, element, typeof(TAttribute), false, false);

    internal static TAttribute? ReadAttribute<TAttribute>(this FieldInfo element, bool inherit)
        where TAttribute : Attribute
        => RuntimeFeature.IsDynamicCodeSupported
            ? element.GetCustomAttribute<TAttribute>(inherit)
            : Single<TAttribute>(ReadAttributes(null, element, typeof(TAttribute), inherit, true));

    internal static IEnumerable<TAttribute> ReadAttributes<TAttribute>(this FieldInfo element, bool inherit)
        where TAttribute : Attribute
        => RuntimeFeature.IsDynamicCodeSupported
            ? element.GetCustomAttributes<TAttribute>(inherit)
            : (IEnumerable<TAttribute>)ReadAttributes(null, element, typeof(TAttribute), inherit, false);

    internal static IEnumerable<TAttribute> ReadAttributes<TAttribute>(this Type element, bool inherit)
        where TAttribute : Attribute
        => RuntimeFeature.IsDynamicCodeSupported
            ? element.GetCustomAttributes<TAttribute>(inherit)
            : (IEnumerable<TAttribute>)ReadAttributes(null, element, typeof(TAttribute), inherit, false);

    internal static TAttribute? ReadAttribute<TAttribute>(this Type element, bool inherit)
        where TAttribute : Attribute
        => RuntimeFeature.IsDynamicCodeSupported
            ? element.GetCustomAttribute<TAttribute>(inherit)
            : Single<TAttribute>(ReadAttributes(null, element, typeof(TAttribute), inherit, true));

    internal static IEnumerable<TAttribute> ReadAttributes<TAttribute>(this MethodInfo element, bool inherit)
        where TAttribute : Attribute
        => RuntimeFeature.IsDynamicCodeSupported
            ? element.GetCustomAttributes<TAttribute>(inherit)
            : (IEnumerable<TAttribute>)ReadAttributes(null, element, typeof(TAttribute), inherit, false);

    internal static TAttribute? ReadAttribute<TAttribute>(this MethodInfo element, bool inherit)
        where TAttribute : Attribute
        => RuntimeFeature.IsDynamicCodeSupported
            ? element.GetCustomAttribute<TAttribute>(inherit)
            : Single<TAttribute>(ReadAttributes(null, element, typeof(TAttribute), inherit, true));

    // A framework's runtime-derived receiver is a semantic base contract, not a
    // list of application types. The generator closes it against selected inputs.
    internal static TAttribute? ReadAttributeForReceiver<TAttribute, TReceiver>(this Type element, bool inherit)
        where TAttribute : Attribute where TReceiver : class
        => RuntimeFeature.IsDynamicCodeSupported
            ? element.GetCustomAttribute<TAttribute>(inherit)
            : Single<TAttribute>(ReadAttributes(null, element, typeof(TAttribute), inherit, true));

    internal static TAttribute? ReadAttributeForReceiver<TAttribute, TReceiver>(this MethodInfo element, bool inherit)
        where TAttribute : Attribute where TReceiver : class
        => RuntimeFeature.IsDynamicCodeSupported
            ? element.GetCustomAttribute<TAttribute>(inherit)
            : Single<TAttribute>(ReadAttributes(null, element, typeof(TAttribute), inherit, true));

    internal static bool IsDefinedForReceiver<TAttribute, TReceiver>(this MethodInfo element, bool inherit)
        where TAttribute : Attribute where TReceiver : class
        => RuntimeFeature.IsDynamicCodeSupported
            ? element.IsDefined(typeof(TAttribute), inherit)
            : IsDefined(null, element, typeof(TAttribute), inherit);

    private static TAttribute? Single<TAttribute>(Attribute[] attributes)
        where TAttribute : Attribute
        => attributes.Length == 0 ? null : (TAttribute)attributes[0];

    // PropertyInfo's raw virtual API ignores inheritance. Keep its object-array
    // contract for framework callers that select the first item themselves.
    internal static object[] ReadAttributes<TAttribute>(this PropertyInfo element, bool inherit)
        where TAttribute : Attribute
        => RuntimeFeature.IsDynamicCodeSupported
            ? element.GetCustomAttributes(typeof(TAttribute), inherit)
            : ReadAttributes(null, element, typeof(TAttribute), false, false);

    internal static bool IsDefined<TAttribute>(this Type element, bool inherit)
        where TAttribute : Attribute
        => RuntimeFeature.IsDynamicCodeSupported
            ? element.IsDefined(typeof(TAttribute), inherit)
            : IsDefined(null, element, typeof(TAttribute), inherit);

    internal static bool IsDefined<TAttribute>(this MethodInfo element, bool inherit)
        where TAttribute : Attribute
        => RuntimeFeature.IsDynamicCodeSupported
            ? element.IsDefined(typeof(TAttribute), inherit)
            : IsDefined(null, element, typeof(TAttribute), inherit);

    internal static bool IsDefined<TAttribute>(this PropertyInfo element, bool inherit)
        where TAttribute : Attribute
        => RuntimeFeature.IsDynamicCodeSupported
            ? element.IsDefined(typeof(TAttribute), inherit)
            : IsDefined(null, element, typeof(TAttribute), false);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "IsDefined")]
    private static extern bool IsDefined(
        [UnsafeAccessorType("Neemle.DBAL.Inferred.PropertyAccessors,Neemle.DBAL.TypeMetadataProbe")] object? target,
        object element,
        Type attributeType,
        bool inherit);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "ReadAttributes")]
    private static extern Attribute[] ReadAttributes(
        [UnsafeAccessorType("Neemle.DBAL.Inferred.PropertyAccessors,Neemle.DBAL.TypeMetadataProbe")] object? target,
        object element,
        Type attributeType,
        bool inherit,
        bool single);
}

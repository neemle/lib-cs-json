// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Neemle.DBAL.Tools;

internal static class NativeMemberInterfaceMetadata
{
    internal static IReadOnlyList<string> Append(MetadataLoadContext context, IEnumerable<Type> inferred, StringBuilder source)
    {
        if (!context.GetAssemblies().Any(assembly => assembly.GetType("Neemle.DBAL.EntityFrameworkCore.NativeAotSequenceTypes") != null))
            return Array.Empty<string>();
        var roots = new SortedSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<Type>();
        foreach (var type in inferred)
        {
            Add(type);
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                Add(field.FieldType);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                Add(property.PropertyType);
        }
        foreach (var root in roots)
            source.Append("global::Neemle.DBAL.EntityFrameworkCore.NativeAotSequenceTypes.RegisterType(global::System.Type.GetType(")
                .Append(JsonSerializer.Serialize(root)).Append(", throwOnError: true)!);\n");
        return roots.ToArray();

        void Add(Type type)
        {
            if (!visited.Add(type) || type.IsGenericParameter || type.IsByRefLike || type.IsByRef) return;
            if (type.AssemblyQualifiedName is { } name) roots.Add(name);
            if (type.HasElementType) Add(type.GetElementType()!);
            if (type.IsConstructedGenericType) Add(type.GetGenericTypeDefinition());
            foreach (var argument in type.GetGenericArguments()) Add(argument);
            foreach (var contract in type.GetInterfaces()) Add(contract);
            if (type.BaseType is { } parent) Add(parent);
        }
    }
}

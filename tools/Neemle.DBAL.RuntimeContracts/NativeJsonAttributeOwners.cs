// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Reflection;

namespace Neemle.DBAL.Tools;

/// <summary>Reads the serialized Type domain from canonical source-generated JSON contracts.</summary>
internal static class NativeJsonAttributeOwners
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    internal static IReadOnlyList<Type> Infer(Assembly json, IEnumerable<Assembly> inputs, IEnumerable<Type> inferred)
    {
        var context = json.GetType("System.Text.Json.Serialization.JsonSerializerContext");
        var serializable = json.GetType("System.Text.Json.Serialization.JsonSerializableAttribute");
        var info = json.GetType("System.Text.Json.Serialization.Metadata.JsonTypeInfo`1");
        if (context == null || serializable == null || info == null) return [];
        var owners = new HashSet<Type>();
        var contexts = inputs.SelectMany(assembly => assembly.GetTypes()).Concat(inferred)
            .Where(type => !type.HasElementType && !type.IsGenericParameter && context.IsAssignableFrom(type)).Distinct();
        foreach (var candidate in contexts)
            for (var declaration = candidate; declaration != null && declaration != context; declaration = declaration.BaseType)
            {
                foreach (var attribute in declaration.GetCustomAttributesData())
                    if (attribute.AttributeType == serializable && attribute.ConstructorArguments.Count != 0
                        && attribute.ConstructorArguments[0].Value is Type serialized) Add(serialized);
                foreach (var field in declaration.GetFields(Declared)) Signature(field.FieldType);
                foreach (var property in declaration.GetProperties(Declared)) Signature(property.PropertyType);
                foreach (var method in declaration.GetMethods(Declared))
                {
                    Signature(method.ReturnType);
                    foreach (var parameter in method.GetParameters()) Signature(parameter.ParameterType);
                }
            }
        return owners.OrderBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal).ToArray();

        void Signature(Type type)
        {
            if (type.HasElementType) Signature(type.GetElementType()!);
            else if (type.IsConstructedGenericType)
            {
                if (type.GetGenericTypeDefinition() == info) Add(type.GetGenericArguments()[0]);
                else foreach (var argument in type.GetGenericArguments()) Signature(argument);
            }
        }

        void Add(Type type)
        {
            // A CLR argument need not participate in serialization (for example
            // Envelope<Unused> with only a string property). Nested JSON values
            // have their own canonical generated JsonTypeInfo<T> contracts.
            if (!type.ContainsGenericParameters && !type.IsByRef && !type.IsPointer && !type.IsFunctionPointer)
                owners.Add(type);
        }
    }
}

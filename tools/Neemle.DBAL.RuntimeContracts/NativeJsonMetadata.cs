// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Text;
using System.Text.Json;
using Neemle.DBAL.Generators;

namespace Neemle.DBAL.Tools;

/// <summary>Prepares the official JSON generator's inputs from inferred mapping CLR types.</summary>
internal static class NativeJsonMetadata
{
    internal sealed record Inputs(string[] Sources, string[] References);

    internal static Inputs Prepare(string[] references, string directory)
    {
        using var context = new MetadataLoadContext(new SelectedCompilerAssemblyResolver(references), "System.Private.CoreLib");
        var nullable = context.CoreAssembly!.GetType("System.Nullable`1", true)!;
        bool IsNullable(Type type) => type.IsConstructedGenericType && type.GetGenericTypeDefinition() == nullable;
        var roots = new SortedDictionary<string, Type>(StringComparer.Ordinal);
        var postgresRoots = new SortedDictionary<string, Type>(StringComparer.Ordinal);
        var hasEf = references.Any(path => context.LoadFromAssemblyPath(path)
            .GetType("Neemle.DBAL.EntityFrameworkCore.NativeAotJsonSerializer") != null);
        var assemblyMetadata = context.CoreAssembly!.GetType("System.Reflection.AssemblyMetadataAttribute", true)!;
        var hasPostgres = references.Any(path => context.LoadFromAssemblyPath(path)
            .GetType("Neemle.DBAL.PostgreSQL.NativeJsonConverters") != null)
            && references.Any(path => context.LoadFromAssemblyPath(path).GetCustomAttributesData().Any(attribute =>
                attribute.AttributeType == assemblyMetadata
                && attribute.ConstructorArguments[0].Value as string == NativeMetadataNames.PostgresJsonEnabledKey
                && attribute.ConstructorArguments[1].Value as string == "true"));
        foreach (var path in references)
        {
            var assembly = context.LoadFromAssemblyPath(path);
            foreach (var attribute in assembly.GetCustomAttributesData().Where(attribute => attribute.AttributeType == assemblyMetadata
                && (attribute.ConstructorArguments[0].Value as string == NativeMetadataNames.JsonBindingsKey
                    || hasPostgres && attribute.ConstructorArguments[0].Value as string == NativeMetadataNames.PostgresJsonBindingsKey)))
                foreach (var name in Encoding.UTF8.GetString(Convert.FromBase64String((string)attribute.ConstructorArguments[1].Value!))
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var type = Type.GetType(name, context.LoadFromAssemblyName,
                        static (owner, name, ignoreCase) => owner?.GetType(name, true, ignoreCase), true)!;
                    roots.TryAdd(type.AssemblyQualifiedName!, type);
                    if (attribute.ConstructorArguments[0].Value as string == NativeMetadataNames.PostgresJsonBindingsKey)
                        postgresRoots.TryAdd(type.AssemblyQualifiedName!, type);
                }
        }
        if (hasPostgres)
        {
            var objectType = context.CoreAssembly.GetType("System.Object", true)!;
            roots.TryAdd(objectType.AssemblyQualifiedName!, objectType);
        }
        if (roots.Count == 0 && !hasEf) return new([], references);

        // Serialize(object) selects the actual runtime type, including ordinary
        // subclasses of a mapped POCO. Infer these from the selected assemblies;
        // a user must not add another mapping merely to retain a derived value.
        var polymorphic = roots.Values.Where(type => type.IsClass && !type.IsSealed
            && type != context.CoreAssembly.GetType("System.Object", true)).ToArray();
        if (polymorphic.Length != 0)
            foreach (var path in references)
                foreach (var candidate in context.LoadFromAssemblyPath(path).GetTypes())
                    if (!candidate.ContainsGenericParameters && !candidate.IsAbstract
                        && polymorphic.Any(candidate.IsSubclassOf))
                    {
                        roots.TryAdd(candidate.AssemblyQualifiedName!, candidate);
                        if (postgresRoots.Values.ToArray().Any(candidate.IsSubclassOf))
                            postgresRoots.TryAdd(candidate.AssemblyQualifiedName!, candidate);
                    }

        // Project visibility only for the actual JSON graph. The projection never reaches ILC.
        // The official generator still sees the original member access and JSON attributes.
        var graph = new HashSet<Type>();
        foreach (var type in roots.Values) Visit(type);
        void Visit(Type type)
        {
            if (type.ContainsGenericParameters || !graph.Add(type)) return;
            if (type.HasElementType) Visit(type.GetElementType()!);
            if (type.IsConstructedGenericType)
                foreach (var argument in type.GetGenericArguments()) Visit(argument);
            if (type.DeclaringType is { } declaring) Visit(declaring);
            if (type.BaseType is { } parent) Visit(parent);
            if (type.Assembly == context.CoreAssembly || type.Namespace == "System"
                || type.Namespace?.StartsWith("System.", StringComparison.Ordinal) == true) return;
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) Visit(property.PropertyType);
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance)) Visit(field.FieldType);
            foreach (var attribute in type.GetCustomAttributesData())
                if (attribute.AttributeType.FullName == "System.Text.Json.Serialization.JsonDerivedTypeAttribute"
                    && attribute.ConstructorArguments[0].Value is Type derived) Visit(derived);
        }

        var owners = graph.Where(type => !type.HasElementType
                && !(type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type).IsVisible)
            .GroupBy(type => type.Assembly.GetName().Name!).ToDictionary(group => group.Key,
                group => group.Select(type => (type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type).FullName!)
                    .ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var projected = references.Select(path =>
        {
            var name = AssemblyName.GetAssemblyName(path).Name!;
            if (!owners.TryGetValue(name, out var types)) return path;
            var output = Path.Combine(directory, "json-references", name + ".dll");
            NativeModelBridge.Project(path, output, types);
            return output;
        }).ToArray();
        var source = new StringBuilder("// <auto-generated />\n#nullable enable\n");
        foreach (var owner in owners.Keys.Order(StringComparer.Ordinal))
            source.Append("[assembly: global::System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute(")
                .Append(JsonSerializer.Serialize(owner)).Append(")]\n");
        if (owners.Count != 0)
            source.Append("namespace System.Runtime.CompilerServices { [global::System.AttributeUsage(global::System.AttributeTargets.Assembly, AllowMultiple=true)] internal sealed class IgnoresAccessChecksToAttribute : global::System.Attribute { public IgnoresAccessChecksToAttribute(string name) { AssemblyName=name; } public string AssemblyName {get;} } }\n");
        source.Append("namespace Neemle.DBAL.Inferred {\n");
        if (roots.Count != 0)
        {
            source.Append("[global::System.Text.Json.Serialization.JsonSourceGenerationOptions(GenerationMode=global::System.Text.Json.Serialization.JsonSourceGenerationMode.Metadata)]\n");
            var jsonAssembly = context.LoadFromAssemblyName("System.Text.Json");
            var converter = jsonAssembly.GetType("System.Text.Json.Serialization.JsonConverter`1", true)!;
            var builtins = jsonAssembly.GetType("System.Text.Json.Serialization.Metadata.JsonMetadataServices", true)!
                .GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Select(property => property.PropertyType)
                .Where(type => type.IsConstructedGenericType && type.GetGenericTypeDefinition() == converter)
                .Select(type => type.GetGenericArguments()[0]).ToHashSet();
            var index = 0;
            foreach (var root in roots.Values)
            {
                source.Append("[global::System.Text.Json.Serialization.JsonSerializable(typeof(").Append(CSharpName(root)).Append(")");
                // The official generator also uses built-in metadata names to bind
                // JsonMetadataServices converters. Preserve those framework names.
                if (!builtins.Contains(root)) source.Append(", TypeInfoPropertyName=\"Root").Append(index++).Append("\"");
                source.Append(")]\n");
            }
            source.Append("internal partial class JsonContext : global::System.Text.Json.Serialization.JsonSerializerContext {}\n");
        }
        if (hasEf)
        {
            source.Append("public sealed class JsonRoots : global::Neemle.DBAL.EntityFrameworkCore.INativeAotExtensionRoot { public JsonRoots() {} public void Register() {\n");
            foreach (var root in roots.Values)
                source.Append("global::Neemle.DBAL.EntityFrameworkCore.NativeAotJsonSerializer.Register<").Append(CSharpName(root))
                    .Append(">(static () => (global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<").Append(CSharpName(root))
                    .Append(">)JsonContext.Default.GetTypeInfo(typeof(").Append(CSharpName(root)).Append("))!);\n");
            source.Append("} }\n");
        }
        if (hasPostgres)
        {
            source.Append("public static class PostgresJsonRoots { public static void Register() {\n");
            foreach (var root in postgresRoots.Values)
            {
                if (IsNullable(root)) continue;
                source.Append("global::Neemle.DBAL.PostgreSQL.NativeJsonConverters.Register")
                    .Append(root.IsValueType ? "Struct" : "Class").Append('<').Append(CSharpName(root))
                    .Append(">(static () => JsonContext.Default);\n");
                foreach (var baseType in postgresRoots.Values.Where(type => type != root && !IsNullable(type) && type.IsAssignableFrom(root)))
                    source.Append("global::Neemle.DBAL.PostgreSQL.NativeJsonConverters.RegisterBase<").Append(CSharpName(root))
                        .Append(',').Append(CSharpName(baseType)).Append(">();\n");
            }
            source.Append("} }\n");
        }
        source.Append("}\n");
        var outputSource = Path.Combine(directory, "json-roots.g.cs");
        Write(outputSource, source.ToString());
        Write(outputSource + ".json", JsonSerializer.Serialize(new { roots = roots.Keys, postgresRoots = postgresRoots.Keys, projectedTypes = owners }, new JsonSerializerOptions { WriteIndented = true }));
        return new([outputSource], projected);
    }

    internal static string CSharpName(Type type)
    {
        if (type.IsArray) return CSharpName(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        var owners = new Stack<Type>();
        for (var current = type; current != null; current = current.DeclaringType) owners.Push(current);
        var arguments = type.IsGenericType ? type.GetGenericArguments() : [];
        var index = 0;
        var result = "global::";
        while (owners.TryPop(out var owner))
        {
            if (owner.DeclaringType == null && owner.Namespace is { Length: > 0 } ns)
                result += string.Join(".", ns.Split('.').Select(part => "@" + part)) + ".";
            var parts = owner.Name.Split('`');
            result += "@" + parts[0];
            if (parts.Length == 2)
            {
                var count = int.Parse(parts[1]);
                result += "<" + string.Join(",", arguments.Skip(index).Take(count).Select(CSharpName)) + ">";
                index += count;
            }
            if (owners.Count != 0) result += ".";
        }
        return result;
    }

    private static void Write(string path, string value)
    {
        if (!File.Exists(path) || File.ReadAllText(path) != value) File.WriteAllText(path, value, new UTF8Encoding(false));
    }
}

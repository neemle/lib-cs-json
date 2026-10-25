// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Neemle.DBAL.Tools;

/// <summary>Infers metadata module closure from payloads/signatures, never assembly-name catalogs.</summary>
internal static class NativeDiagnosticMetadataUniverse
{
    internal static void Generate(string manifest, string application, string output)
    {
        var paths = File.ReadAllLines(manifest).Append(application).Where(File.Exists)
            .Select(Path.GetFullPath).Distinct().ToArray();
        using var context = new MetadataLoadContext(new SelectedCompilerAssemblyResolver(paths), "System.Private.CoreLib");
        var selected = paths.Select(context.LoadFromAssemblyPath).ToArray();
        var payloads = NativeDiagnosticPayloadMetadata.Resolve(context, selected);
        var pending = new Queue<Assembly>();
        var modules = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var rows = new List<object>();
        var shapes = new HashSet<string>(StringComparer.Ordinal);
        Add(context.CoreAssembly!, "native core");
        Add(context.LoadFromAssemblyPath(Path.GetFullPath(application)), "consumer");
        foreach (var payload in payloads) Add(payload.Assembly, "inferred payload");
        foreach (var assembly in selected)
            if (assembly.GetType("Neemle.DBAL.Runtime.NativeDiagnosticPayloads", false) is { } registry)
                Visit(registry.GetMethod("CurrentActivity")!.ReturnType, "activity contract");
        const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var count = 0;
        while (pending.TryDequeue(out var assembly))
        {
            var types = assembly.GetTypes();
            count += types.Length;
            if (count > 200_000 || modules.Count > 1024)
                throw new InvalidOperationException("The inferred metadata signature closure exceeds the explicit build limit; no module was omitted.");
            var origin = assembly.FullName!;
            Attributes(assembly.GetCustomAttributesData(), origin);
            foreach (var type in types)
            {
                Visit(type.BaseType, origin);
                foreach (var contract in type.GetInterfaces()) Visit(contract, origin);
                foreach (var parameter in type.GetGenericArguments()) Constraints(parameter, origin);
                Attributes(type.GetCustomAttributesData(), origin);
                foreach (var member in type.GetMembers(declared))
                {
                    Attributes(member.GetCustomAttributesData(), origin);
                    switch (member)
                    {
                        case FieldInfo field: Visit(field.FieldType, origin); break;
                        case PropertyInfo property: Visit(property.PropertyType, origin); break;
                        case EventInfo evt: Visit(evt.EventHandlerType, origin); break;
                        case MethodBase method:
                            if (method is MethodInfo info) { Visit(info.ReturnType, origin); Attributes(info.ReturnParameter.GetCustomAttributesData(), origin); }
                            foreach (var parameter in method.GetParameters())
                            { Visit(parameter.ParameterType, origin); Attributes(parameter.GetCustomAttributesData(), origin); }
                            if (method.IsGenericMethodDefinition)
                                foreach (var parameter in method.GetGenericArguments()) Constraints(parameter, origin);
                            break;
                    }
                }
            }
            rows.Add(new { identity = origin, source = assembly.Location, sha256 = Hash(assembly.Location), definitions = types.Length });
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            experimentOnly = true, productionReplacement = false, metadataOnly = true, applicationExecuted = false,
            payloads = payloads.Select(type => type.AssemblyQualifiedName), definitionCount = count,
            assemblies = rows, edges = edges.ToDictionary(pair => pair.Key, pair => pair.Value.Order(StringComparer.Ordinal)),
            closedSignatureShapes = shapes.Order(StringComparer.Ordinal),
            actualInputSha256 = paths.ToDictionary(path => path, Hash)
        }, new JsonSerializerOptions { WriteIndented = true }));

        void Add(Assembly assembly, string origin)
        {
            var identity = assembly.FullName!;
            if (!edges.TryGetValue(origin, out var destinations)) edges.Add(origin, destinations = new(StringComparer.Ordinal));
            destinations.Add(identity);
            if (modules.TryAdd(identity, assembly)) pending.Enqueue(assembly);
        }
        void Visit(Type? type, string origin)
        {
            if (type == null || type.IsGenericParameter) return;
            if (type.IsFunctionPointer)
            {
                Visit(type.GetFunctionPointerReturnType(), origin);
                foreach (var parameter in type.GetFunctionPointerParameterTypes()) Visit(parameter, origin);
                return;
            }
            if (!type.ContainsGenericParameters && type.AssemblyQualifiedName is { } shape) shapes.Add(shape);
            if (type.HasElementType) { Visit(type.GetElementType(), origin); return; }
            Add(type.Assembly, origin);
            if (type.IsGenericType && !type.IsGenericTypeDefinition)
                foreach (var argument in type.GetGenericArguments()) Visit(argument, origin);
        }
        void Constraints(Type parameter, string origin)
        {
            if (parameter.IsGenericParameter)
                foreach (var constraint in parameter.GetGenericParameterConstraints()) Visit(constraint, origin);
        }
        void Attributes(IList<CustomAttributeData> attributes, string origin)
        {
            foreach (var attribute in attributes)
            {
                Visit(attribute.AttributeType, origin);
                foreach (var argument in attribute.ConstructorArguments) Argument(argument, origin);
                foreach (var argument in attribute.NamedArguments) Argument(argument.TypedValue, origin);
            }
        }
        void Argument(CustomAttributeTypedArgument argument, string origin)
        {
            Visit(argument.ArgumentType, origin);
            if (argument.Value is Type type) Visit(type, origin);
            else if (argument.Value is IEnumerable<CustomAttributeTypedArgument> array)
                foreach (var element in array) Argument(element, origin);
        }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

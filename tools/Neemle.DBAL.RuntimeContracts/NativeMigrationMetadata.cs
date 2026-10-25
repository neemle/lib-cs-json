// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;

namespace Neemle.DBAL.Tools;

internal static class NativeMigrationMetadata
{
    internal static IReadOnlyList<Binding> Append(
        MetadataLoadContext context, IEnumerable<Assembly> assemblies, StringBuilder source)
    {
        var selected = assemblies.ToArray();
        var relational = selected.SingleOrDefault(assembly => assembly.GetName().Name == "Microsoft.EntityFrameworkCore.Relational");
        var migration = relational?.GetType("Microsoft.EntityFrameworkCore.Migrations.Migration");
        var snapshot = relational?.GetType("Microsoft.EntityFrameworkCore.Infrastructure.ModelSnapshot");
        if (relational?.GetType("Neemle.DBAL.EntityFrameworkCore.NativeAotMigrations") == null
            || migration == null || snapshot == null) return [];
        var bindings = new List<Binding>();
        foreach (var assembly in SelectDerivedAssemblies(context, selected, relational))
        {
            // Read the selected PE, not namespace/symbol order: EF uses DefinedTypes order.
            using var stream = File.OpenRead(assembly.Location);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            var types = new NativeParameterMetadata.SignatureTypes(context, assembly);
            foreach (var handle in metadata.TypeDefinitions)
            {
                if (metadata.GetString(metadata.GetTypeDefinition(handle).Name) == "<Module>") continue;
                var type = types.GetTypeFromDefinition(metadata, handle, 0)!;
                var isSnapshot = type.IsSubclassOf(snapshot);
                if (!isSnapshot && !type.IsSubclassOf(migration)) continue;
                var name = type.AssemblyQualifiedName
                    ?? throw new InvalidOperationException($"Cannot infer migration identity in '{assembly.Location}'.");
                var order = MetadataTokens.GetRowNumber(handle);
                source.Append("global::Neemle.DBAL.EntityFrameworkCore.NativeAotMigrations.Register(global::System.Type.GetType(")
                    .Append(JsonSerializer.Serialize(name)).Append(", throwOnError: true)!, ")
                    .Append(order).Append(", ").Append(isSnapshot ? "true" : "false").Append(");\n");
                bindings.Add(new Binding(assembly.Location, name, order, isSnapshot));
            }
        }
        return bindings;
    }

    internal sealed record Binding(string AssemblyPath, string TypeName, int MetadataOrder, bool Snapshot);

    internal static IReadOnlyList<string> AppendOperations(
        MetadataLoadContext context, IEnumerable<Assembly> assemblies, StringBuilder source)
    {
        var selected = assemblies.ToArray();
        var relational = selected.SingleOrDefault(assembly => assembly.GetName().Name == "Microsoft.EntityFrameworkCore.Relational");
        var operation = relational?.GetType("Microsoft.EntityFrameworkCore.Migrations.Operations.MigrationOperation");
        if (operation == null || relational?.GetType("Neemle.DBAL.EntityFrameworkCore.NativeAotMigrations")?
            .GetMethod("RegisterOperation") == null) return [];
        var names = new SortedSet<string>(StringComparer.Ordinal);
        void Add(Type? type)
        {
            if (type is { ContainsGenericParameters: false } && type.IsSubclassOf(operation)) names.Add(type.AssemblyQualifiedName!);
        }
        foreach (var assembly in SelectDerivedAssemblies(context, selected, relational!))
        {
            foreach (var type in assembly.GetTypes()) Add(type);
            using var stream = File.OpenRead(assembly.Location);
            using var image = new PEReader(stream);
            var reader = image.GetMetadataReader();
            var signature = new NativeParameterMetadata.SignatureTypes(context, assembly);
            for (var row = 1; row <= reader.GetTableRowCount(TableIndex.TypeSpec); row++)
                Add(reader.GetTypeSpecification(MetadataTokens.TypeSpecificationHandle(row)).DecodeSignature(signature, (NativeParameterMetadata.GenericContext?)null));
        }
        foreach (var name in names)
            source.Append("global::Neemle.DBAL.EntityFrameworkCore.NativeAotMigrations.RegisterOperation(global::System.Type.GetType(")
                .Append(JsonSerializer.Serialize(name)).Append(", throwOnError: true)!);\n");
        return names.ToArray();
    }

    private static IEnumerable<Assembly> SelectDerivedAssemblies(
        MetadataLoadContext context, Assembly[] selected, Assembly root)
    {
        var possible = new HashSet<string>(StringComparer.Ordinal) { root.FullName! };
        var simpleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.GetName().Name! };
        bool changed;
        do
        {
            changed = false;
            foreach (var assembly in selected)
            {
                if (possible.Contains(assembly.FullName!)) continue;
                foreach (var reference in assembly.GetReferencedAssemblies())
                {
                    // Only resolve edges into the inferred graph. Unrelated optional references
                    // must not become required merely because an assembly is a candidate.
                    if (!simpleNames.Contains(reference.Name!)) continue;
                    var resolved = context.LoadFromAssemblyName(reference);
                    if (resolved.GetName().Version is { } version && reference.Version is { } requested
                        && version < requested)
                        throw new InvalidOperationException($"The selected migration metadata assembly '{resolved.FullName}' "
                            + $"cannot satisfy the higher requested version '{reference.FullName}'.");
                    if (!possible.Contains(resolved.FullName!)) continue;
                    changed |= possible.Add(assembly.FullName!);
                    simpleNames.Add(assembly.GetName().Name!);
                    break;
                }
            }
        }
        while (changed);
        return selected.Where(assembly => possible.Contains(assembly.FullName!));
    }

    internal static IReadOnlyList<string> AppendColumns(
        MetadataLoadContext context, IEnumerable<Assembly> assemblies, IEnumerable<Type> anonymousShapes, StringBuilder source)
    {
        var selected = assemblies.ToArray();
        var relational = selected.SingleOrDefault(assembly => assembly.GetName().Name == "Microsoft.EntityFrameworkCore.Relational");
        var builder = relational?.GetType("Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder");
        if (builder == null || relational?.GetType("Neemle.DBAL.EntityFrameworkCore.NativeAotMigrations")?
                .GetMethod("RegisterColumns") == null) return [];
        var shapes = new HashSet<Type>(anonymousShapes.Where(type => type.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal)));
        foreach (var assembly in selected)
        {
            using var stream = File.OpenRead(assembly.Location);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            var signature = new NativeParameterMetadata.SignatureTypes(context, assembly);
            for (var row = 1; row <= metadata.GetTableRowCount(TableIndex.MethodSpec); row++)
            {
                var specification = metadata.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle(row));
                if (specification.Method.Kind != HandleKind.MemberReference) continue;
                var reference = metadata.GetMemberReference((MemberReferenceHandle)specification.Method);
                if (metadata.GetString(reference.Name) != "CreateTable") continue;
                var owner = reference.Parent.Kind switch
                {
                    HandleKind.TypeReference => signature.GetTypeFromReference(metadata, (TypeReferenceHandle)reference.Parent, 0),
                    HandleKind.TypeSpecification => signature.GetTypeFromSpecification(metadata, null, (TypeSpecificationHandle)reference.Parent, 0),
                    _ => null
                };
                if (owner != builder) continue;
                foreach (var type in specification.DecodeSignature(signature, null))
                    if (type != null && !type.ContainsGenericParameters) shapes.Add(type);
            }
        }
        var names = shapes.Select(type => type.AssemblyQualifiedName!).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        foreach (var name in names)
        {
            var type = "global::System.Type.GetType(" + JsonSerializer.Serialize(name) + ", throwOnError: true)!";
            source.Append("global::Neemle.DBAL.EntityFrameworkCore.NativeAotMigrations.RegisterColumns(").Append(type)
                .Append(", static () => ").Append(type).Append(".GetProperties(global::System.Reflection.BindingFlags.Public | ")
                .Append("global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.Instance | ")
                .Append("global::System.Reflection.BindingFlags.Static | global::System.Reflection.BindingFlags.DeclaredOnly));\n");
        }
        return names;
    }
}

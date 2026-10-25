// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Neemle.DBAL.Runtime.Build;

internal static class MetadataInventory
{
    internal static void WriteSuppressions(string assembly, string output)
    {
        using var stream = File.OpenRead(assembly);
        using var image = new PEReader(stream);
        var metadata = image.GetMetadataReader();
        var entries = new List<object>();
        foreach (var handle in metadata.CustomAttributes)
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (AttributeType(metadata, attribute.Constructor) != "System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessageAttribute")
            {
                continue;
            }

            var value = metadata.GetBlobReader(attribute.Value);
            if (value.ReadUInt16() != 1) throw new BadImageFormatException("Invalid custom attribute prolog.");
            var category = value.ReadSerializedString();
            var checkId = value.ReadSerializedString();
            var named = new Dictionary<string, string?>();
            var count = value.ReadUInt16();
            for (var index = 0; index < count; index++)
            {
                var kind = value.ReadByte();
                var type = value.ReadByte();
                if (kind is not (0x53 or 0x54) || type != 0x0e)
                    throw new BadImageFormatException("Unexpected suppression named argument encoding.");
                named.Add(value.ReadSerializedString()!, value.ReadSerializedString());
            }

            entries.Add(new { ownerToken = "0x" + MetadataTokens.GetToken(attribute.Parent).ToString("X8"),
                ownerKind = attribute.Parent.Kind.ToString(), category, checkId, named });
        }

        Write(output, new { assembly = Path.GetFullPath(assembly), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))),
            unconditionalSuppressionCount = entries.Count, entries });
        Console.WriteLine($"{Path.GetFileName(assembly)}: {entries.Count} compiled unconditional suppressions.");
    }

    internal static void WriteAnonymousPropertyRoots(string assembly, string output)
    {
        using var stream = File.OpenRead(assembly);
        using var image = new PEReader(stream);
        var metadata = image.GetMetadataReader();
        var assemblyName = metadata.GetString(metadata.GetAssemblyDefinition().Name);
        var entries = new List<object>();
        foreach (var handle in metadata.TypeDefinitions)
        {
            var definition = metadata.GetTypeDefinition(handle);
            if ((definition.Attributes & TypeAttributes.Sealed) == 0
                || TypeName(metadata, definition.BaseType) != "System.Object"
                || !definition.GetCustomAttributes().Any(h => AttributeType(metadata, metadata.GetCustomAttribute(h).Constructor)
                    == "System.Runtime.CompilerServices.CompilerGeneratedAttribute")) continue;
            var properties = definition.GetProperties().ToArray();
            if (properties.Length == 0) continue;
            // Structural object overrides identify anonymous projections without relying
            // on compiler-generated CLR name prefixes or template ordinal guesses.
            var overrides = definition.GetMethods().Select(metadata.GetMethodDefinition)
                .Where(m => (m.Attributes & MethodAttributes.Virtual) != 0 && (m.Attributes & MethodAttributes.NewSlot) == 0)
                .Select(m => metadata.GetString(m.Name)).ToHashSet(StringComparer.Ordinal);
            if (!new[] { "Equals", "GetHashCode", "ToString" }.All(overrides.Contains)) continue;
            entries.Add(new { typeName = TypeName(metadata, handle), assemblyName,
                properties = properties.Select(p => metadata.GetString(metadata.GetPropertyDefinition(p).Name)).ToArray(),
                genericParameterCount = definition.GetGenericParameters().Count });
        }

        Write(output, new { assembly = Path.GetFullPath(assembly), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))),
            inferredAnonymousDefinitions = entries.Count, entries,
            note = "Metadata inference only; this manifest does not inject or prove native retention." });
        Console.WriteLine($"{assemblyName}: inferred {entries.Count} anonymous property definitions.");
    }

    private static string AttributeType(MetadataReader reader, EntityHandle constructor)
        => constructor.Kind switch
        {
            HandleKind.MemberReference => TypeName(reader, reader.GetMemberReference((MemberReferenceHandle)constructor).Parent),
            HandleKind.MethodDefinition => TypeName(reader, reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType()),
            _ => ""
        };

    private static string TypeName(MetadataReader reader, EntityHandle handle)
    {
        if (handle.Kind == HandleKind.TypeReference)
        {
            var type = reader.GetTypeReference((TypeReferenceHandle)handle);
            return Join(reader.GetString(type.Namespace), reader.GetString(type.Name));
        }
        if (handle.Kind == HandleKind.TypeDefinition)
        {
            var type = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
            var parent = type.GetDeclaringType();
            return parent.IsNil ? Join(reader.GetString(type.Namespace), reader.GetString(type.Name))
                : TypeName(reader, parent) + "+" + reader.GetString(type.Name);
        }
        return "";
    }

    private static string Join(string scope, string name) => scope.Length == 0 ? name : scope + "." + name;
    private static void Write(string path, object value)
        => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + "\n");
}

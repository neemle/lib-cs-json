// Copyright (c) Neemle contributors. Licensed under the MIT License.

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Neemle.DBAL.Tools;

/// <summary>
/// MetadataLoadContext escapes identifier punctuation in Name/Namespace, while
/// PersistedAssemblyBuilder expects literal ECMA identifiers in TypeRef rows.
/// Obtain those identifiers from the actual definition rather than unescaping CLR
/// display names (which would corrupt identifiers containing literal backslashes).
/// </summary>
internal static class NativeEmissionTypeNames
{
    private static readonly ConditionalWeakTable<Module, Definitions> Modules = new();
    private static readonly ConditionalWeakTable<Type, LiteralType> Wrappers = new();

    internal static Type ForEmission(Type type) => type is LiteralType ? type : Wrappers.GetValue(type, value => new LiteralType(value));

    internal static void ValidateReferences(string path, MetadataLoadContext context)
    {
        using var image = new PEReader(File.OpenRead(path));
        var reader = image.GetMetadataReader();
        var resolved = new Dictionary<TypeReferenceHandle, (Definitions Definitions, int Token)>();
        var rows = new List<object>();
        foreach (var handle in reader.TypeReferences) Resolve(handle);
        File.WriteAllText(path + ".type-reference-proof.json", JsonSerializer.Serialize(new
        {
            generatedAssemblySha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
            validatedTypeReferences = rows.Count, definitionsReadFromActualMetadata = true, applicationExecuted = false, rows
        }, new JsonSerializerOptions { WriteIndented = true }));

        (Definitions Definitions, int Token) Resolve(TypeReferenceHandle handle)
        {
            if (resolved.TryGetValue(handle, out var known)) return known;
            var reference = reader.GetTypeReference(handle);
            Definitions definitions;
            var parent = 0;
            if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
            {
                var owner = Resolve((TypeReferenceHandle)reference.ResolutionScope);
                definitions = owner.Definitions;
                parent = owner.Token;
            }
            else if (reference.ResolutionScope.Kind == HandleKind.AssemblyReference)
            {
                var assembly = reader.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope);
                var identity = new AssemblyName
                {
                    Name = reader.GetString(assembly.Name), Version = assembly.Version,
                    CultureName = assembly.Culture.IsNil ? null : reader.GetString(assembly.Culture)
                };
                if ((assembly.Flags & AssemblyFlags.PublicKey) != 0) identity.SetPublicKey(reader.GetBlobBytes(assembly.PublicKeyOrToken));
                else identity.SetPublicKeyToken(reader.GetBlobBytes(assembly.PublicKeyOrToken));
                var module = context.LoadFromAssemblyName(identity).ManifestModule;
                definitions = Modules.GetValue(module, value => new Definitions(value));
            }
            else throw new NotSupportedException("Native type-reference proof requires an assembly or nested type scope: " + reference.ResolutionScope.Kind);
            var name = reader.GetString(reference.Name);
            var ns = reference.Namespace.IsNil ? "" : reader.GetString(reference.Namespace);
            if (!definitions.Tokens.TryGetValue((parent, ns, name), out var token))
                throw new InvalidOperationException($"Generated TypeRef '{ns}.{name}' has no literal ECMA definition in '{definitions.Path}' (parent 0x{parent:x8}).");
            rows.Add(new { typeReference = MetadataTokens.GetToken(handle), definition = token, assembly = definitions.Path,
                parent, name, @namespace = ns });
            return resolved[handle] = (definitions, token);
        }
    }

    private sealed class Definitions
    {
        internal readonly Dictionary<int, (string Name, string? Namespace)> Names = [];
        internal readonly Dictionary<(int Parent, string Namespace, string Name), int> Tokens = [];
        internal readonly string Path;

        internal Definitions(Module module)
        {
            Path = module.FullyQualifiedName;
            using var image = new PEReader(File.OpenRead(module.FullyQualifiedName));
            var reader = image.GetMetadataReader();
            foreach (var handle in reader.TypeDefinitions)
            {
                var definition = reader.GetTypeDefinition(handle);
                Names.Add(MetadataTokens.GetToken(handle), (reader.GetString(definition.Name),
                    definition.Namespace.IsNil ? null : reader.GetString(definition.Namespace)));
                var declaring = definition.GetDeclaringType();
                Tokens.Add((declaring.IsNil ? 0 : MetadataTokens.GetToken(declaring), reader.GetString(definition.Namespace),
                    reader.GetString(definition.Name)), MetadataTokens.GetToken(handle));
            }
        }
    }

    private sealed class LiteralType(Type type) : TypeDelegator(type)
    {
        // PAB unwraps UnderlyingSystemType before creating TypeRef rows. Keep
        // this metadata-only adapter until it has read the literal identifier.
        public override Type UnderlyingSystemType => this;
        private (string Name, string? Namespace) Definition => Modules.GetValue(typeImpl.Module, module => new Definitions(module)).Names[typeImpl.MetadataToken];
        public override string Name => typeImpl.HasElementType || typeImpl.IsGenericParameter ? typeImpl.Name : Definition.Name;
        public override string? Namespace => typeImpl.HasElementType || typeImpl.IsGenericParameter ? typeImpl.Namespace : Definition.Namespace;
        public override Type? DeclaringType => typeImpl.DeclaringType is { } declaring ? ForEmission(declaring) : null;
        public override bool IsGenericType => typeImpl.IsGenericType;
        public override bool IsGenericTypeDefinition => typeImpl.IsGenericTypeDefinition;
        public override bool IsConstructedGenericType => typeImpl.IsConstructedGenericType;
        public override bool IsGenericParameter => typeImpl.IsGenericParameter;
        public override bool IsGenericMethodParameter => typeImpl.IsGenericMethodParameter;
        public override bool IsGenericTypeParameter => typeImpl.IsGenericTypeParameter;
        public override int GenericParameterPosition => typeImpl.GenericParameterPosition;
        public override Type GetGenericTypeDefinition() => ForEmission(typeImpl.GetGenericTypeDefinition());
        public override Type[] GetGenericArguments() => typeImpl.GetGenericArguments().Select(ForEmission).ToArray();
        public override Type? GetElementType() => typeImpl.GetElementType() is { } element ? ForEmission(element) : null;
    }
}

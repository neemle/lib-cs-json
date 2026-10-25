// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.Emit;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Neemle.DBAL.Generators;

namespace Neemle.DBAL.Tools;

internal static class NativeParameterMetadata
{
    internal static void Generate(string manifest, string application, string output)
    {
        var paths = File.ReadAllLines(manifest).Where(line => !string.IsNullOrWhiteSpace(line))
            .Append(application).Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToArray();
        var conflict = paths.GroupBy(path => AssemblyName.GetAssemblyName(path).FullName, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (conflict != null)
            throw new InvalidOperationException($"Multiple selected metadata payloads for '{conflict.Key}': {string.Join(", ", conflict)}. "
                + "The metadata manifest must match the final ILC input/reference selection.");
        using var context = new MetadataLoadContext(new SelectedCompilerAssemblyResolver(paths), "System.Private.CoreLib");
        var entry = context.LoadFromAssemblyPath(Path.GetFullPath(application));
        var pending = new Queue<Assembly>();
        pending.Enqueue(entry);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var roots = new SortedDictionary<string, Type>(StringComparer.Ordinal);
        var inputs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        var hierarchyAvailable = false;
        var selectedAssemblies = new List<Assembly>();

        while (pending.Count > 0)
        {
            var assembly = pending.Dequeue();
            if (!visited.Add(assembly.FullName!)) continue;
            selectedAssemblies.Add(assembly);
            hierarchyAvailable |= assembly.GetType("Neemle.DBAL.EntityFrameworkCore.NativeAotHierarchyProperties") != null;
            inputs.Add(assembly.Location, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))));
            foreach (var attribute in assembly.GetCustomAttributesData())
            {
                if (attribute.AttributeType == context.CoreAssembly!.GetType("System.Reflection.AssemblyMetadataAttribute")
                    && attribute.ConstructorArguments.Count == 2
                    && attribute.ConstructorArguments[0].Value as string == NativeMetadataNames.BindingsKey
                    && attribute.ConstructorArguments[1].Value is string encoded)
                {
                    foreach (var name in Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Split('\n'))
                        if (!string.IsNullOrWhiteSpace(name))
                            Add(Type.GetType(name, context.LoadFromAssemblyName,
                                static (owner, type, ignoreCase) => owner?.GetType(type, throwOnError: true, ignoreCase), throwOnError: true)!);
                }
            }

            using var stream = File.OpenRead(assembly.Location);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            var provider = new SignatureTypes(context, assembly);
            foreach (var handle in metadata.TypeDefinitions)
            {
                var definition = metadata.GetTypeDefinition(handle);
                if (definition.GetGenericParameters().Count == 0
                    && metadata.GetString(definition.Name).StartsWith("<>f__AnonymousType", StringComparison.Ordinal))
                    Add(provider.GetTypeFromDefinition(metadata, handle, 0)!);
            }

            for (var row = 1; row <= metadata.GetTableRowCount(TableIndex.TypeSpec); row++)
            {
                provider.SawAnonymous = false;
                try
                {
                    var type = metadata.GetTypeSpecification(MetadataTokens.TypeSpecificationHandle(row))
                        .DecodeSignature(provider, (GenericContext?)null);
                    if (type != null && type.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal)) Add(type);
                }
                catch (Exception error) when (error is FileNotFoundException or TypeLoadException or BadImageFormatException)
                {
                    if (provider.SawAnonymous)
                        throw new InvalidOperationException($"Cannot infer anonymous parameter metadata in '{assembly.Location}', TypeSpec row {row}.", error);
                    unresolved.Add(assembly.GetName().Name + ": " + error.Message);
                }
            }

            AddGenericMethodShapes(assembly, metadata, pe, provider);

            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                try { pending.Enqueue(context.LoadFromAssemblyName(reference)); }
                catch (FileNotFoundException error) { unresolved.Add(reference.FullName + ": " + error.Message); }
            }
        }

        var source = new StringBuilder("// <auto-generated />\n#nullable enable\n")
            .Append("[assembly: global::Neemle.DBAL.EntityFrameworkCore.NativeAotGeneratedMetadataAttribute]\nnamespace Neemle.DBAL.Inferred;\n")
            .Append("public sealed class ParameterMetadataRoots : global::Neemle.DBAL.EntityFrameworkCore.INativeAotExtensionRoot {\n")
            .Append("public ParameterMetadataRoots() { }\npublic void Register() {\n");
        foreach (var pair in roots)
        {
            var type = "global::System.Type.GetType(\"" + Escape(pair.Key) + "\", throwOnError: true)!";
            if (hierarchyAvailable && pair.Value.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal))
            {
                source.Append("global::Neemle.DBAL.EntityFrameworkCore.NativeAotHierarchyProperties.Register(").Append(type);
                foreach (var name in pair.Value.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Select(property => property.Name).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal))
                    source.Append(", new global::Neemle.DBAL.EntityFrameworkCore.NativeAotHierarchyProperties.Property(\"").Append(Escape(name))
                        .Append("\", static () => ").Append(type).Append(".GetMember(\"").Append(Escape(name))
                        .Append("\", global::System.Reflection.MemberTypes.Property, global::System.Reflection.BindingFlags.Public | ")
                        .Append("global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.Instance | ")
                        .Append("global::System.Reflection.BindingFlags.DeclaredOnly))");
                source.Append(");\n");
            }
            var properties = pair.Value.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            source.Append("global::Neemle.DBAL.EntityFrameworkCore.NativeAotParameterValues.Register(").Append(type)
                .Append(", static () => ").Append(properties.Any(property => property.Name == "Value") ? type + ".GetProperty(\"Value\")" : "null")
                .Append(", static () => ").Append(properties.Any(property => property.Name == "IsNull") ? type + ".GetProperty(\"IsNull\")" : "null")
                .Append(");\n");
        }

        var migrations = NativeMigrationMetadata.Append(context, selectedAssemblies, source);
        var migrationColumns = NativeMigrationMetadata.AppendColumns(context, selectedAssemblies, roots.Values, source);
        var migrationOperations = NativeMigrationMetadata.AppendOperations(context, selectedAssemblies, source);
        var memberInterfaces = NativeMemberInterfaceMetadata.Append(context, roots.Values, source);
        source.Append("} }\n");
        Write(output, source.ToString());
        Write(output + ".json", JsonSerializer.Serialize(new
        {
            application = Path.GetFullPath(application),
            bridgeAssembly = NativeMetadataNames.Assembly(entry.GetName().Name!),
            rootType = NativeMetadataNames.RootType,
            roots = roots.Keys,
            migrations,
            migrationColumns,
            migrationOperations,
            memberInterfaces,
            inputs,
            unresolvedReferencesOutsideAnonymousRoots = unresolved,
            metadataOnly = true
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine($"Generated native parameter metadata: {roots.Count} exact types from {inputs.Count} assemblies.");

        void Add(Type type)
        {
            if (type.ContainsGenericParameters || type.IsByRefLike || type.AssemblyQualifiedName is not { } name || !roots.TryAdd(name, type)) return;
            // The formatter recursively formats Value's result, while IsNull is only a Boolean test.
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                if (property.Name == "Value" && property.PropertyType != type) Add(property.PropertyType);
        }

        void AddGenericMethodShapes(Assembly assembly, MetadataReader reader, PEReader image, SignatureTypes signature)
        {
            if (!reader.TypeDefinitions.Any(handle => reader.GetString(reader.GetTypeDefinition(handle).Name)
                    .StartsWith("<>f__AnonymousType", StringComparison.Ordinal))) return;
            var bodies = new Dictionary<MethodDefinitionHandle, IReadOnlyList<(OpCode Code, EntityHandle Handle)>>();
            var needed = new HashSet<MethodDefinitionHandle>();
            var calls = new Dictionary<MethodDefinitionHandle, HashSet<MethodDefinitionHandle>>();
            foreach (var handle in reader.MethodDefinitions)
            {
                var method = reader.GetMethodDefinition(handle);
                if (method.RelativeVirtualAddress == 0) continue;
                var operands = MetadataMethodOperands.Read(image.GetMethodBody(method.RelativeVirtualAddress));
                bodies.Add(handle, operands);
                var targets = new HashSet<MethodDefinitionHandle>();
                calls.Add(handle, targets);
                foreach (var operand in operands)
                {
                    signature.SawAnonymous = false;
                    _ = OperandType(operand.Handle, null);
                    if (signature.SawAnonymous) needed.Add(handle);
                    var target = operand.Handle.Kind == HandleKind.MethodSpecification
                        ? reader.GetMethodSpecification((MethodSpecificationHandle)operand.Handle).Method : operand.Handle;
                    if (target.Kind == HandleKind.MethodDefinition) targets.Add((MethodDefinitionHandle)target);
                }
            }

            bool changed;
            do
            {
                changed = false;
                foreach (var pair in calls)
                    if (!needed.Contains(pair.Key) && pair.Value.Any(needed.Contains)) changed |= needed.Add(pair.Key);
            } while (changed);

            var queue = new Queue<(MethodDefinitionHandle Method, GenericContext Context)>();
            for (var index = 1; index <= reader.GetTableRowCount(TableIndex.MethodSpec); index++)
                Schedule(reader.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle(index)), null);
            // A prebuilt library's open anonymous TypeSpec becomes closed at its caller's MemberRef.
            foreach (var callerAssembly in context.GetAssemblies().ToArray())
            {
                if (callerAssembly == assembly) continue;
                using var callerStream = File.OpenRead(callerAssembly.Location);
                using var callerImage = new PEReader(callerStream);
                var callerReader = callerImage.GetMetadataReader();
                var callerSignature = new SignatureTypes(context, callerAssembly);
                for (var index = 1; index <= callerReader.GetTableRowCount(TableIndex.MethodSpec); index++)
                {
                    var specification = callerReader.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle(index));
                    if (specification.Method.Kind != HandleKind.MemberReference) continue;
                    try
                    {
                        var reference = callerReader.GetMemberReference((MemberReferenceHandle)specification.Method);
                        var owner = reference.Parent.Kind switch
                        {
                            HandleKind.TypeReference => callerSignature.GetTypeFromReference(callerReader, (TypeReferenceHandle)reference.Parent, 0),
                            HandleKind.TypeSpecification => callerSignature.GetTypeFromSpecification(callerReader, null, (TypeSpecificationHandle)reference.Parent, 0),
                            _ => null
                        };
                        if (owner == null || owner.Assembly != assembly || owner.ContainsGenericParameters) continue;
                        var arguments = specification.DecodeSignature(callerSignature, null);
                        if (arguments.Any(type => type == null || type.ContainsGenericParameters)) continue;
                        var targetContext = new GenericContext(owner.GenericTypeArguments.Cast<Type?>().ToImmutableArray(), arguments);
                        var expected = reference.DecodeMethodSignature(callerSignature, targetContext);
                        var name = callerReader.GetString(reference.Name);
                        foreach (var candidate in owner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                        {
                            if (candidate.Name != name || !candidate.IsGenericMethodDefinition || candidate.GetGenericArguments().Length != arguments.Length) continue;
                            var handle = MetadataTokens.MethodDefinitionHandle(candidate.MetadataToken & 0x00ffffff);
                            if (!needed.Contains(handle)) continue;
                            var closed = candidate.MakeGenericMethod(arguments.Select(type => type!).ToArray());
                            if (closed.ReturnType == expected.ReturnType
                                && closed.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(expected.ParameterTypes))
                                queue.Enqueue((handle, targetContext));
                        }
                    }
                    catch (Exception error) when (error is FileNotFoundException or TypeLoadException or BadImageFormatException)
                    {
                        unresolved.Add(callerAssembly.GetName().Name + ": " + error.Message);
                    }
                }
            }
            var completed = new HashSet<string>(StringComparer.Ordinal);
            while (queue.Count > 0)
            {
                var item = queue.Dequeue();
                var identity = MetadataTokens.GetToken(item.Method) + ":" + string.Join(";", item.Context.TypeArguments.Select(type => type?.AssemblyQualifiedName))
                    + ":" + string.Join(";", item.Context.MethodArguments.Select(type => type?.AssemblyQualifiedName));
                if (!completed.Add(identity) || !bodies.TryGetValue(item.Method, out var operands)) continue;
                foreach (var operand in operands)
                {
                    var type = OperandType(operand.Handle, item.Context);
                    if (type != null && type.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal)) Add(type);
                    if (operand.Handle.Kind == HandleKind.MethodSpecification)
                        Schedule(reader.GetMethodSpecification((MethodSpecificationHandle)operand.Handle), item.Context);
                }
            }

            void Schedule(MethodSpecification specification, GenericContext? caller)
            {
                if (specification.Method.Kind != HandleKind.MethodDefinition || !needed.Contains((MethodDefinitionHandle)specification.Method)) return;
                var arguments = specification.DecodeSignature(signature, caller);
                if (arguments.Any(type => type == null || type.ContainsGenericParameters)) return;
                var method = reader.GetMethodDefinition((MethodDefinitionHandle)specification.Method);
                var declaring = signature.GetTypeFromDefinition(reader, method.GetDeclaringType(), 0);
                if (declaring == null || declaring.ContainsGenericParameters) return;
                queue.Enqueue(((MethodDefinitionHandle)specification.Method, new GenericContext([], arguments)));
            }

            Type? OperandType(EntityHandle handle, GenericContext? context)
            {
                if (handle.Kind == HandleKind.MemberReference) handle = reader.GetMemberReference((MemberReferenceHandle)handle).Parent;
                return handle.Kind switch
                {
                    HandleKind.TypeSpecification => signature.GetTypeFromSpecification(reader, context, (TypeSpecificationHandle)handle, 0),
                    _ => null
                };
            }
        }
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static void Write(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (!File.Exists(path) || File.ReadAllText(path) != value) File.WriteAllText(path, value, new UTF8Encoding(false));
    }

    internal sealed record GenericContext(ImmutableArray<Type?> TypeArguments, ImmutableArray<Type?> MethodArguments);

    internal sealed class SignatureTypes(MetadataLoadContext context, Assembly assembly) : ISignatureTypeProvider<Type?, GenericContext?>
    {
        internal bool SawAnonymous { get; set; }
        public Type? GetArrayType(Type? elementType, ArrayShape shape) => elementType?.MakeArrayType(shape.Rank);
        public Type? GetByReferenceType(Type? elementType) => elementType?.MakeByRefType();
        public Type? GetFunctionPointerType(MethodSignature<Type?> signature) => null;
        public Type? GetGenericInstantiation(Type? genericType, ImmutableArray<Type?> typeArguments)
            => genericType == null || typeArguments.Any(type => type == null) ? null : genericType.MakeGenericType(typeArguments.Select(type => type!).ToArray());
        public Type? GetGenericMethodParameter(GenericContext? genericContext, int index)
            => genericContext != null && index < genericContext.MethodArguments.Length ? genericContext.MethodArguments[index] : null;
        public Type? GetGenericTypeParameter(GenericContext? genericContext, int index)
            => genericContext != null && index < genericContext.TypeArguments.Length ? genericContext.TypeArguments[index] : null;
        public Type? GetModifiedType(Type? modifier, Type? unmodifiedType, bool isRequired) => unmodifiedType;
        public Type? GetPinnedType(Type? elementType) => elementType;
        public Type? GetPointerType(Type? elementType) => elementType?.MakePointerType();
        public Type? GetSZArrayType(Type? elementType) => elementType?.MakeArrayType();
        public Type? GetPrimitiveType(PrimitiveTypeCode typeCode)
            => context.CoreAssembly!.GetType("System." + (typeCode switch
            {
                PrimitiveTypeCode.IntPtr => "IntPtr", PrimitiveTypeCode.UIntPtr => "UIntPtr",
                _ => typeCode.ToString()
            }), throwOnError: true);

        public Type? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var definition = reader.GetTypeDefinition(handle);
            var name = reader.GetString(definition.Name);
            SawAnonymous |= name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal);
            return definition.GetDeclaringType().IsNil
                ? assembly.GetType(Qualified(reader.GetString(definition.Namespace), name), throwOnError: true)
                : GetTypeFromDefinition(reader, definition.GetDeclaringType(), rawTypeKind)!.GetNestedType(name, BindingFlags.Public | BindingFlags.NonPublic);
        }

        public Type? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var reference = reader.GetTypeReference(handle);
            var name = reader.GetString(reference.Name);
            SawAnonymous |= name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal);
            if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
                return GetTypeFromReference(reader, (TypeReferenceHandle)reference.ResolutionScope, rawTypeKind)!
                    .GetNestedType(name, BindingFlags.Public | BindingFlags.NonPublic);
            var owner = reference.ResolutionScope.Kind == HandleKind.AssemblyReference
                ? context.LoadFromAssemblyName(reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name))
                : assembly;
            return owner.GetType(Qualified(reader.GetString(reference.Namespace), name), throwOnError: true);
        }

        public Type? GetTypeFromSpecification(MetadataReader reader, GenericContext? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        private static string Qualified(string space, string name) => space.Length == 0 ? name : space + "." + name;
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Neemle.DBAL.Generators;

namespace Neemle.DBAL.Tools;

internal static class NativeQueryIteratorMetadata
{
    internal static void Generate(string manifest, string application, string output)
    {
        application = Path.GetFullPath(application);
        var paths = File.ReadAllLines(manifest).Where(path => !string.IsNullOrWhiteSpace(path))
            .Append(application).Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var path in paths) _ = AssemblyName.GetAssemblyName(path);
        var resolver = new SelectedCompilerAssemblyResolver(paths);
        using var context = new MetadataLoadContext(resolver, "System.Private.CoreLib");
        var entry = context.LoadFromAssemblyPath(application);
        var assemblyMetadata = context.CoreAssembly!.GetType("System.Reflection.AssemblyMetadataAttribute", true)!;
        var iteratorAttribute = context.CoreAssembly.GetType("System.Runtime.CompilerServices.IteratorStateMachineAttribute", true)!;
        var requests = new List<Request>();
        var shapeRequests = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var attribute in entry.GetCustomAttributesData().Where(attribute => attribute.AttributeType == assemblyMetadata))
        {
            var key = attribute.ConstructorArguments[0].Value as string;
            var encoded = (string)attribute.ConstructorArguments[1].Value!;
            if (key == NativeMetadataNames.QueryShapesKey)
            {
                foreach (var name in Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Split('\n'))
                    if (!string.IsNullOrWhiteSpace(name)) shapeRequests.Add(name);
                continue;
            }
            if (key != NativeMetadataNames.QueryIteratorsKey) continue;
            foreach (var line in Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Split('\n'))
            {
                var fields = line.Split('\t');
                if (fields.Length is not (7 or 8)) throw new InvalidDataException("Invalid inferred iterator request");
                var owner = Type.GetType(fields[0], context.LoadFromAssemblyName,
                    static (assembly, name, ignoreCase) => assembly?.GetType(name, true, ignoreCase), true)!;
                var arguments = fields.Length == 7 || fields[7].Length == 0 ? [] :
                    Encoding.UTF8.GetString(Convert.FromBase64String(fields[7])).Split('\n')
                        .Select(name => Type.GetType(name, requested => ValidateResolution(requested, context.LoadFromAssemblyName(requested)),
                            static (assembly, type, ignoreCase) => assembly?.GetType(type, true, ignoreCase), true)!).ToArray();
                requests.Add(new(owner, fields[1], fields[2], int.Parse(fields[3]), int.Parse(fields[4]), int.Parse(fields[5]), int.Parse(fields[6]), arguments));
            }
        }

        var roots = new SortedDictionary<string, Type>(StringComparer.Ordinal);
        var visited = new HashSet<Type>();
        void AddShape(Type type)
        {
            if (!visited.Add(type)) return;
            if (type.AssemblyQualifiedName is { } name) roots.TryAdd(name, type);
            // The selected implementation pack can expose interfaces absent
            // from reference assemblies. Follow its actual metadata graph.
            foreach (var contract in type.GetInterfaces()) AddShape(contract);
            if (type.BaseType is { } parent) AddShape(parent);
            if (type.IsConstructedGenericType) AddShape(type.GetGenericTypeDefinition());
        }
        foreach (var name in shapeRequests)
            AddShape(Type.GetType(name, requested => ValidateResolution(requested, context.LoadFromAssemblyName(requested)),
                static (assembly, type, ignoreCase) => assembly?.GetType(type, true, ignoreCase), true)!);
        var matched = new HashSet<Request>();
        string? sidecarPdb = null;
        if (requests.Count > 0)
        {
            using var stream = File.OpenRead(application);
            using var image = new PEReader(stream);
            var embedded = image.ReadDebugDirectory().Where(entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb).ToArray();
            using var symbols = embedded.Length == 1 ? image.ReadEmbeddedPortablePdbDebugDirectoryData(embedded[0])
                : MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(Path.ChangeExtension(application, ".pdb")));
            var pdb = symbols.GetMetadataReader();
            var codeView = image.ReadDebugDirectory().Single(entry => entry.Type == DebugDirectoryEntryType.CodeView);
            var pdbId = new BlobContentId(pdb.DebugMetadataHeader!.Id);
            if (image.ReadCodeViewDebugDirectoryData(codeView).Guid != pdbId.Guid || codeView.Stamp != pdbId.Stamp)
                throw new InvalidDataException("Iterator PDB identity does not match the actual application assembly");
            var methods = entry.GetTypes().SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)).ToDictionary(method => method.MetadataToken);
            foreach (var handle in pdb.MethodDebugInformation)
            {
                var debug = pdb.GetMethodDebugInformation(handle);
                var kickoff = debug.GetStateMachineKickoffMethod();
                if (kickoff.IsNil || !methods.TryGetValue(MetadataTokens.GetToken(kickoff), out var method)) continue;
                var attribute = method.GetCustomAttributesData().SingleOrDefault(attribute => attribute.AttributeType == iteratorAttribute);
                if (attribute?.ConstructorArguments[0].Value is not Type machine) continue;
                foreach (var point in debug.GetSequencePoints())
                {
                    if (point.IsHidden) continue;
                    var document = pdb.GetDocument(point.Document.IsNil ? debug.Document : point.Document);
                    var hash = Convert.ToHexString(pdb.GetBlobBytes(document.Hash));
                    var algorithm = pdb.GetGuid(document.HashAlgorithm) switch
                    {
                        var value when value == new Guid("8829d00f-11b8-4213-878b-770e8597ac16") => "Sha256",
                        var value when value == new Guid("ff1816ec-aa5e-4d10-87f7-6f4963833460") => "Sha1",
                        _ => throw new InvalidDataException("Unsupported iterator source checksum algorithm")
                    };
                    foreach (var request in requests)
                    {
                        if (request.Algorithm != algorithm || request.Hash != hash || !Contains(request, point)) continue;
                        var owner = method.DeclaringType;
                        while (owner != null && owner != request.Owner) owner = owner.DeclaringType;
                        if (owner == null) continue;
                        var actualMachine = machine;
                        if (machine.ContainsGenericParameters)
                        {
                            var metadata = image.GetMetadataReader();
                            var body = metadata.GetMethodDefinition(kickoff);
                            var signature = new NativeParameterMetadata.SignatureTypes(context, entry);
                            var generic = new NativeParameterMetadata.GenericContext(
                                method.DeclaringType!.GetGenericArguments().Cast<Type?>().ToImmutableArray(),
                                request.Arguments.Cast<Type?>().ToImmutableArray());
                            var definition = machine.IsGenericTypeDefinition ? machine : machine.GetGenericTypeDefinition();
                            var candidates = MetadataMethodOperands.Read(image.GetMethodBody(body.RelativeVirtualAddress))
                                .Where(operand => operand.Code == OpCodes.Newobj && operand.Handle.Kind == HandleKind.MemberReference)
                                .Select(operand => metadata.GetMemberReference((MemberReferenceHandle)operand.Handle).Parent)
                                .Where(parent => parent.Kind == HandleKind.TypeSpecification)
                                .Select(parent => signature.GetTypeFromSpecification(metadata, generic, (TypeSpecificationHandle)parent, 0))
                                .Where(type => type is { ContainsGenericParameters: false, IsGenericType: true }
                                    && type.GetGenericTypeDefinition() == definition).Distinct().ToArray();
                            if (candidates.Length != 1)
                                throw new InvalidOperationException("Cannot infer the exact closed iterator constructor from actual method metadata: " + method);
                            actualMachine = candidates[0]!;
                            var open = actualMachine.GetGenericTypeDefinition();
                            AddShape(open);
                        }
                        AddShape(actualMachine);
                        matched.Add(request);
                    }
                }
            }
            if (embedded.Length == 0) sidecarPdb = Path.ChangeExtension(application, ".pdb");
        }
        if (matched.Count != requests.Count)
            throw new InvalidOperationException($"Resolved {matched.Count} of {requests.Count} source-bound iterator requests from actual emitted PDB metadata.");

        var efInterfaces = paths.Select(path => context.LoadFromAssemblyPath(path))
            .Any(assembly => assembly.GetName().Name == "Microsoft.EntityFrameworkCore"
                && assembly.GetType("Neemle.DBAL.EntityFrameworkCore.NativeAotSequenceTypes") != null
                && assembly.GetType("Neemle.DBAL.EntityFrameworkCore.INativeAotExtensionRoot") != null);
        var bindingTypes = new SortedDictionary<string, Type>(StringComparer.Ordinal);
        var skippedOptionalAssemblyReferences = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var unifiedAssemblyReferences = new SortedDictionary<string, string>(StringComparer.Ordinal);
        Assembly ValidateResolution(AssemblyName requested, Assembly selected)
        {
            var actual = selected.GetName();
            if (requested.Version != null && actual.Version != null && actual.Version < requested.Version)
                throw new InvalidOperationException("The actual metadata reference manifest provides an older assembly than requested: "
                    + requested.FullName + " -> " + actual.FullName);
            return selected;
        }
        if (efInterfaces)
        {
            // A consumer can use a prebuilt model adapter without spelling its CLR
            // bindings or a query in its own source. Follow only its actual assembly
            // references and the adapter's generated contracts, using implementation
            // metadata rather than a catalog of application or framework type names.
            var pending = new Queue<Assembly>();
            var assemblies = new HashSet<string>(StringComparer.Ordinal);
            pending.Enqueue(entry);
            while (pending.Count > 0)
            {
                var assembly = pending.Dequeue();
                if (!assemblies.Add(assembly.FullName!)) continue;
                foreach (var attribute in assembly.GetCustomAttributesData())
                {
                    if (attribute.AttributeType != assemblyMetadata
                        || attribute.ConstructorArguments.Count != 2
                        || attribute.ConstructorArguments[0].Value as string != NativeMetadataNames.BindingsKey
                        || attribute.ConstructorArguments[1].Value is not string encoded) continue;
                    foreach (var name in Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Split('\n'))
                    {
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        bindingTypes.TryAdd(name, Type.GetType(name, requested => ValidateResolution(requested, context.LoadFromAssemblyName(requested)),
                            static (owner, type, ignoreCase) => owner?.GetType(type, true, ignoreCase), true)!);
                    }
                }
                foreach (var reference in assembly.GetReferencedAssemblies())
                {
                    var resolved = resolver.Resolve(context, reference);
                    if (resolved == null)
                    {
                        skippedOptionalAssemblyReferences.TryAdd(assembly.FullName! + " -> " + reference.FullName,
                            "The actual compiler reference manifest cannot resolve this optional assembly edge.");
                        continue;
                    }
                    ValidateResolution(reference, resolved);
                    if (!string.Equals(resolved.FullName, reference.FullName, StringComparison.OrdinalIgnoreCase))
                        unifiedAssemblyReferences.TryAdd(assembly.FullName! + " -> " + reference.FullName, resolved.FullName!);
                    pending.Enqueue(resolved);
                }
            }
        }
        var source = new StringBuilder("// <auto-generated/>\n#nullable enable\nnamespace Neemle.DBAL.Inferred;\n")
            .Append("public sealed class QueryIteratorRoots : global::Neemle.DBAL.Runtime.Queryable.INativeQueryRoot")
            .Append(efInterfaces ? ", global::Neemle.DBAL.EntityFrameworkCore.INativeAotExtensionRoot" : "")
            .Append(" {\npublic QueryIteratorRoots() {} public void Register() {\n");
        foreach (var root in roots.Keys)
        {
            source.Append("global::Neemle.DBAL.Runtime.Queryable.NativeQueryDescriptors.RegisterInterfaces(global::System.Type.GetType(")
                .Append(JsonSerializer.Serialize(root)).Append(", throwOnError: true)!);\n");
            if (efInterfaces)
                source.Append("global::Neemle.DBAL.EntityFrameworkCore.NativeAotSequenceTypes.RegisterType(global::System.Type.GetType(")
                    .Append(JsonSerializer.Serialize(root)).Append(", throwOnError: true)!);\n");
        }
        var memberInterfaces = NativeMemberInterfaceMetadata.Append(context, bindingTypes.Values, source);
        var migrationOperations = NativeMigrationMetadata.AppendOperations(context,
            paths.Select(context.LoadFromAssemblyPath), source);
        source.Append("} }\n");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        Write(output, source.ToString());
        var inputs = paths.ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        if (sidecarPdb != null)
            inputs.Add(sidecarPdb, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sidecarPdb))));
        Write(output + ".json", JsonSerializer.Serialize(new
        {
            bridgeAssembly = NativeMetadataNames.QueryAssembly(entry.GetName().Name!),
            roots = roots.Keys, requestCount = requests.Count, matchedRequestCount = matched.Count,
            bindingTypes = bindingTypes.Keys, memberInterfaces, migrationOperations, skippedOptionalAssemblyReferences, unifiedAssemblyReferences,
            metadataOnly = true, application, shapeRequestCount = shapeRequests.Count,
            inputs, pdbIdentityVerified = requests.Count > 0
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine($"Generated {roots.Count} exact iterator interface roots from {requests.Count} source/PDB bindings.");
    }

    private static bool Contains(Request request, SequencePoint point)
        => (point.StartLine > request.StartLine || point.StartLine == request.StartLine && point.StartColumn >= request.StartColumn)
            && (point.EndLine < request.EndLine || point.EndLine == request.EndLine && point.EndColumn <= request.EndColumn);

    private static void Write(string path, string value)
    {
        if (!File.Exists(path) || File.ReadAllText(path) != value) File.WriteAllText(path, value, new UTF8Encoding(false));
    }

    private sealed record Request(Type Owner, string Algorithm, string Hash, int StartLine, int StartColumn, int EndLine, int EndColumn, Type[] Arguments);
}

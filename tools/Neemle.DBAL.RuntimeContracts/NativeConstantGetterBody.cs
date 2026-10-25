// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Xml.Linq;

namespace Neemle.DBAL.Tools;

/// <summary>Proves selected native getter bodies return a literal or unconditionally throw, without file access.</summary>
internal sealed record NativeConstantGetterBody(string? Literal, MethodInfo? Error, string Source, int Token, string IlSha256,
    ConstructorInfo? ExceptionConstructor = null, string? RuntimeArchive = null)
{
    internal void Emit(ILGenerator body)
    {
        // Discard the non-null receiver already validated by the generated dispatch.
        body.Emit(OpCodes.Pop);
        if (Error != null)
        {
            body.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(Error));
            if (ExceptionConstructor != null) body.Emit(OpCodes.Newobj, NativePropertyAccessorEmitter.ConstructorForEmission(ExceptionConstructor));
            body.Emit(OpCodes.Throw);
        }
        else
        {
            if (Literal == null) body.Emit(OpCodes.Ldnull); else body.Emit(OpCodes.Ldstr, Literal);
            body.Emit(OpCodes.Ret);
        }
    }

    internal static NativeConstantGetterBody? Infer(Assembly core, Type receiver, MethodInfo method)
        => Infer(core, receiver, method, new HashSet<int>());

    private static NativeConstantGetterBody? Infer(Assembly core, Type receiver, MethodInfo method, HashSet<int> visiting)
    {
        var implementation = method.DeclaringType!.Assembly;
        // Keep application/provider contracts intact. The external Emit facade
        // must match the selected host or its actual NativeAOT runtime archive.
        // Runtime-pack facades can differ from the host despite equal identities.
        var emitter = typeof(PersistedAssemblyBuilder).Assembly;
        string? archive = null;
        if (implementation != core && (implementation.FullName != emitter.FullName
            || !SHA256.HashData(File.ReadAllBytes(implementation.Location)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(emitter.Location)))
                && (archive = RuntimeArchiveFor(implementation.Location)) == null))
            return null;
        if (method.IsStatic || method.ReturnType.IsValueType || method.ReturnType.IsByRef
            || method.ReturnType.IsPointer || method.ContainsGenericParameters || !visiting.Add(method.MetadataToken)) return null;
        using var stream = File.OpenRead(implementation.Location);
        using var image = new PEReader(stream);
        var metadata = image.GetMetadataReader();
        var definition = metadata.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.Handle(method.MetadataToken));
        if (definition.RelativeVirtualAddress == 0) return null;
        var block = image.GetMethodBody(definition.RelativeVirtualAddress);
        if (block.ExceptionRegions.Length != 0) return null;
        var bytes = block.GetILBytes()!;
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (bytes is [0x14, 0x2a]) return new(null, null, implementation.Location, method.MetadataToken, hash, RuntimeArchive: archive);
        if (bytes.Length == 6 && bytes[0] == 0x72 && bytes[5] == 0x2a)
            return new(metadata.GetUserString(MetadataTokens.UserStringHandle(BitConverter.ToInt32(bytes, 1) & 0x00ffffff)), null,
                implementation.Location, method.MetadataToken, hash, RuntimeArchive: archive);
        if (bytes.Length == 6 && bytes[0] == 0x28 && bytes[5] == 0x7a
            && Resolve(BitConverter.ToInt32(bytes, 1)) is { IsStatic: true } factory && factory.GetParameters().Length == 0
            && core.GetType("System.Exception", true)!.IsAssignableFrom(factory.ReturnType)
            && implementation == core && !Contract(factory) && LiteralExceptionFactory(factory))
            return new(null, factory, implementation.Location, method.MetadataToken, hash);
        if (implementation == core && bytes.Length == 11 && bytes[0] == 0x28 && bytes[5] == 0x73 && bytes[10] == 0x7a
            && Resolve(BitConverter.ToInt32(bytes, 1)) is { IsStatic: true } message && message.GetParameters().Length == 0
            && message.ReturnType == core.GetType("System.String") && !Contract(message)
            && Constructor(BitConverter.ToInt32(bytes, 6)) is { } errorConstructor
            && core.GetType("System.Exception", true)!.IsAssignableFrom(errorConstructor.DeclaringType)
            && errorConstructor.GetParameters() is [{ ParameterType: var argument }] && argument == core.GetType("System.String")
            && !errorConstructor.GetCustomAttributesData().Any(attribute => attribute.AttributeType.FullName is
                "System.Diagnostics.CodeAnalysis.RequiresAssemblyFilesAttribute" or "System.Diagnostics.CodeAnalysis.RequiresUnreferencedCodeAttribute"
                or "System.Diagnostics.CodeAnalysis.RequiresDynamicCodeAttribute"))
            return new(null, message, implementation.Location, method.MetadataToken, hash, errorConstructor);
        // A receiver-only virtual call that is proven to throw cannot reach the
        // remainder of its wrapper. Resolve the actual selected slot first.
        var call = bytes.Length >= 7 && bytes[0] == 0x02 && bytes[1] == 0x6f ? 1
            : bytes.Length >= 8 && bytes[0] == 0x02 && bytes[1] is >= 0x15 and <= 0x1e && bytes[2] == 0x6f ? 2 : -1;
        if (call != -1 && Resolve(BitConverter.ToInt32(bytes, call + 1)) is { } slot
            && slot.GetParameters().Length == call - 1)
        {
            var targets = receiver.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name == slot.Name && candidate.ReturnType == slot.ReturnType
                    && candidate.GetParameters().Select(parameter => parameter.ParameterType)
                        .SequenceEqual(slot.GetParameters().Select(parameter => parameter.ParameterType))
                    && NativeVirtualGetterDispatch.Root(candidate)?.MetadataToken == NativeVirtualGetterDispatch.Root(slot)?.MetadataToken).ToArray();
            // MetadataLoadContext cannot resolve vtables. Ordinary name/signature
            // reuse is sufficient only when the dispatch resolver has excluded
            // explicit MethodImpl mappings for this slot.
            if (targets.Length == 1 && Infer(core, receiver, targets[0], visiting) is { Error: not null } nested)
                return nested with { Source = implementation.Location, Token = method.MetadataToken, IlSha256 = hash };
        }
        return null;

        MethodInfo? Resolve(int token)
        {
            var handle = MetadataTokens.EntityHandle(token);
            if (handle.Kind != HandleKind.MethodDefinition) return null;
            var target = metadata.GetMethodDefinition((MethodDefinitionHandle)handle);
            var owner = FindType(target.GetDeclaringType());
            return owner.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .SingleOrDefault(candidate => candidate.MetadataToken == token);
        }
        Type FindType(TypeDefinitionHandle handle)
        {
            var type = metadata.GetTypeDefinition(handle);
            var parent = type.GetDeclaringType();
            if (!parent.IsNil) return FindType(parent).GetNestedType(metadata.GetString(type.Name), BindingFlags.Public | BindingFlags.NonPublic)!;
            var ns = metadata.GetString(type.Namespace);
            return implementation.GetType((ns.Length == 0 ? "" : ns + ".") + metadata.GetString(type.Name), true)!;
        }
        ConstructorInfo? Constructor(int token)
        {
            var handle = MetadataTokens.EntityHandle(token);
            if (handle.Kind != HandleKind.MethodDefinition) return null;
            var definition = metadata.GetMethodDefinition((MethodDefinitionHandle)handle);
            return FindType(definition.GetDeclaringType()).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SingleOrDefault(candidate => candidate.MetadataToken == token);
        }
        bool LiteralExceptionFactory(MethodInfo factory)
        {
            var definition = metadata.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.Handle(factory.MetadataToken));
            if (definition.RelativeVirtualAddress == 0) return false;
            var body = image.GetMethodBody(definition.RelativeVirtualAddress);
            var bytes = body.GetILBytes()!;
            if (body.ExceptionRegions.Length != 0 || !body.LocalSignature.IsNil || bytes.Length != 6 || bytes[0] != 0x73 || bytes[5] != 0x2a)
                return false;
            var handle = MetadataTokens.EntityHandle(BitConverter.ToInt32(bytes, 1));
            if (handle.Kind != HandleKind.MethodDefinition) return false;
            var constructor = metadata.GetMethodDefinition((MethodDefinitionHandle)handle);
            var type = FindType(constructor.GetDeclaringType());
            return core.GetType("System.Exception", true)!.IsAssignableFrom(type)
                && type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Any(candidate => candidate.MetadataToken == MetadataTokens.GetToken(handle) && candidate.GetParameters().Length == 0
                        && !candidate.GetCustomAttributesData().Any(attribute => attribute.AttributeType.FullName is
                            "System.Diagnostics.CodeAnalysis.RequiresAssemblyFilesAttribute" or "System.Diagnostics.CodeAnalysis.RequiresUnreferencedCodeAttribute"
                            or "System.Diagnostics.CodeAnalysis.RequiresDynamicCodeAttribute"));
        }
        static bool Contract(MethodInfo member)
            => member.GetCustomAttributesData().Concat(member.DeclaringType!.GetCustomAttributesData())
                .Concat(member.ReturnParameter.GetCustomAttributesData()).Any(attribute => attribute.AttributeType.FullName is
                    "System.Diagnostics.CodeAnalysis.RequiresAssemblyFilesAttribute" or "System.Diagnostics.CodeAnalysis.RequiresUnreferencedCodeAttribute"
                    or "System.Diagnostics.CodeAnalysis.RequiresDynamicCodeAttribute" or "System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembersAttribute");
    }

    private static string? RuntimeArchiveFor(string assembly)
    {
        var bytes = File.ReadAllBytes(assembly);
        for (var directory = new FileInfo(assembly).Directory; directory != null; directory = directory.Parent)
        {
            var relative = Path.GetRelativePath(directory.FullName, assembly).Replace(Path.DirectorySeparatorChar, '/');
            if (!relative.StartsWith("runtimes/", StringComparison.Ordinal)) continue;
            foreach (var path in Directory.EnumerateFiles(directory.FullName, "*.nupkg", SearchOption.TopDirectoryOnly))
            {
                using var package = ZipFile.OpenRead(path);
                var spec = package.Entries.SingleOrDefault(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
                if (spec == null) continue;
                using var metadata = spec.Open();
                var document = XDocument.Load(metadata);
                var id = document.Descendants().Single(element => element.Name.LocalName == "id").Value;
                var version = document.Descendants().Single(element => element.Name.LocalName == "version").Value;
                if (!id.StartsWith("Microsoft.NETCore.App.Runtime.NativeAOT.", StringComparison.OrdinalIgnoreCase)
                    || !version.StartsWith("10.", StringComparison.Ordinal)) continue;
                if (package.GetEntry(relative) is not { } entry) continue;
                using var content = entry.Open();
                if (SHA256.HashData(content).SequenceEqual(SHA256.HashData(bytes))) return path;
            }
        }
        return null;
    }
}

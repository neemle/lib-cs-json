// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Neemle.DBAL.Tools;

internal static class NativeAttributeQueryInference
{
    internal sealed record Request(Type Attribute, string Receiver, bool DefinitionOnly, IReadOnlyList<Type> ReceiverBases, IReadOnlyList<string> Callers);
    internal static IEnumerable<Type> Helpers(Assembly assembly)
    {
        // These are fixed bridge contracts, never application owner/type catalogs.
        var name = assembly.GetName().Name;
        var fullName = name == "Microsoft.EntityFrameworkCore"
            ? "Neemle.DBAL.EntityFrameworkCore.NativeAotAttributeQueries"
            : name is "System.Linq.Queryable" or "Neemle.DBAL.Runtime.Reflection" or "System.ComponentModel.TypeConverter"
                or "System.Linq.Expressions" or "System.Configuration.ConfigurationManager" or "Microsoft.Data.SqlClient" or "System.Private.CoreLib"
                or "System.Text.Json" or "Azure.Core"
                ? "Neemle.DBAL.Runtime.NativeAttributeQueries" : null;
        if (fullName != null && assembly.GetType(fullName) is { } helper) yield return helper;
    }
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    internal static IReadOnlyList<Request> Infer(MetadataLoadContext context, IEnumerable<Assembly> assemblies, IEnumerable<Type> shapes)
    {
        var selected = assemblies.Distinct().ToArray();
        var helpers = selected.SelectMany(Helpers).ToHashSet();
        if (helpers.Count == 0) return [];
        var attributeBase = context.CoreAssembly!.GetType("System.Attribute", true)!;
        var images = new Dictionary<Assembly, PEReader>();
        var readers = new Dictionary<Assembly, MetadataReader>();
        var signatures = new Dictionary<Assembly, NativeParameterMetadata.SignatureTypes>();
        try
        {
            foreach (var assembly in selected)
            {
                var image = new PEReader(File.OpenRead(assembly.Location));
                images.Add(assembly, image);
                readers.Add(assembly, image.GetMetadataReader());
                signatures.Add(assembly, new NativeParameterMetadata.SignatureTypes(context, assembly));
            }
            var callers = selected.Where(assembly => HasHelperCalls(assembly)).ToHashSet();
            var owners = new HashSet<Type>();
            foreach (var type in shapes.Concat(callers.SelectMany(assembly => assembly.GetTypes()))) AddOwner(type);
            var queue = new Queue<MethodBase>();
            foreach (var owner in owners.OrderBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal))
                if (callers.Contains(owner.Assembly))
                {
                    foreach (var method in owner.GetMethods(Declared).Where(method => !method.IsGenericMethodDefinition)) queue.Enqueue(method);
                    foreach (var constructor in owner.GetConstructors(Declared)) queue.Enqueue(constructor);
                }
            var completed = new HashSet<string>(StringComparer.Ordinal);
            var inferred = new Dictionary<(Type Attribute, string Receiver, bool DefinitionOnly), SortedSet<string>>();
            var receiverBases = new Dictionary<(Type Attribute, string Receiver, bool DefinitionOnly), HashSet<Type>>();
            while (queue.TryDequeue(out var method))
            {
                if (method.ContainsGenericParameters || method.DeclaringType == null || !readers.TryGetValue(method.DeclaringType.Assembly, out var reader)) continue;
                // Array Get/Set/Address/constructor members are runtime recipes,
                // not MethodDef rows with IL. Calls to them cannot contain helper
                // calls; MetadataLoadContext represents their token as row zero.
                if (method.DeclaringType.IsArray) continue;
                var identity = method.DeclaringType.AssemblyQualifiedName + ":" + method.MetadataToken + ":"
                    + string.Join(";", method.IsGenericMethod ? method.GetGenericArguments().Select(type => type.AssemblyQualifiedName) : []);
                if (!completed.Add(identity)) continue;
                MethodDefinition definition;
                int relativeVirtualAddress;
                try
                {
                    definition = reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(method.MetadataToken & 0x00ffffff));
                    relativeVirtualAddress = definition.RelativeVirtualAddress;
                }
                catch (BadImageFormatException error)
                {
                    throw new BadImageFormatException("Cannot inspect attribute-query method declaration: " + method.DeclaringType.AssemblyQualifiedName
                        + "::" + method + "; token=0x" + method.MetadataToken.ToString("x8") + "; module=" + method.Module.Name
                        + "; moduleMvid=" + method.Module.ModuleVersionId + "; image=" + method.DeclaringType.Assembly.Location, error);
                }
                if (relativeVirtualAddress == 0) continue;
                var arguments = new NativeParameterMetadata.GenericContext(
                    method.DeclaringType.GetGenericArguments().Cast<Type?>().ToImmutableArray(),
                    method.IsGenericMethod ? method.GetGenericArguments().Cast<Type?>().ToImmutableArray() : []);
                var assembly = method.DeclaringType.Assembly;
                MethodBodyBlock body;
                try { body = images[assembly].GetMethodBody(definition.RelativeVirtualAddress); }
                catch (BadImageFormatException error)
                {
                    throw new BadImageFormatException("Cannot inspect native attribute-query IL: " + method.DeclaringType.AssemblyQualifiedName
                        + "::" + method + "; token=0x" + method.MetadataToken.ToString("x8")
                        + "; RVA=0x" + definition.RelativeVirtualAddress.ToString("x8")
                        + "; implementation=" + definition.ImplAttributes + "; attributes=" + definition.Attributes
                        + "; image=" + assembly.Location, error);
                }
                foreach (var operand in MetadataMethodOperands.Read(body))
                {
                    if (operand.Code.OperandType is not (OperandType.InlineMethod or OperandType.InlineTok)) continue;
                    MethodBase? target;
                    try { target = Resolve(operand.Handle, method.DeclaringType, arguments, assembly); }
                    catch (Exception error) when (error is FileNotFoundException or TypeLoadException or BadImageFormatException or NotSupportedException)
                    {
                        // Unrelated framework calls may use signatures outside this contract. An
                        // unresolved actual attribute query remains an actionable native miss;
                        // it never authorizes constructing every attribute in the input universe.
                        continue;
                    }
                    if (target?.DeclaringType is { } helper && helpers.Contains(helper) && target.IsGenericMethod
                        && target.Name is "ReadAttribute" or "ReadAttributes" or "IsDefined" or "ReadAttributeForReceiver" or "IsDefinedForReceiver")
                    {
                        var genericArguments = target.GetGenericArguments();
                        var attribute = genericArguments[0];
                        if (attribute.ContainsGenericParameters || !attributeBase.IsAssignableFrom(attribute)) continue;
                        var receiver = target.GetParameters()[0].ParameterType.FullName!;
                        var key = (attribute, receiver, target.Name is "IsDefined" or "IsDefinedForReceiver");
                        if (target.Name is "ReadAttributeForReceiver" or "IsDefinedForReceiver")
                        {
                            if (genericArguments.Length != 2 || genericArguments[1].ContainsGenericParameters) continue;
                            if (!receiverBases.TryGetValue(key, out var bases)) receiverBases.Add(key, bases = new HashSet<Type>());
                            bases.Add(genericArguments[1]);
                        }
                        if (!inferred.TryGetValue(key, out var sites)) inferred.Add(key, sites = new SortedSet<string>(StringComparer.Ordinal));
                        sites.Add(identity);
                    }
                    else if (target?.DeclaringType is { } owner && callers.Contains(owner.Assembly)) queue.Enqueue(target);
                }
            }
            return inferred.OrderBy(pair => pair.Key.Attribute.AssemblyQualifiedName, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.Receiver, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.DefinitionOnly)
                .Select(pair => new Request(pair.Key.Attribute, pair.Key.Receiver, pair.Key.DefinitionOnly, receiverBases.TryGetValue(pair.Key, out var bases)
                    ? bases.OrderBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal).ToArray() : [], pair.Value.ToArray())).ToArray();

            void AddOwner(Type type)
            {
                if (type.ContainsGenericParameters || type.HasElementType || type.IsGenericParameter) return;
                for (var current = type; current != null && owners.Add(current); current = current.BaseType) { }
            }

            bool HasHelperCalls(Assembly assembly)
            {
                if (helpers.Any(helper => assembly == helper.Assembly)) return true;
                var reader = readers[assembly];
                var signature = signatures[assembly];
                foreach (var handle in reader.MemberReferences)
                {
                    var reference = reader.GetMemberReference(handle);
                    if (reader.GetString(reference.Name) is not ("ReadAttribute" or "ReadAttributes" or "IsDefined" or "ReadAttributeForReceiver" or "IsDefinedForReceiver")) continue;
                    if (reference.Parent.Kind == HandleKind.TypeReference
                        && helpers.Contains(signature.GetTypeFromReference(reader, (TypeReferenceHandle)reference.Parent, 0)!)) return true;
                }
                return false;
            }

            MethodBase? Resolve(EntityHandle handle, Type callerOwner, NativeParameterMetadata.GenericContext caller, Assembly assembly)
            {
                var reader = readers[assembly];
                var signature = signatures[assembly];
                ImmutableArray<Type?> methodArguments = [];
                if (handle.Kind == HandleKind.MethodSpecification)
                {
                    var specification = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                    methodArguments = specification.DecodeSignature(signature, caller);
                    if (methodArguments.Any(type => type == null || type.ContainsGenericParameters)) return null;
                    handle = specification.Method;
                }
                if (handle.Kind == HandleKind.MethodDefinition)
                {
                    var definition = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                    var owner = signature.GetTypeFromDefinition(reader, definition.GetDeclaringType(), 0);
                    if (owner?.IsGenericTypeDefinition == true
                        && callerOwner.IsGenericType && callerOwner.GetGenericTypeDefinition() == owner) owner = callerOwner;
                    if (owner == null || owner.ContainsGenericParameters) return null;
                    var target = Methods(owner).SingleOrDefault(candidate => candidate.MetadataToken == MetadataTokens.GetToken(handle));
                    return Close(target, methodArguments);
                }
                if (handle.Kind != HandleKind.MemberReference) return null;
                var reference = reader.GetMemberReference((MemberReferenceHandle)handle);
                if (reference.GetKind() != MemberReferenceKind.Method) return null;
                var referencedOwner = reference.Parent.Kind switch
                {
                    HandleKind.TypeDefinition => signature.GetTypeFromDefinition(reader, (TypeDefinitionHandle)reference.Parent, 0),
                    HandleKind.TypeReference => signature.GetTypeFromReference(reader, (TypeReferenceHandle)reference.Parent, 0),
                    HandleKind.TypeSpecification => signature.GetTypeFromSpecification(reader, caller, (TypeSpecificationHandle)reference.Parent, 0),
                    _ => null
                };
                if (referencedOwner == null || referencedOwner.ContainsGenericParameters) return null;
                var expected = reference.DecodeMethodSignature(signature, new NativeParameterMetadata.GenericContext(
                    referencedOwner.GetGenericArguments().Cast<Type?>().ToImmutableArray(), methodArguments));
                if (expected.ReturnType == null || expected.ParameterTypes.Any(type => type == null)) return null;
                var name = reader.GetString(reference.Name);
                foreach (var candidate in Methods(referencedOwner).Where(candidate => candidate.Name == name))
                {
                    var closed = Close(candidate, methodArguments);
                    if (closed == null || closed.ContainsGenericParameters) continue;
                    if (closed is MethodInfo info && info.ReturnType != expected.ReturnType) continue;
                    if (closed.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(expected.ParameterTypes)) return closed;
                }
                return null;
            }
        }
        finally
        {
            foreach (var image in images.Values) image.Dispose();
        }
    }

    private static IEnumerable<MethodBase> Methods(Type owner)
        => owner.GetMethods(Declared).Cast<MethodBase>().Concat(owner.GetConstructors(Declared));

    private static MethodBase? Close(MethodBase? method, ImmutableArray<Type?> arguments)
    {
        if (method is MethodInfo { IsGenericMethodDefinition: true } generic)
            return generic.GetGenericArguments().Length == arguments.Length && arguments.Length != 0
                ? generic.MakeGenericMethod(arguments.Select(type => type!).ToArray()) : null;
        return arguments.Length == 0 ? method : null;
    }
}

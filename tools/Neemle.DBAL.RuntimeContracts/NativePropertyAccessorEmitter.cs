// Copyright (c) Neemle contributors. Licensed under the MIT License.

using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Neemle.DBAL.Tools;

/// <summary>Emits direct getter calls for inferred compiled members, including inaccessible CLR type names.</summary>
internal static class NativePropertyAccessorEmitter
{
    internal const string Owner = "Neemle.DBAL.Inferred.PropertyAccessors";
    internal static MethodInfo MethodForEmission(MethodInfo method) => new EmissionMethod(method);
    internal static ConstructorInfo ConstructorForEmission(ConstructorInfo constructor) => new EmissionConstructor(constructor);
    internal static FieldInfo FieldForEmission(FieldInfo field) => new EmissionField(field);
    internal static MethodInfo ConstructorTokenForEmission(ConstructorInfo constructor, Type voidType) => new EmissionConstructorToken(constructor, voidType);

    internal static void Generate(Assembly coreAssembly, IEnumerable<PropertyInfo> inferred, string assemblyName, string output,
        Action<ModuleBuilder, TypeBuilder, IReadOnlyDictionary<PropertyInfo, MethodBuilder>>? complete = null,
        IEnumerable<Assembly>? additionalAccess = null,
        NativeVirtualGetterDispatch? virtualDispatch = null,
        Func<ModuleBuilder, TypeBuilder, PropertyInfo, ILGenerator, bool>? lowerGetter = null)
    {
        // Selection belongs to inference. This emitter never enumerates an application type catalog.
        var properties = inferred.DistinctBy(property => (property.Module.ModuleVersionId, property.MetadataToken,
                property.DeclaringType!.AssemblyQualifiedName))
            .OrderBy(property => property.DeclaringType!.AssemblyQualifiedName, StringComparer.Ordinal)
            .ThenBy(property => property.MetadataToken).ToArray();
        var dispatch = properties.ToDictionary(property => property, property => virtualDispatch?.Resolve(property));
        var objectType = coreAssembly.GetType("System.Object", true)!;
        var stringType = coreAssembly.GetType("System.String", true)!;
        var voidType = coreAssembly.GetType("System.Void", true)!;
        var attributeType = coreAssembly.GetType("System.Attribute", true)!;
        var assembly = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), coreAssembly);
        var module = assembly.DefineDynamicModule(assemblyName + ".dll");
        var accessAttribute = module.DefineType("System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute",
            TypeAttributes.NotPublic | TypeAttributes.Sealed, attributeType);
        var accessConstructor = accessAttribute.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [stringType]);
        var constructor = accessConstructor.GetILGenerator();
        constructor.Emit(OpCodes.Ldarg_0);
        constructor.Emit(OpCodes.Call, attributeType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, types: [], modifiers: null)!);
        constructor.Emit(OpCodes.Ret);
        accessAttribute.CreateType();
        var accessAttributes = new List<byte[]>();
        foreach (var name in properties.Select(property => property.DeclaringType!.Assembly.GetName().Name!)
                     .Concat((additionalAccess ?? []).Select(assembly => assembly.GetName().Name!))
                     .Concat(dispatch.Values.Where(arms => arms != null).SelectMany(arms => arms!)
                         .SelectMany(arm => new[] { arm.Receiver.Assembly, arm.Getter.DeclaringType!.Assembly }).Select(assembly => assembly.GetName().Name!))
                     .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var attribute = new BlobBuilder();
            attribute.WriteUInt16(1);
            attribute.WriteSerializedString(name);
            attribute.WriteUInt16(0);
            accessAttributes.Add(attribute.ToArray());
        }

        var owner = module.DefineType(Owner, TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, objectType);
        var rows = new List<object>();
        var methods = new Dictionary<PropertyInfo, MethodBuilder>();
        for (var index = 0; index < properties.Length; index++)
        {
            var property = properties[index];
            var getter = property.GetMethod ?? throw new InvalidOperationException("The inferred property has no getter: " + property);
            var declaring = getter.DeclaringType!;
            var result = getter.ReturnType;
            if (getter.ContainsGenericParameters || declaring.ContainsGenericParameters || declaring.IsByRefLike || getter.IsStatic
                || property.GetIndexParameters().Length != 0 || result.IsByRefLike || result.IsPointer
                || result.IsFunctionPointer || result.IsByRef && (result.GetElementType()!.IsByRefLike
                    || result.GetElementType()!.IsPointer || result.GetElementType()!.IsFunctionPointer))
                throw new InvalidOperationException("A direct native getter requires a closed, boxable, zero-index instance property: " + property);
            var name = "Get_" + index;
            var method = owner.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                objectType, [objectType]);
            var il = method.GetILGenerator();
            methods.Add(property, method);
            if (lowerGetter?.Invoke(module, owner, property, il) == true)
            {
                rows.Add(new { accessor = name, declaringType = declaring.AssemblyQualifiedName,
                    property = property.Name, getterToken = getter.MetadataToken, generatedAssemblyTypeOperation = true,
                    module = getter.Module.ModuleVersionId, returnType = getter.ReturnType.AssemblyQualifiedName,
                    structReceiverCopy = false,
                    sourceAssembly = declaring.Assembly.Location,
                    sourceAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(declaring.Assembly.Location))) });
                continue;
            }
            if (dispatch[property] is { } arms)
            {
                var receiver = il.DeclareLocal(declaring);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, declaring);
                il.Emit(OpCodes.Stloc, receiver);
                var present = il.DefineLabel();
                il.Emit(OpCodes.Ldloc, receiver);
                il.Emit(OpCodes.Brtrue, present);
                il.Emit(OpCodes.Newobj, coreAssembly.GetType("System.NullReferenceException", true)!.GetConstructor(Type.EmptyTypes)!);
                il.Emit(OpCodes.Throw);
                il.MarkLabel(present);
                foreach (var arm in arms)
                {
                    var next = il.DefineLabel();
                    il.Emit(OpCodes.Ldloc, receiver);
                    il.Emit(OpCodes.Isinst, arm.Receiver);
                    il.Emit(OpCodes.Brfalse, next);
                    il.Emit(OpCodes.Ldloc, receiver);
                    il.Emit(OpCodes.Castclass, arm.Getter.DeclaringType!);
                    if (arm.Body != null) arm.Body.Emit(il);
                    else
                    {
                        il.Emit(OpCodes.Callvirt, new EmissionMethod(arm.Getter));
                        il.Emit(OpCodes.Ret);
                    }
                    il.MarkLabel(next);
                }
                il.Emit(OpCodes.Ldstr, "No closed native virtual getter receiver was inferred for this selected slot.");
                il.Emit(OpCodes.Newobj, coreAssembly.GetType("System.InvalidOperationException", true)!.GetConstructor([stringType])!);
                il.Emit(OpCodes.Throw);
            }
            else
            {
                il.Emit(OpCodes.Ldarg_0);
                if (declaring.IsValueType)
                {
                    // Match DiagnosticSource's typed fetcher: a mutating getter runs
                    // on an unboxed copy, not on the original boxed payload.
                    var copy = il.DeclareLocal(declaring);
                    il.Emit(OpCodes.Unbox_Any, declaring);
                    il.Emit(OpCodes.Stloc, copy);
                    il.Emit(OpCodes.Ldloca, copy);
                    il.Emit(OpCodes.Call, new EmissionMethod(getter));
                }
                else
                {
                    il.Emit(OpCodes.Castclass, declaring);
                    il.Emit(OpCodes.Callvirt, new EmissionMethod(getter));
                }
                if (result.IsByRef)
                {
                    result = result.GetElementType()!;
                    il.Emit(OpCodes.Ldobj, result);
                }
                if (result.IsValueType) il.Emit(OpCodes.Box, result);
                il.Emit(OpCodes.Ret);
            }
            rows.Add(new
            {
                accessor = name, declaringType = declaring.AssemblyQualifiedName, property = property.Name,
                getterToken = getter.MetadataToken,
                virtualDispatch = dispatch[property]?.Select(arm => new { receiver = arm.Receiver.AssemblyQualifiedName,
                    declaring = arm.Getter.DeclaringType!.AssemblyQualifiedName, token = arm.Getter.MetadataToken,
                    loweredBody = arm.Body == null ? null : new { arm.Body.Literal,
                        error = arm.Body.Error == null ? null : new { owner = arm.Body.Error.DeclaringType!.AssemblyQualifiedName,
                            token = arm.Body.Error.MetadataToken }, exceptionConstructor = arm.Body.ExceptionConstructor?.MetadataToken,
                        arm.Body.Source, arm.Body.Token, arm.Body.IlSha256, arm.Body.RuntimeArchive,
                        runtimeArchiveSha256 = arm.Body.RuntimeArchive == null ? null : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(arm.Body.RuntimeArchive))) },
                    module = arm.Getter.Module.ModuleVersionId, source = arm.Getter.DeclaringType.Assembly.Location,
                    sourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(arm.Getter.DeclaringType.Assembly.Location))) }), module = getter.Module.ModuleVersionId,
                returnType = getter.ReturnType.AssemblyQualifiedName, structReceiverCopy = declaring.IsValueType,
                sourceAssembly = declaring.Assembly.Location,
                sourceAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(declaring.Assembly.Location)))
            });
        }
        complete?.Invoke(module, owner, methods);
        owner.CreateType();
        var metadata = assembly.GenerateMetadata(out var bodies, out var fieldData);
        foreach (var attribute in accessAttributes)
            metadata.AddCustomAttribute(MetadataTokens.EntityHandle(TableIndex.Assembly, 1),
                MetadataTokens.EntityHandle(accessConstructor.MetadataToken), metadata.GetOrAddBlob(attribute));
        var image = new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(metadata), bodies, mappedFieldData: fieldData);
        var contents = new BlobBuilder();
        image.Serialize(contents);
        var bytes = contents.ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        MakeDeterministic(bytes);
        File.WriteAllBytes(output, bytes);
        File.WriteAllText(output + ".json", JsonSerializer.Serialize(new
        {
            assemblyName, owner = Owner, accessors = rows, runtimeReflection = false,
            inferenceRequired = true, nativeVerified = false,
            sha256 = Convert.ToHexString(SHA256.HashData(bytes))
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void MakeDeterministic(byte[] bytes)
    {
        // PersistedAssemblyBuilder supplies a fresh MVID and a time-based PE ID.
        // Normalize both before deriving a content identity; no input is omitted.
        using var stream = new MemoryStream(bytes, writable: false);
        using var image = new PEReader(stream);
        var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(image);
        var mvid = metadata.GetModuleDefinition().Mvid;
        var offset = image.PEHeaders.MetadataStartOffset + metadata.GetHeapMetadataOffset(System.Reflection.Metadata.Ecma335.HeapIndex.Guid)
            + (MetadataTokens.GetHeapOffset(mvid) - 1) * 16;
        Array.Clear(bytes, offset, 16);
        var timestamp = BitConverter.ToInt32(bytes, 0x3c) + 8;
        Array.Clear(bytes, timestamp, 4);
        var hash = SHA256.HashData(bytes);
        hash.AsSpan(0, 16).CopyTo(bytes.AsSpan(offset, 16));
        hash.AsSpan(16, 4).CopyTo(bytes.AsSpan(timestamp, 4));
    }

    // .NET 10 PersistedAssemblyBuilder asks a modified metadata-only type for
    // GetGenericTypeDefinition, which MetadataLoadContext deliberately rejects.
    // Keep all custom modifiers and modified arguments; resolve only the definition
    // through the corresponding unmodified identity. This is build-time only.
    private sealed class EmissionType(Type type) : TypeDelegator(type)
    {
        public override bool IsGenericType => typeImpl.IsGenericType;
        public override bool IsGenericTypeDefinition => typeImpl.IsGenericTypeDefinition;
        public override bool IsConstructedGenericType => typeImpl.IsConstructedGenericType;
        public override bool IsGenericParameter => typeImpl.IsGenericParameter;
        public override bool IsGenericMethodParameter => typeImpl.IsGenericMethodParameter;
        public override bool IsGenericTypeParameter => typeImpl.IsGenericTypeParameter;
        public override int GenericParameterPosition => typeImpl.GenericParameterPosition;
        public override Type GetGenericTypeDefinition() => typeImpl.UnderlyingSystemType.GetGenericTypeDefinition();
        public override Type[] GetGenericArguments() => typeImpl.GetGenericArguments().Select(argument => (Type)new EmissionType(argument)).ToArray();
        public override Type? GetElementType() => typeImpl.GetElementType() is { } element ? new EmissionType(element) : null;
        public override Type[] GetRequiredCustomModifiers() => typeImpl.GetRequiredCustomModifiers();
        public override Type[] GetOptionalCustomModifiers() => typeImpl.GetOptionalCustomModifiers();
    }

    private sealed class EmissionParameter(ParameterInfo parameter) : ParameterInfo
    {
        public override Type GetModifiedParameterType() => new EmissionType(parameter.GetModifiedParameterType());
        public override Type ParameterType => parameter.ParameterType;
    }

    private sealed class EmissionOwner(Type type) : TypeDelegator(type)
    {
        public override bool IsConstructedGenericType => typeImpl.IsConstructedGenericType;
        public override bool IsGenericType => typeImpl.IsGenericType;
        public override bool IsGenericTypeDefinition => typeImpl.IsGenericTypeDefinition;
        public override Type GetGenericTypeDefinition() => new EmissionOwner(typeImpl.GetGenericTypeDefinition());
        public override Type[] GetGenericArguments() => typeImpl.GetGenericArguments();
        public override MemberInfo GetMemberWithSameMetadataDefinitionAs(MemberInfo member)
            => typeImpl.GetMemberWithSameMetadataDefinitionAs(member switch
            { EmissionMethod method => method.Inner, EmissionConstructor constructor => constructor.Inner, EmissionConstructorToken constructor => constructor.Inner, EmissionField field => field.Inner, _ => member }) switch
            { MethodInfo method => new EmissionMethod(method), ConstructorInfo constructor => new EmissionConstructor(constructor),
                FieldInfo field => new EmissionField(field), _ => throw new InvalidOperationException("Expected an inferred method, constructor or field.") };
    }

    // PersistedAssemblyBuilder's ConstructorInfo token path unconditionally
    // writes HASTHIS. A method signature adapter preserves static .cctor tokens.
    private sealed class EmissionConstructorToken(ConstructorInfo constructor, Type voidType) : MethodInfo
    {
        internal ConstructorInfo Inner => constructor;
        public override MethodAttributes Attributes => constructor.Attributes;
        public override CallingConventions CallingConvention => constructor.CallingConvention;
        public override RuntimeMethodHandle MethodHandle => constructor.MethodHandle;
        public override string Name => constructor.Name;
        public override Module Module => constructor.Module;
        public override int MetadataToken => constructor.MetadataToken;
        public override Type? DeclaringType => constructor.DeclaringType is { IsConstructedGenericType: true } type
            ? new ConstructorTokenOwner(type, voidType) : constructor.DeclaringType;
        public override Type? ReflectedType => constructor.ReflectedType;
        public override Type ReturnType => voidType;
        public override ParameterInfo ReturnParameter => new TokenReturn(voidType);
        public override ICustomAttributeProvider ReturnTypeCustomAttributes => ReturnParameter;
        public override bool IsGenericMethod => false;
        public override bool IsGenericMethodDefinition => false;
        public override bool ContainsGenericParameters => constructor.ContainsGenericParameters;
        public override Type[] GetGenericArguments() => [];
        public override MethodInfo GetBaseDefinition() => this;
        public override MethodImplAttributes GetMethodImplementationFlags() => constructor.GetMethodImplementationFlags();
        public override ParameterInfo[] GetParameters() => constructor.GetParameters().Select(parameter => (ParameterInfo)new EmissionParameter(parameter)).ToArray();
        public override object[] GetCustomAttributes(bool inherit) => constructor.GetCustomAttributes(inherit);
        public override object[] GetCustomAttributes(Type attributeType, bool inherit) => constructor.GetCustomAttributes(attributeType, inherit);
        public override bool IsDefined(Type attributeType, bool inherit) => constructor.IsDefined(attributeType, inherit);
        public override object? Invoke(object? obj, BindingFlags invokeAttr, Binder? binder, object?[]? parameters, System.Globalization.CultureInfo? culture)
            => throw new NotSupportedException("The metadata-only constructor token emitter never invokes application code.");
        private sealed class TokenReturn(Type type) : ParameterInfo
        {
            public override Type ParameterType => type;
            public override Type GetModifiedParameterType() => type;
        }
        private sealed class ConstructorTokenOwner(Type type, Type result) : TypeDelegator(type)
        {
            public override bool IsConstructedGenericType => typeImpl.IsConstructedGenericType;
            public override bool IsGenericType => typeImpl.IsGenericType;
            public override bool IsGenericTypeDefinition => typeImpl.IsGenericTypeDefinition;
            public override Type GetGenericTypeDefinition() => new ConstructorTokenOwner(typeImpl.GetGenericTypeDefinition(), result);
            public override Type[] GetGenericArguments() => typeImpl.GetGenericArguments();
            public override MemberInfo GetMemberWithSameMetadataDefinitionAs(MemberInfo member)
                => new EmissionConstructorToken((ConstructorInfo)typeImpl.GetMemberWithSameMetadataDefinitionAs(((EmissionConstructorToken)member).Inner), result);
        }
    }

    private sealed class EmissionField(FieldInfo source) : FieldInfo
    {
        internal FieldInfo Inner => source;
        public override FieldAttributes Attributes => source.Attributes;
        public override RuntimeFieldHandle FieldHandle => source.FieldHandle;
        public override Type FieldType => source.FieldType;
        public override Type GetModifiedFieldType() => new EmissionType(source.GetModifiedFieldType());
        public override Type[] GetRequiredCustomModifiers() => source.GetRequiredCustomModifiers();
        public override Type[] GetOptionalCustomModifiers() => source.GetOptionalCustomModifiers();
        public override Type? DeclaringType => source.DeclaringType switch
        {
            { IsConstructedGenericType: true } type => new EmissionOwner(type),
            { IsGenericTypeDefinition: true } type => new FieldDefinitionOwner(type),
            _ => source.DeclaringType
        };
        public override Type? ReflectedType => source.ReflectedType;
        public override string Name => source.Name;
        public override Module Module => source.Module;
        public override int MetadataToken => source.MetadataToken;
        public override object[] GetCustomAttributes(bool inherit) => source.GetCustomAttributes(inherit);
        public override object[] GetCustomAttributes(Type attributeType, bool inherit) => source.GetCustomAttributes(attributeType, inherit);
        public override bool IsDefined(Type attributeType, bool inherit) => source.IsDefined(attributeType, inherit);
        public override object? GetRawConstantValue() => source.GetRawConstantValue();
        public override object? GetValue(object? obj) => throw new NotSupportedException("The metadata-only field emitter never reads application state.");
        public override void SetValue(object? obj, object? value, BindingFlags invokeAttr, Binder? binder, System.Globalization.CultureInfo? culture)
            => throw new NotSupportedException("The metadata-only field emitter never modifies application state.");
        private sealed class FieldDefinitionOwner(Type definition) : TypeDelegator(definition)
        {
            public override bool IsGenericType => true;
            public override bool IsGenericTypeDefinition => true;
            public override bool IsConstructedGenericType => false;
            public override Type[] GetGenericArguments() => typeImpl.GetGenericArguments();
            // PAB normally rewrites a field's generic definition to Owner<!0>.
            // A handle factory outside that generic scope needs the definition
            // itself, not a TypeSpec referring to its caller's nonexistent !0.
            public override Type MakeGenericType(params Type[] typeArguments)
                => typeArguments.SequenceEqual(typeImpl.GetGenericArguments()) ? typeImpl : typeImpl.MakeGenericType(typeArguments);
        }
    }

    private sealed class EmissionConstructor(ConstructorInfo constructor) : ConstructorInfo
    {
        internal ConstructorInfo Inner => constructor;
        public override MethodAttributes Attributes => constructor.Attributes;
        public override RuntimeMethodHandle MethodHandle => constructor.MethodHandle;
        public override Type? DeclaringType => constructor.DeclaringType is { IsConstructedGenericType: true } declaring ? new EmissionOwner(declaring) : constructor.DeclaringType;
        public override Type? ReflectedType => constructor.ReflectedType;
        public override string Name => constructor.Name;
        public override Module Module => constructor.Module;
        public override int MetadataToken => constructor.MetadataToken;
        public override CallingConventions CallingConvention => constructor.CallingConvention;
        public override bool ContainsGenericParameters => constructor.ContainsGenericParameters;
        public override MethodImplAttributes GetMethodImplementationFlags() => constructor.GetMethodImplementationFlags();
        public override ParameterInfo[] GetParameters() => constructor.GetParameters().Select(parameter => (ParameterInfo)new EmissionParameter(parameter)).ToArray();
        public override object[] GetCustomAttributes(bool inherit) => constructor.GetCustomAttributes(inherit);
        public override object[] GetCustomAttributes(Type attributeType, bool inherit) => constructor.GetCustomAttributes(attributeType, inherit);
        public override bool IsDefined(Type attributeType, bool inherit) => constructor.IsDefined(attributeType, inherit);
        public override object Invoke(BindingFlags invokeAttr, Binder? binder, object?[]? parameters, System.Globalization.CultureInfo? culture)
            => throw new NotSupportedException("The metadata-only constructor emitter never invokes application code.");
        public override object? Invoke(object? obj, BindingFlags invokeAttr, Binder? binder, object?[]? parameters, System.Globalization.CultureInfo? culture)
            => throw new NotSupportedException("The metadata-only constructor emitter never invokes application code.");
    }

    private sealed class EmissionMethod(MethodInfo method) : MethodInfo
    {
        internal MethodInfo Inner => method;
        public override ParameterInfo ReturnParameter => new EmissionParameter(method.ReturnParameter);
        public override Type ReturnType => method.ReturnType;
        public override ICustomAttributeProvider ReturnTypeCustomAttributes => method.ReturnTypeCustomAttributes;
        public override MethodInfo GetBaseDefinition() => new EmissionMethod(method.GetBaseDefinition());
        public override MethodAttributes Attributes => method.Attributes;
        public override RuntimeMethodHandle MethodHandle => method.MethodHandle;
        public override Type? DeclaringType => method.DeclaringType is { IsConstructedGenericType: true } declaring ? new EmissionOwner(declaring) : method.DeclaringType;
        public override Type? ReflectedType => method.ReflectedType;
        public override string Name => method.Name;
        public override Module Module => method.Module;
        public override int MetadataToken => method.MetadataToken;
        public override CallingConventions CallingConvention => method.CallingConvention;
        public override bool IsGenericMethod => method.IsGenericMethod;
        public override bool IsGenericMethodDefinition => method.IsGenericMethodDefinition;
        public override bool ContainsGenericParameters => method.ContainsGenericParameters;
        public override Type[] GetGenericArguments() => method.GetGenericArguments();
        public override MethodInfo GetGenericMethodDefinition() => new EmissionMethod(method.GetGenericMethodDefinition());
        public override MethodInfo MakeGenericMethod(params Type[] typeArguments) => new EmissionMethod(method.MakeGenericMethod(typeArguments));
        public override MethodImplAttributes GetMethodImplementationFlags() => method.GetMethodImplementationFlags();
        public override ParameterInfo[] GetParameters() => method.GetParameters().Select(parameter => (ParameterInfo)new EmissionParameter(parameter)).ToArray();
        public override object[] GetCustomAttributes(bool inherit) => method.GetCustomAttributes(inherit);
        public override object[] GetCustomAttributes(Type attributeType, bool inherit) => method.GetCustomAttributes(attributeType, inherit);
        public override bool IsDefined(Type attributeType, bool inherit) => method.IsDefined(attributeType, inherit);
        public override object? Invoke(object? obj, BindingFlags invokeAttr, Binder? binder, object?[]? parameters, System.Globalization.CultureInfo? culture)
            => throw new NotSupportedException("The metadata-only accessor emitter never invokes application code.");
    }
}

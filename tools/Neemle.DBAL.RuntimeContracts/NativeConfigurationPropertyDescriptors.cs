// Copyright (c) Neemle contributors. Licensed under the MIT License.
// Attribute inheritance follows the MIT-licensed .NET Foundation implementation
// in runtime commit 6aa72daa24f66c01c3d7b6a32519e9d21396deec,
// src/coreclr/System.Private.CoreLib/src/System/Attribute.CoreCLR.cs.
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Neemle.DBAL.Tools;

/// <summary>Compiles configuration attribute construction and property policy from selected metadata.</summary>
internal sealed class NativeConfigurationPropertyDescriptors
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly MetadataLoadContext context;
    private readonly Assembly core;
    private readonly Type registry, element, propertyAttribute, validatorAttribute, collectionAttribute, usageAttribute;
    private readonly Type collection, property, validator, attribute, typeType, func;
    private readonly (Type Owner, Property[] Properties, CustomAttributeData? Validator)[] bags;
    private readonly Type[] values;
    internal IReadOnlyList<Type> PropertyTypes { get; }
    internal Type ElementType => element;
    internal Type ConverterType => property.GetConstructors().SelectMany(constructor => constructor.GetParameters())
        .Select(parameter => parameter.ParameterType).First(type => type.FullName == "System.ComponentModel.TypeConverter");
    internal void EmitAttribute(ILGenerator body, CustomAttributeData data) => Attribute(body, data);
    private readonly Dictionary<Type, (bool Inherited, bool Multiple)> usages = new();
    private readonly List<object> records = new();
    internal IReadOnlyList<object> Records => records;
    private sealed record Property(PropertyInfo Info, CustomAttributeData[] Attributes, CustomAttributeData Marker, CustomAttributeData? Collection);

    internal NativeConfigurationPropertyDescriptors(MetadataLoadContext context, Type constructorRegistry, IEnumerable<Type> candidates, IEnumerable<Type> inferredValues)
    {
        this.context = context;
        core = context.CoreAssembly!;
        var assembly = constructorRegistry.Assembly;
        registry = assembly.GetType("Neemle.DBAL.Runtime.NativeConfigurationProperties", true)!;
        element = assembly.GetType("Neemle.DBAL.Runtime.NativeConfigurationElements", true)!.GetMethod("Create")!.ReturnType;
        var register = registry.GetMethod("Register")!.GetParameters();
        collection = register[1].ParameterType.GenericTypeArguments[0];
        validator = register[2].ParameterType.GenericTypeArguments[0];
        property = collection.GetMethod("Add")!.GetParameters()[0].ParameterType;
        propertyAttribute = assembly.GetType("System.Configuration.ConfigurationPropertyAttribute", true)!;
        validatorAttribute = assembly.GetType("System.Configuration.ConfigurationValidatorAttribute", true)!;
        collectionAttribute = assembly.GetType("System.Configuration.ConfigurationCollectionAttribute", true)!;
        usageAttribute = core.GetType("System.AttributeUsageAttribute", true)!;
        attribute = core.GetType("System.Attribute", true)!;
        typeType = core.GetType("System.Type", true)!;
        func = core.GetType("System.Func`1", true)!;
        bags = candidates.Where(type => !type.ContainsGenericParameters && !type.IsByRefLike && element.IsAssignableFrom(type))
            .Distinct().OrderBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal).Select(owner =>
            {
                var entries = new List<Property>();
                foreach (var info in owner.GetProperties(Instance))
                {
                    var attributes = Attributes(info);
                    var markers = attributes.Where(data => data.AttributeType == propertyAttribute).ToArray();
                    if (markers.Length == 0) continue;
                    if (markers.Length != 1) throw new NotSupportedException("Ambiguous native configuration property attributes: " + info);
                    if (info.PropertyType.IsByRefLike || info.PropertyType.IsPointer || info.PropertyType.IsByRef || info.PropertyType.IsFunctionPointer)
                        throw new NotSupportedException("The inferred configuration property type cannot be boxed: " + info);
                    var policies = attributes.Where(data => data.AttributeType == collectionAttribute).ToArray();
                    if (policies.Length == 0) policies = Attributes(info.PropertyType).Where(data => data.AttributeType == collectionAttribute).ToArray();
                    if (policies.Length > 1) throw new NotSupportedException("Ambiguous native configuration collection policy: " + info);
                    entries.Add(new Property(info, attributes, markers[0], policies.SingleOrDefault()));
                }
                var validators = Attributes(owner).Where(data => validatorAttribute.IsAssignableFrom(data.AttributeType)).ToArray();
                if (validators.Length > 1) throw new NotSupportedException("Ambiguous native configuration type validators: " + owner);
                return (owner, entries.ToArray(), validators.SingleOrDefault());
            }).ToArray();
        PropertyTypes = inferredValues.Concat(bags.SelectMany(bag => bag.Properties).Select(entry => entry.Info.PropertyType))
            .Distinct().OrderBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal).ToArray();
        values = PropertyTypes
            .Where(type => type.IsValueType && !type.IsByRefLike && !type.ContainsGenericParameters).Distinct()
            .OrderBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal).ToArray();
    }

    internal IEnumerable<Assembly> AccessAssemblies => bags.Select(bag => bag.Owner.Assembly)
        .Concat(bags.SelectMany(bag => bag.Properties).Select(entry => entry.Info.PropertyType.Assembly))
        .Concat(values.Select(value => value.Assembly))
        .Concat(bags.SelectMany(bag => bag.Properties).SelectMany(entry => entry.Attributes.Append(entry.Collection).Where(data => data != null))
            .Select(data => data!.AttributeType.Assembly))
        .Concat(bags.Where(bag => bag.Validator != null).Select(bag => bag.Validator!.AttributeType.Assembly)).Append(core).Distinct();

    internal void Emit(TypeBuilder owner, ILGenerator registration)
    {
        var propertyConstructor = property.GetConstructors(Instance).Single(constructor => constructor.GetParameters() is { Length: 5 } args
            && args[0].ParameterType == typeType && args[3].ParameterType == attribute.MakeArrayType()
            && args[4].ParameterType == func.MakeGenericType(collectionAttribute));
        var collectionConstructor = collection.GetConstructor(Type.EmptyTypes)!;
        var add = collection.GetMethod("Add")!;
        var initializeChild = registry.GetMethod("InitializeChild")!;
        var valueRegistry = registry.Assembly.GetType("Neemle.DBAL.Runtime.NativeConfigurationValues", true)!;
        var valueIndex = 0;
        foreach (var valueType in values)
        {
            var factory = owner.DefineMethod("ValueDefault_" + valueIndex++, MethodAttributes.Public | MethodAttributes.Static,
                core.GetType("System.Object", true)!, Type.EmptyTypes);
            var il = factory.GetILGenerator();
            var constructor = valueType.GetConstructor(Instance, null, Type.EmptyTypes, null);
            if (constructor != null) il.Emit(OpCodes.Newobj, constructor);
            else
            {
                var value = il.DeclareLocal(valueType);
                il.Emit(OpCodes.Ldloca, value);
                il.Emit(OpCodes.Initobj, valueType);
                il.Emit(OpCodes.Ldloc, value);
            }
            il.Emit(OpCodes.Box, valueType);
            il.Emit(OpCodes.Ret);
            EmitType(registration, valueType);
            Delegate(registration, factory, core.GetType("System.Object", true)!);
            registration.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(valueRegistry.GetMethod("Register")!));
        }
        for (var index = 0; index < bags.Length; index++)
        {
            var bag = bags[index];
            var factory = owner.DefineMethod("Properties_" + index, MethodAttributes.Public | MethodAttributes.Static, collection, Type.EmptyTypes);
            var body = factory.GetILGenerator();
            var result = body.DeclareLocal(collection);
            var current = body.DeclareLocal(property);
            body.Emit(OpCodes.Newobj, collectionConstructor);
            body.Emit(OpCodes.Stloc, result);
            for (var position = 0; position < bag.Properties.Length; position++)
            {
                var entry = bag.Properties[position];
                // Attribute.GetCustomAttribute selects and constructs the canonical
                // marker before ConfigurationProperty constructs all attributes.
                Attribute(body, entry.Marker);
                body.Emit(OpCodes.Pop);
                EmitType(body, entry.Info.PropertyType);
                body.Emit(OpCodes.Ldstr, entry.Info.Name);
                EmitType(body, entry.Info.DeclaringType!);
                body.Emit(OpCodes.Ldc_I4, entry.Attributes.Length);
                body.Emit(OpCodes.Newarr, attribute);
                for (var argument = 0; argument < entry.Attributes.Length; argument++)
                {
                    body.Emit(OpCodes.Dup);
                    body.Emit(OpCodes.Ldc_I4, argument);
                    Attribute(body, entry.Attributes[argument]);
                    body.Emit(OpCodes.Stelem_Ref);
                }
                var policy = owner.DefineMethod("CollectionPolicy_" + index + "_" + position,
                    MethodAttributes.Public | MethodAttributes.Static, collectionAttribute, Type.EmptyTypes);
                var policyBody = policy.GetILGenerator();
                if (entry.Collection == null) policyBody.Emit(OpCodes.Ldnull); else Attribute(policyBody, entry.Collection);
                policyBody.Emit(OpCodes.Ret);
                Delegate(body, policy, collectionAttribute);
                body.Emit(OpCodes.Newobj, NativePropertyAccessorEmitter.ConstructorForEmission(propertyConstructor));
                body.Emit(OpCodes.Stloc, current);
                body.Emit(OpCodes.Ldloc, current);
                body.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(initializeChild));
                body.Emit(OpCodes.Ldloc, result);
                body.Emit(OpCodes.Ldloc, current);
                body.Emit(OpCodes.Callvirt, NativePropertyAccessorEmitter.MethodForEmission(add));
            }
            body.Emit(OpCodes.Ldloc, result);
            body.Emit(OpCodes.Ret);
            EmitType(registration, bag.Owner);
            Delegate(registration, factory, collection);
            if (bag.Validator == null) registration.Emit(OpCodes.Ldnull);
            else
            {
                var validation = owner.DefineMethod("TypeValidator_" + index, MethodAttributes.Public | MethodAttributes.Static, validator, Type.EmptyTypes);
                var il = validation.GetILGenerator();
                Attribute(il, bag.Validator);
                il.Emit(OpCodes.Dup);
                EmitType(il, bag.Owner);
                il.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(validatorAttribute.GetMethod("SetDeclaringType", Instance)!));
                il.Emit(OpCodes.Callvirt, NativePropertyAccessorEmitter.MethodForEmission(validatorAttribute.GetProperty("ValidatorInstance")!.GetMethod!));
                il.Emit(OpCodes.Ret);
                Delegate(registration, validation, validator);
            }
            registration.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(registry.GetMethod("Register")!));
            records.Add(new { owner = bag.Owner.AssemblyQualifiedName, bag.Owner.Module.ModuleVersionId,
                source = bag.Owner.Assembly.Location, sha256 = Hash(bag.Owner.Assembly),
                method = factory.Name, validator = bag.Validator?.AttributeType.AssemblyQualifiedName,
                properties = bag.Properties.Select(entry => new { entry.Info.Name, type = entry.Info.PropertyType.AssemblyQualifiedName,
                    declaring = entry.Info.DeclaringType!.AssemblyQualifiedName, token = entry.Info.MetadataToken,
                    attributes = entry.Attributes.Select(data => new { type = data.AttributeType.AssemblyQualifiedName,
                        constructor = data.Constructor.MetadataToken, source = data.AttributeType.Assembly.Location,
                        sha256 = Hash(data.AttributeType.Assembly) }), collection = entry.Collection?.AttributeType.AssemblyQualifiedName }) });
        }
    }

    private void Delegate(ILGenerator body, MethodBuilder method, Type result)
    {
        body.Emit(OpCodes.Ldnull);
        body.Emit(OpCodes.Ldftn, method);
        body.Emit(OpCodes.Newobj, func.MakeGenericType(result).GetConstructors().Single());
    }
    private void EmitType(ILGenerator body, Type type)
    {
        body.Emit(OpCodes.Ldtoken, type);
        body.Emit(OpCodes.Call, typeType.GetMethod("GetTypeFromHandle")!);
    }
    private void Attribute(ILGenerator body, CustomAttributeData data)
        => NativeAttributeConstructionEmitter.Emit(body, data, typeType);

    private CustomAttributeData[] Attributes(MemberInfo member)
    {
        var result = member.GetCustomAttributesData().ToList();
        var seen = result.Select(data => data.AttributeType).ToHashSet();
        MemberInfo? parent = Parent(member);
        while (parent != null)
        {
            foreach (var data in parent.GetCustomAttributesData())
            {
                var usage = Usage(data.AttributeType);
                if (usage.Inherited && (usage.Multiple || !seen.Contains(data.AttributeType)))
                { result.Add(data); seen.Add(data.AttributeType); }
            }
            parent = Parent(parent);
        }
        return result.ToArray();
    }
    private (bool Inherited, bool Multiple) Usage(Type type)
    {
        if (usages.TryGetValue(type, out var result)) return result;
        for (var current = type; current != null; current = current.BaseType)
            if (current.GetCustomAttributesData().SingleOrDefault(data => data.AttributeType == usageAttribute) is { } usage)
                return usages[type] = (!usage.NamedArguments.Any(value => value.MemberName == "Inherited" && value.TypedValue.Value is false),
                    usage.NamedArguments.Any(value => value.MemberName == "AllowMultiple" && value.TypedValue.Value is true));
        return usages[type] = (true, false);
    }
    private MemberInfo? Parent(MemberInfo member)
    {
        if (member is Type type) return type.BaseType;
        if (member is not PropertyInfo property) throw new NotSupportedException("Unsupported configuration attribute owner: " + member);
        var method = property.GetMethod ?? property.SetMethod;
        if (method == null || !method.IsVirtual) return null;
        RejectExplicitClassMapping(method);
        if ((method.Attributes & MethodAttributes.NewSlot) != 0) return null;
        for (var current = method.DeclaringType!.BaseType; current != null; current = current.BaseType)
        {
            var methods = current.GetMethods(Instance | BindingFlags.DeclaredOnly).Where(candidate => candidate.IsVirtual && !candidate.IsPrivate
                && candidate.Name == method.Name && candidate.ReturnType == method.ReturnType
                && candidate.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(method.GetParameters().Select(parameter => parameter.ParameterType))
                && candidate.ReturnParameter.GetRequiredCustomModifiers().SequenceEqual(method.ReturnParameter.GetRequiredCustomModifiers())
                && candidate.ReturnParameter.GetOptionalCustomModifiers().SequenceEqual(method.ReturnParameter.GetOptionalCustomModifiers())).ToArray();
            if (methods.Length == 0) continue;
            if (methods.Length != 1) throw new NotSupportedException("Ambiguous native configuration property slot: " + property);
            return current.GetProperty(property.Name, Instance | BindingFlags.DeclaredOnly, null, property.PropertyType,
                property.GetIndexParameters().Select(parameter => parameter.ParameterType).ToArray(), null);
        }
        throw new NotSupportedException("No exact inherited native configuration property slot was inferred: " + property);
    }
    private void RejectExplicitClassMapping(MethodInfo method)
    {
        using var stream = File.OpenRead(method.Module.Assembly.Location);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var definition = reader.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.Handle(method.DeclaringType!.MetadataToken));
        var signature = new NativeParameterMetadata.SignatureTypes(context, method.Module.Assembly);
        foreach (var handle in definition.GetMethodImplementations())
        {
            var implementation = reader.GetMethodImplementation(handle);
            if (implementation.MethodBody.Kind != HandleKind.MethodDefinition || MetadataTokens.GetToken(implementation.MethodBody) != method.MetadataToken) continue;
            var declaration = implementation.MethodDeclaration;
            Type? parent = declaration.Kind switch
            {
                HandleKind.MethodDefinition => signature.GetTypeFromDefinition(reader, reader.GetMethodDefinition((MethodDefinitionHandle)declaration).GetDeclaringType(), 0),
                HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)declaration).Parent.Kind switch
                {
                    HandleKind.TypeDefinition => signature.GetTypeFromDefinition(reader, (TypeDefinitionHandle)reader.GetMemberReference((MemberReferenceHandle)declaration).Parent, 0),
                    HandleKind.TypeReference => signature.GetTypeFromReference(reader, (TypeReferenceHandle)reader.GetMemberReference((MemberReferenceHandle)declaration).Parent, 0),
                    HandleKind.TypeSpecification => signature.GetTypeFromSpecification(reader, null, (TypeSpecificationHandle)reader.GetMemberReference((MemberReferenceHandle)declaration).Parent, 0),
                    _ => null
                },
                _ => null
            };
            if (parent?.IsInterface != true) throw new NotSupportedException("Explicit class MethodImpl configuration attribute inheritance requires a precise slot descriptor: " + method);
        }
    }
    private static string Hash(Assembly assembly) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)));
}

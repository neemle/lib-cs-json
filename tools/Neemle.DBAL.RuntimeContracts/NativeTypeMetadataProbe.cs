// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text;

namespace Neemle.DBAL.Tools;

/// <summary>Isolated executable metadata-handle experiment; not a production reflection fallback.</summary>
internal static class NativeTypeMetadataProbe
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    internal static void Generate(string manifest, string application, string output, bool includeCore = false, bool allCoreDefinitions = false, string? universe = null)
    {
        var paths = File.ReadAllLines(manifest).Append(application).Where(File.Exists).Select(Path.GetFullPath).Distinct().ToArray();
        using var context = new MetadataLoadContext(new SelectedCompilerAssemblyResolver(paths), "System.Private.CoreLib");
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(application));
        var core = context.CoreAssembly!;
        var defined = assembly.GetTypes();
        var memberProtocol = core.GetType("Neemle.DBAL.Runtime.NativeMemberHandles", false);
        var memberAttribute = core.GetType("Neemle.DBAL.Runtime.NativeMemberHandleAttribute", false);
        var hasMemberProtocol = memberProtocol != null && memberAttribute != null;
        var descriptorRecords = memberProtocol?.GetMethod("MethodFromRecord", BindingFlags.Static | BindingFlags.NonPublic) != null;
        if (hasMemberProtocol && memberAttribute!.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single().GetParameters().Length != 4)
            throw new NotSupportedException("The metadata probe requires generated member protocol version2.");
        var roots = new HashSet<Type>(defined.Where(type => !type.ContainsGenericParameters || type.IsGenericTypeDefinition));
        if (allCoreDefinitions) roots.UnionWith(core.GetTypes());
        if (universe != null)
        {
            using var inventory = JsonDocument.Parse(File.ReadAllText(universe));
            foreach (var row in inventory.RootElement.GetProperty("assemblies").EnumerateArray())
                roots.UnionWith(context.LoadFromAssemblyName(row.GetProperty("identity").GetString()!).GetTypes());
            if (inventory.RootElement.TryGetProperty("closedSignatureShapes", out var shapes))
                foreach (var shape in shapes.EnumerateArray())
                {
                    var type = Type.GetType(shape.GetString()!, context.LoadFromAssemblyName,
                        static (owner, name, ignoreCase) => owner?.GetType(name, true, ignoreCase), true)!;
                    if (!type.IsByRef && !type.IsPointer && !type.IsFunctionPointer) roots.Add(type);
                }
        }
        // Source generators already bind actual closed model/query contracts.
        // Reuse those inferred CLR shapes, including generic argument and array
        // element types; applications never maintain a metadata type catalog.
        foreach (var owner in paths.Select(context.LoadFromAssemblyPath))
            foreach (var attribute in owner.GetCustomAttributesData())
                if (attribute.AttributeType.FullName == "System.Reflection.AssemblyMetadataAttribute"
                    && attribute.ConstructorArguments.Count == 2
                    && attribute.ConstructorArguments[0].Value as string == Neemle.DBAL.Generators.NativeMetadataNames.BindingsKey
                    && attribute.ConstructorArguments[1].Value is string encoded)
                    foreach (var name in Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Split('\n'))
                        if (!string.IsNullOrWhiteSpace(name))
                            AddBound(Type.GetType(name, context.LoadFromAssemblyName,
                                static (assembly, type, ignoreCase) => assembly?.GetType(type, true, ignoreCase), true)!);
        using (var image = new PEReader(File.OpenRead(application)))
        {
            var reader = image.GetMetadataReader();
            var signature = new NativeParameterMetadata.SignatureTypes(context, assembly);
            foreach (var handle in reader.MethodDefinitions)
            {
                var method = reader.GetMethodDefinition(handle);
                if (method.RelativeVirtualAddress == 0) continue;
                var callerOwner = signature.GetTypeFromDefinition(reader, method.GetDeclaringType(), 0)!;
                var callerMethod = method.GetGenericParameters().Count == 0 ? null
                    : callerOwner.GetMethods(Declared).Single(candidate => candidate.MetadataToken == MetadataTokens.GetToken(handle));
                var caller = new NativeParameterMetadata.GenericContext(
                    callerOwner.GetGenericArguments().Cast<Type?>().ToImmutableArray(),
                    callerMethod?.GetGenericArguments().Cast<Type?>().ToImmutableArray() ?? []);
                foreach (var operand in MetadataMethodOperands.Read(image.GetMethodBody(method.RelativeVirtualAddress)))
                {
                    if (operand.Code != OpCodes.Ldtoken) continue;
                    var type = NativeMetadataTokenOwners.Resolve(reader, signature, operand.Handle, caller);
                    // Expression trees carry real MethodInfo tokens for framework
                    // methods too. Their declaring owners are exact metadata contracts;
                    // an incidental typeof(coreType) still does not opt in the core universe.
                    var methodToken = operand.Handle.Kind is HandleKind.MethodDefinition or HandleKind.MethodSpecification
                        || operand.Handle.Kind == HandleKind.MemberReference
                        && reader.GetMemberReference((MemberReferenceHandle)operand.Handle).GetKind() == MemberReferenceKind.Method;
                    if (type != null && (includeCore || type.Assembly != core || methodToken)
                        && (!type.ContainsGenericParameters || type.IsGenericTypeDefinition)) roots.Add(type);
                }
            }
        }
        foreach (var definition in roots.Where(type => !type.IsGenericParameter && !type.HasElementType)
                     .Select(type => type.IsGenericType ? type.GetGenericTypeDefinition() : type).Distinct().ToArray())
        {
            if (definition.IsGenericTypeDefinition)
                foreach (var parameter in definition.GetGenericArguments()) roots.Add(parameter);
            foreach (var method in definition.GetMethods(Declared).Where(method => method.IsGenericMethodDefinition))
                foreach (var parameter in method.GetGenericArguments()) roots.Add(parameter);
        }
        var types = roots.OrderBy(type => type.AssemblyQualifiedName ?? type.DeclaringType!.AssemblyQualifiedName + ":" + type.DeclaringMethod?.MetadataToken + ":" + type.GenericParameterPosition, StringComparer.Ordinal).ToArray();
        var selectedAssemblies = paths.Select(context.LoadFromAssemblyPath).ToArray();
        var serviceParameterBridge = selectedAssemblies.Any(NativeServiceParameterAttributeEmitter.IsPresent);
        var serviceDomain = serviceParameterBridge || selectedAssemblies.Any(NativeServiceParameterAttributeEmitter.HasRetentionBridge)
            ? NativeServiceConstructorMetadata.Infer(context, selectedAssemblies, types) : null;
        NativeServiceParameterAttributeEmitter.Summary? serviceAttributes = null;
        if (!hasMemberProtocol && types.SelectMany(type => type.GetInterfaces()).Any(type => type.ContainsGenericParameters && !type.IsGenericTypeDefinition))
            throw new NotSupportedException("The metadata probe needs native type-signature records for interfaces containing generic variables; no interface was omitted.");
        var initializerSites = NativeExpressionInitializerOperations.Infer(context, paths.Select(context.LoadFromAssemblyPath));
        NativeAttributeQueryEmitter.Summary? attributeQueries = null;
        NativeJsonMemberAttributeEmitter.Summary? jsonAttributes = null;
        NativeCustomDefaultValueEmitter.Summary? customDefaultValues = null;
        var attributeQueryCapability = paths.Select(context.LoadFromAssemblyPath)
            .Any(input => NativeAttributeQueryInference.Helpers(input).Any());
        var records = new List<object>();
        var assemblyTypes = types.Select(type => type.Assembly).Distinct().ToDictionary(value => value, value => value.GetTypes());
        NativePropertyAccessorEmitter.Generate(core, [], "Neemle.DBAL.TypeMetadataProbe", output, (module, owner, _) =>
        {
            var obj = core.GetType("System.Object", true)!;
            var typeType = core.GetType("System.Type", true)!;
            var typeInfo = core.GetType("System.Reflection.TypeInfo", true)!;
            var methodInfo = core.GetType("System.Reflection.MethodInfo", true)!;
            var ctorInfo = core.GetType("System.Reflection.ConstructorInfo", true)!;
            var fieldInfo = core.GetType("System.Reflection.FieldInfo", true)!;
            var propertyInfo = core.GetType("System.Reflection.PropertyInfo", true)!;
            var eventInfo = core.GetType("System.Reflection.EventInfo", true)!;
            var memberInfo = core.GetType("System.Reflection.MemberInfo", true)!;
            var methodBase = core.GetType("System.Reflection.MethodBase", true)!;
            var getType = typeType.GetMethod("GetTypeFromHandle")!;
            var getInfo = core.GetType("System.Reflection.IntrospectionExtensions", true)!.GetMethod("GetTypeInfo")!;
            var getMethod = methodBase.GetMethod("GetMethodFromHandle", [core.GetType("System.RuntimeMethodHandle", true)!, core.GetType("System.RuntimeTypeHandle", true)!])!;
            var getField = fieldInfo.GetMethod("GetFieldFromHandle", [core.GetType("System.RuntimeFieldHandle", true)!, core.GetType("System.RuntimeTypeHandle", true)!])!;
            var emitGetType = NativePropertyAccessorEmitter.MethodForEmission(getType);
            var emitGetInfo = NativePropertyAccessorEmitter.MethodForEmission(getInfo);
            var emitGetMethod = NativePropertyAccessorEmitter.MethodForEmission(getMethod);
            var emitGetField = NativePropertyAccessorEmitter.MethodForEmission(getField);
            var marker = hasMemberProtocol ? NativePropertyAccessorEmitter.ConstructorForEmission(memberAttribute!.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single()) : null;
            var protocolCalls = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
            var dispatch = owner.DefineMethod("Read", MethodAttributes.Public | MethodAttributes.Static, obj, [typeInfo, core.GetType("System.Int32", true)!]);
            var il = dispatch.GetILGenerator();
            var actual = il.DeclareLocal(typeType);
            var nativeRecords = new Dictionary<(Type Owner, int Token, int Kind, string Signature), FieldBuilder>();
            var assemblyFactories = new Dictionary<(Assembly Owner, int Kind), MethodBuilder>();
            var emptyFactories = new Dictionary<Type, MethodBuilder>();
            var nativeReceiver = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Isinst, core.GetType("System.RuntimeType", true)!);
            il.Emit(OpCodes.Brtrue, nativeReceiver);
            Fail(il, "Custom TypeInfo dispatch is outside the native metadata experiment; AsType aliases must not select CLR descriptors.");
            il.MarkLabel(nativeReceiver);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Callvirt, NativePropertyAccessorEmitter.MethodForEmission(typeInfo.GetMethod("AsType")!));
            il.Emit(OpCodes.Stloc, actual);
            // A canonical default-member attribute contains only a literal name.
            // Its presence and inherited value are invariant under closed generic
            // substitutions, including compiler-generated anonymous CLR types.
            var receiverReady = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldc_I4, 13); il.Emit(OpCodes.Bne_Un, receiverReady);
            il.Emit(OpCodes.Ldloc, actual);
            il.Emit(OpCodes.Callvirt, NativePropertyAccessorEmitter.MethodForEmission(typeType.GetProperty("IsConstructedGenericType")!.GetMethod!));
            il.Emit(OpCodes.Brfalse, receiverReady);
            il.Emit(OpCodes.Ldloc, actual);
            il.Emit(OpCodes.Callvirt, NativePropertyAccessorEmitter.MethodForEmission(typeType.GetMethod("GetGenericTypeDefinition")!));
            il.Emit(OpCodes.Stloc, actual);
            il.MarkLabel(receiverReady);
            var entry = il;
            var entryType = actual;
            var groups = new List<MethodBuilder>();
            foreach (var type in types)
            {
                // A single method for the whole runtime produces extreme JIT
                // liveness/code size. Bound each generated dispatch unit; the
                // entry point keeps only its receiver and one result live.
                if (records.Count % 64 == 0)
                {
                    if (groups.Count != 0) { il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Ret); }
                    var group = owner.DefineMethod("ReadGroup_" + groups.Count, MethodAttributes.Private | MethodAttributes.Static,
                        obj, [typeType, core.GetType("System.Int32", true)!]);
                    group.SetImplementationFlags(MethodImplAttributes.NoInlining);
                    groups.Add(group);
                    il = group.GetILGenerator();
                    actual = il.DeclareLocal(typeType);
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Stloc, actual);
                }
                var next = il.DefineLabel();
                il.Emit(OpCodes.Ldloc, actual); TypeToken(il, type); il.Emit(OpCodes.Ceq); il.Emit(OpCodes.Brfalse, next);
                var factories = new List<MethodBuilder>();
                var constructors = type.IsArray ? [] : type.GetConstructors(Declared);
                var methods = type.IsArray ? [] : type.GetMethods(Declared);
                var fields = type.IsArray ? [] : type.GetFields(Declared);
                var nested = type.IsArray ? [] : type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
                // MetadataLoadContext's array/generic-parameter adapters may
                // omit inherited interfaces. Infer the transitive closure from
                // the actual definitions for every shape, without name lists.
                var interfaces = InterfaceClosure(type);
                if (type.IsArray)
                {
                    ArrayFactory("ArrayConstructors", ctorInfo.MakeArrayType());
                    ArrayFactory("ArrayMethods", methodInfo.MakeArrayType());
                }
                else
                {
                    Factory(ctorInfo, constructors.Length, (body, index) => Method(body, constructors[index], ctorInfo));
                    Factory(methodInfo, methods.Length, (body, index) => Method(body, methods[index], methodInfo));
                }
                Factory(fieldInfo, fields.Length, (body, index) =>
                {
                    if (descriptorRecords) { MemberRecord(body, fields[index], 7); return; }
                    body.Emit(OpCodes.Ldtoken, NativePropertyAccessorEmitter.FieldForEmission(fields[index]));
                    body.Emit(OpCodes.Ldtoken, fields[index].DeclaringType!);
                    body.Emit(OpCodes.Call, emitGetField);
                });
                Factory(typeInfo, nested.Length, (body, index) => { TypeToken(body, nested[index]); body.Emit(OpCodes.Call, emitGetInfo); });
                Factory(typeType, interfaces.Length, (body, index) =>
                {
                    if (interfaces[index].ContainsGenericParameters && type.IsGenericParameter)
                        NativeRecord(body, type.DeclaringType!, type.MetadataToken, 4, Signature(interfaces[index]), type);
                    else if (interfaces[index].ContainsGenericParameters) NativeRecord(body, type, index, 3, Signature(interfaces[index]));
                    else TypeToken(body, interfaces[index]);
                });
                var ownerAssemblyTypes = assemblyTypes[type.Assembly];
                Factory(typeInfo, ownerAssemblyTypes.Length, (body, index) => { TypeToken(body, ownerAssemblyTypes[index]); body.Emit(OpCodes.Call, emitGetInfo); }, type.Assembly);
                var exported = ownerAssemblyTypes.Where(type => type.IsVisible).ToArray();
                Factory(typeType, exported.Length, (body, index) => TypeToken(body, exported[index]), type.Assembly);
                if (hasMemberProtocol)
                {
                    var properties = type.IsArray ? [] : type.GetProperties(Declared);
                    var events = type.IsArray ? [] : type.GetEvents(Declared);
                    Factory(propertyInfo, properties.Length, (body, index) => MemberRecord(body, properties[index], 1));
                    Factory(eventInfo, events.Length, (body, index) => MemberRecord(body, events[index], 2));
                    var members = type.IsArray ? [] : type.GetMembers(Declared);
                    if (type.IsArray) ArrayFactory("ArrayMembers", memberInfo.MakeArrayType());
                    else Factory(memberInfo, members.Length, (body, index) =>
                    {
                        switch (members[index])
                        {
                            case ConstructorInfo constructor: Method(body, constructor, ctorInfo); break;
                            case MethodInfo method: Method(body, method, methodInfo); break;
                            case FieldInfo field:
                                if (descriptorRecords) { MemberRecord(body, field, 7); break; }
                                body.Emit(OpCodes.Ldtoken, NativePropertyAccessorEmitter.FieldForEmission(field)); body.Emit(OpCodes.Ldtoken, field.DeclaringType!);
                                body.Emit(OpCodes.Call, emitGetField); break;
                            case PropertyInfo property: MemberRecord(body, property, 1); break;
                            case EventInfo evt: MemberRecord(body, evt, 2); break;
                            case Type nestedType: TypeToken(body, nestedType); break;
                            default: throw new NotSupportedException("Unimplemented generated metadata member kind: " + members[index]);
                        }
                    });
                }
                // Keep existing operation numbers 0-9 stable. Equality queries
                // use the CLR metadata lookup during generation, preserving its
                // order, inherited visibility, override and hiding rules.
                if (hasMemberProtocol)
                {
                    var equalityFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                    var equals = type.GetMember("Equals", MemberTypes.Method, equalityFlags).Cast<MethodInfo>().ToArray();
                    var operators = type.GetMember("op_Equality", MemberTypes.Method, equalityFlags | BindingFlags.DeclaredOnly).Cast<MethodInfo>().ToArray();
                    EqualityFactory(equals);
                    EqualityFactory(operators);
                    void EqualityFactory(MethodInfo[] selected)
                    {
                        if (selected.All(method => method.DeclaringType!.AssemblyQualifiedName != null))
                        {
                            Factory(methodInfo, selected.Length, (body, index) => Method(body, selected[index], methodInfo, type));
                            return;
                        }
                        // Application roots must be closed. Do not fabricate an
                        // empty result for an unrepresentable inherited owner in
                        // a framework generic definition inspected as metadata.
                        var unsupported = owner.DefineMethod("Metadata_" + records.Count + "_" + factories.Count,
                            MethodAttributes.Public | MethodAttributes.Static, methodInfo.MakeArrayType(), Type.EmptyTypes);
                        Fail(unsupported.GetILGenerator(), "Native equality metadata requires a closed inherited declaring type: " + type);
                        factories.Add(unsupported);
                    }
                }
                if (hasMemberProtocol)
                {
                    // Operation12 matches the exact inherited property lookup
                    // used by native indexer candidate selection.
                    var inheritedProperties = type.GetProperties(Declared & ~BindingFlags.DeclaredOnly);
                    if (inheritedProperties.All(property => property.DeclaringType!.AssemblyQualifiedName != null))
                        Factory(propertyInfo, inheritedProperties.Length, (body, index) => MemberRecord(body, inheritedProperties[index], 1, type));
                    else
                    {
                        var unsupported = owner.DefineMethod("Metadata_" + records.Count + "_" + factories.Count,
                            MethodAttributes.Public | MethodAttributes.Static, propertyInfo.MakeArrayType(), Type.EmptyTypes);
                        Fail(unsupported.GetILGenerator(), "Native inherited property metadata requires a closed declaring type: " + type);
                        factories.Add(unsupported);
                    }
                }
                if (hasMemberProtocol)
                {
                    // Operation13 is the first inherited canonical default-member
                    // name, with a zero-length array distinguishing absence from
                    // an explicitly null constructor argument. It executes no
                    // attribute constructor and uses actual emitted metadata.
                    var defaultAttribute = core.GetType("System.Reflection.DefaultMemberAttribute", true)!;
                    CustomAttributeData? found = null;
                    for (var current = type; current != null && found == null; current = current.BaseType)
                        found = current.GetCustomAttributesData().FirstOrDefault(data => data.AttributeType == defaultAttribute);
                    var defaultName = found?.ConstructorArguments.Single().Value as string;
                    Factory(core.GetType("System.String", true)!, found == null ? 0 : 1,
                        (body, _) => { if (defaultName == null) body.Emit(OpCodes.Ldnull); else body.Emit(OpCodes.Ldstr, defaultName); });
                }
                var branches = factories.Select(_ => il.DefineLabel()).ToArray();
                il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Switch, branches);
                Fail(il, "The metadata probe operation is not implemented.");
                for (var index = 0; index < factories.Count; index++)
                { il.MarkLabel(branches[index]); il.Emit(OpCodes.Call, factories[index]); il.Emit(OpCodes.Ret); }
                il.MarkLabel(next);
                records.Add(new { owner = type.AssemblyQualifiedName,
                    constructors = constructors.Select(member => member.MetadataToken), methods = methods.Select(member => member.MetadataToken),
                    fields = fields.Select(member => member.MetadataToken), nested = nested.Select(value => value.AssemblyQualifiedName),
                    interfaces = interfaces.Select(value => value.AssemblyQualifiedName) });

                void Factory(Type element, int count, Action<ILGenerator, int> emit, Assembly? sharedOwner = null)
                {
                    var key = (sharedOwner!, factories.Count);
                    if (sharedOwner != null && assemblyFactories.TryGetValue(key, out var shared))
                    { factories.Add(shared); return; }
                    if (count == 0 && emptyFactories.TryGetValue(element, out var empty))
                    { factories.Add(empty); return; }
                    var factory = owner.DefineMethod("Metadata_" + records.Count + "_" + factories.Count, MethodAttributes.Public | MethodAttributes.Static, element.MakeArrayType(), Type.EmptyTypes);
                    var body = factory.GetILGenerator(); body.Emit(OpCodes.Ldc_I4, count); body.Emit(OpCodes.Newarr, element);
                    for (var index = 0; index < count; index++)
                    { body.Emit(OpCodes.Dup); body.Emit(OpCodes.Ldc_I4, index); emit(body, index); body.Emit(OpCodes.Stelem_Ref); }
                    body.Emit(OpCodes.Ret); factories.Add(factory);
                    if (count == 0) emptyFactories.Add(element, factory);
                    if (sharedOwner != null) assemblyFactories.Add(key, factory);
                }
                void ArrayFactory(string helper, Type result)
                {
                    var implementation = memberProtocol?.GetMethod(helper, BindingFlags.Static | BindingFlags.NonPublic)
                        ?? throw new NotSupportedException("The selected native CoreLib has no generated array descriptor factory: " + helper);
                    var factory = owner.DefineMethod("Metadata_" + records.Count + "_" + factories.Count, MethodAttributes.Public | MethodAttributes.Static, result, Type.EmptyTypes);
                    var body = factory.GetILGenerator(); TypeToken(body, type); body.Emit(OpCodes.Ldc_I4, type.GetArrayRank());
                    body.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(implementation)); body.Emit(OpCodes.Ret); factories.Add(factory);
                }
            }
            il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Ret);
            foreach (var group in groups)
            {
                var next = entry.DefineLabel();
                entry.Emit(OpCodes.Ldloc, entryType); entry.Emit(OpCodes.Ldarg_1); entry.Emit(OpCodes.Call, group);
                entry.Emit(OpCodes.Dup); entry.Emit(OpCodes.Brfalse, next); entry.Emit(OpCodes.Ret);
                entry.MarkLabel(next); entry.Emit(OpCodes.Pop);
            }
            Fail(entry, "No CLR metadata shape was inferred. This experiment is not a production fallback.");

            NativeExpressionInitializerOperations.Emit(core, context.LoadFromAssemblyName("System.Linq.Expressions"), module, owner, initializerSites);
            if (attributeQueryCapability)
                attributeQueries = NativeAttributeQueryEmitter.Emit(context, paths.Select(context.LoadFromAssemblyPath), types, module, owner);
            jsonAttributes = NativeJsonMemberAttributeEmitter.Emit(context, selectedAssemblies, types, module, owner);
            if (hasMemberProtocol)
                customDefaultValues = NativeCustomDefaultValueEmitter.Emit(context, types.Concat(attributeQueries?.ReceiverOwners ?? [])
                    .Concat(serviceDomain?.ImplementationTypes ?? []), owner);
            if (serviceDomain != null)
            {
                if (!hasMemberProtocol)
                    throw new NotSupportedException("Generated service parameter attributes require the matched native constructor record protocol.");
                serviceAttributes = NativeServiceParameterAttributeEmitter.Emit(core, serviceDomain.ImplementationTypes, owner, serviceParameterBridge);
            }

            void TypeToken(ILGenerator body, Type type)
            {
                if (type.IsGenericParameter)
                {
                    if (type.DeclaringMethod is MethodInfo method)
                        Method(body, method, methodInfo);
                    else TypeToken(body, type.DeclaringType!);
                    body.Emit(OpCodes.Ldc_I4, type.GenericParameterPosition);
                    var helper = memberProtocol?.GetMethod(type.DeclaringMethod == null ? "GenericTypeParameter" : "GenericMethodParameter", BindingFlags.Static | BindingFlags.NonPublic)
                        ?? throw new NotSupportedException("The selected native CoreLib has no generated generic parameter factory.");
                    body.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(helper));
                }
                else { body.Emit(OpCodes.Ldtoken, NativeEmissionTypeNames.ForEmission(type)); body.Emit(OpCodes.Call, emitGetType); }
            }
            void Method(ILGenerator body, MethodBase member, Type result, Type? reflectedType = null)
            {
                if (descriptorRecords) { MemberRecord(body, member, member is ConstructorInfo ? 6 : 5, reflectedType); return; }
                var token = member is ConstructorInfo constructor
                    ? module.GetMethodMetadataToken(NativePropertyAccessorEmitter.ConstructorTokenForEmission(constructor, core.GetType("System.Void", true)!))
                    : module.GetMethodMetadataToken(NativePropertyAccessorEmitter.MethodForEmission((MethodInfo)member));
                body.Emit(OpCodes.Ldtoken, token); body.Emit(OpCodes.Ldtoken, member.DeclaringType!);
                body.Emit(OpCodes.Call, emitGetMethod); body.Emit(OpCodes.Castclass, result);
            }
            void MemberRecord(ILGenerator body, MemberInfo member, int kind, Type? reflectedType = null)
                => NativeRecord(body, member.DeclaringType!, member.MetadataToken, kind, "", reflectedType: reflectedType == member.DeclaringType ? null : reflectedType);
            void NativeRecord(ILGenerator body, Type declaring, int token, int kind, string signature, Type? signatureContext = null, Type? reflectedType = null)
            {
                var key = (declaring, token, kind, signature);
                if (!nativeRecords.TryGetValue(key, out var field))
                {
                    field = owner.DefineInitializedData("NativeMemberRecord_" + nativeRecords.Count, new byte[16], FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly);
                    var blob = new BlobBuilder(); blob.WriteUInt16(1);
                    blob.WriteSerializedString(declaring.AssemblyQualifiedName ?? throw new NotSupportedException("The native member owner has no metadata name."));
                    blob.WriteInt32(token); blob.WriteInt32(kind); blob.WriteSerializedString(signature); blob.WriteUInt16(0);
                    field.SetCustomAttribute(marker!, blob.ToArray());
                    nativeRecords.Add(key, field);
                }
                if (signatureContext != null) TypeToken(body, signatureContext);
                if (reflectedType != null) TypeToken(body, reflectedType);
                body.Emit(OpCodes.Ldsflda, field); body.Emit(OpCodes.Conv_U);
                var helper = reflectedType != null ? kind switch
                {
                    1 => "PropertyFromRecordWithReflectedType", 5 => "MethodFromRecordWithReflectedType",
                    _ => throw new NotSupportedException("This metadata operation has no inferred reflected-context factory: " + kind)
                } : kind switch
                {
                    1 => "PropertyFromRecord", 2 => "EventFromRecord", 4 => "TypeFromParameterRecord",
                    5 => "MethodFromRecord", 6 => "ConstructorFromRecord", 7 => "FieldFromRecord", _ => "TypeFromRecord"
                };
                if (!protocolCalls.TryGetValue(helper, out var read))
                    protocolCalls.Add(helper, read = NativePropertyAccessorEmitter.MethodForEmission(memberProtocol!.GetMethod(helper, BindingFlags.Static | BindingFlags.NonPublic)!));
                body.Emit(OpCodes.Call, read);
            }
            void Fail(ILGenerator body, string message)
            { body.Emit(OpCodes.Ldstr, message); body.Emit(OpCodes.Newobj, core.GetType("System.NotSupportedException", true)!.GetConstructor([core.GetType("System.String", true)!])!); body.Emit(OpCodes.Throw); }
        }, additionalAccess: assemblyTypes.Keys.Append(core).Concat(paths.Select(context.LoadFromAssemblyPath)));
        NativeEmissionTypeNames.ValidateReferences(output, context);
        File.WriteAllText(output + ".metadata-handles.json", JsonSerializer.Serialize(new
        {
            experimentOnly = true, productionReplacement = false,
            metadataOnly = (attributeQueries == null || attributeQueries.Queries.All(query => query.DefinitionOnly))
                && (customDefaultValues == null || customDefaultValues.Operations.Count == 0)
                && (serviceAttributes == null || serviceAttributes.Attributes == 0)
                && (jsonAttributes == null || jsonAttributes.Attributes == 0), runtimeMemberDiscovery = false,
            inferredAttributeConstruction = attributeQueries != null && attributeQueries.Queries.Any(query => !query.DefinitionOnly)
                || customDefaultValues != null && customDefaultValues.Operations.Count != 0
                || serviceAttributes != null && serviceAttributes.Attributes != 0
                || jsonAttributes != null && jsonAttributes.Attributes != 0,
            jsonAttributeOperations = jsonAttributes,
            runtimeParameterMetadataReads = true,
            customDefaultValueOperations = customDefaultValues,
            serviceConstructorAttributes = serviceDomain == null ? null : new
            {
                implementationTypes = serviceDomain.ImplementationTypes.Select(type => type.AssemblyQualifiedName),
                registrations = serviceDomain.Registrations.Select(registration => new
                {
                    service = registration.ServiceType?.AssemblyQualifiedName,
                    implementation = registration.ImplementationType.AssemblyQualifiedName,
                    registration.Caller, registration.Sink, registration.ClosedFromOpenGeneric
                }),
                serviceDomain.UnresolvedRegistrations,
                emittedOperations = serviceAttributes,
                rawParameterBridgePresent = serviceParameterBridge,
                applicationConstructorsExecutedDuringGeneration = false,
                separateFromModelAndGeneralMetadataRoots = true
            },
            frameworkInterfaceOrderRuntime = Environment.Version.ToString(),
            frameworkInterfaceOrderAssembly = typeof(object).Assembly.Location,
            methodDescriptorsRetainInvocationBodies = !descriptorRecords,
            attributeQueryFactories = attributeQueries == null ? null : new
            {
                queries = attributeQueries.Queries.Select(query => new { attribute = query.Attribute.AssemblyQualifiedName, query.Receiver, query.DefinitionOnly, ReceiverBases = query.ReceiverBases.Select(type => type.AssemblyQualifiedName), query.Callers }),
                attributeQueries.ModelOwners,
                attributeQueries.Factories,
                ReceiverOwners = attributeQueries.ReceiverOwners.Select(type => type.AssemblyQualifiedName),
                applicationAttributesExecutedDuringGeneration = false,
                customReflectionProviderFallback = false
            },
            initializerOperations = initializerSites.Select(site => new { site.Source, site.Module, site.MethodToken, site.Offset, site.PeSha256, site.IlSha256, property = site.Property.MetadataToken, accessor = site.Accessor.MetadataToken, helper = site.Name }),
            roots = records, deferredOpenDefinitions = defined.Where(type => type.ContainsGenericParameters && !type.IsGenericTypeDefinition).Select(type => type.AssemblyQualifiedName),
            nativeMemberRecordProtocolPresent = hasMemberProtocol,
            unimplemented = hasMemberProtocol ? new[] { "custom TypeInfo dispatch, production reachable type graph and enumeration-order conformance" }
                : new[] { "DeclaredProperties", "DeclaredEvents", "DeclaredMembers", "generic-variable interface signatures, generic-parameter/array/custom TypeInfo dispatch" },
            applicationSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(application))),
            generatorSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(NativeTypeMetadataProbe).Assembly.Location))),
            inputSha256 = paths.Append(typeof(object).Assembly.Location).Distinct()
                .ToDictionary(path => path, path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))))
        }, new JsonSerializerOptions { WriteIndented = true }));

        void AddBound(Type type)
        {
            if (type.IsByRef || type.IsPointer || type.IsFunctionPointer
                || type.ContainsGenericParameters && !type.IsGenericTypeDefinition || !roots.Add(type)) return;
            if (type.HasElementType) AddBound(type.GetElementType()!);
            foreach (var argument in type.GetGenericArguments()) AddBound(argument);
        }
    }

    private static Type[] InterfaceClosure(Type type)
    {
        if (type.IsArray)
        {
            var core = type.BaseType!.Assembly;
            if (core.GetName().Version?.Major != Environment.Version.Major)
                throw new NotSupportedException("Array interface ordering requires a generator host matching the target runtime major version.");
            var element = type.GetElementType()!;
            if (element.IsFunctionPointer)
                throw new NotSupportedException("Function-pointer array interface metadata requires separate CLR conformance.");
            // MLC synthesizes array interfaces in a different order from the
            // CLR. Ask the matching build-time runtime for the same array shape,
            // then substitute the actual target element in its metadata context.
            // No application assembly is loaded or executed, and no interface
            // names or ordering tables are maintained here.
            var witnessElement = element.IsPointer ? typeof(int).MakePointerType() : typeof(object);
            var witness = type.IsSZArray ? witnessElement.MakeArrayType() : witnessElement.MakeArrayType(type.GetArrayRank());
            var ordered = witness.GetInterfaces().Select(contract =>
            {
                var definition = contract.IsConstructedGenericType ? contract.GetGenericTypeDefinition() : contract;
                var target = core.GetType(definition.FullName!, throwOnError: true)!;
                return contract.IsConstructedGenericType ? target.MakeGenericType(element) : target;
            }).ToArray();
            if (!ordered.ToHashSet().SetEquals(MetadataInterfaceClosure(type)))
                throw new NotSupportedException("The generator runtime and target metadata disagree on the array interface contract.");
            return ordered;
        }
        return MetadataInterfaceClosure(type);
    }

    private static Type[] MetadataInterfaceClosure(Type type)
    {
        var result = new List<Type>();
        // MetadataLoadContext synthesizes generic SZ-array interfaces even for
        // pointer elements. Those are illegal CLI generic arguments; the CLR
        // exposes only Array's own interfaces for these legal array shapes.
        var pointerArray = type.IsArray && type.GetElementType()!.IsPointer;
        var pending = new Queue<Type>(pointerArray ? type.BaseType!.GetInterfaces() : type.GetInterfaces());
        var seen = new HashSet<Type>();
        while (pending.TryDequeue(out var next))
        {
            if (!seen.Add(next)) continue;
            result.Add(next);
            foreach (var inherited in next.GetInterfaces()) pending.Enqueue(inherited);
        }
        return result.ToArray();
    }

    private static string Signature(Type type)
    {
        if (type.IsGenericParameter) return (type.DeclaringMethod == null ? "!" : "!!") + type.GenericParameterPosition;
        if (type.IsArray) return (type.IsSZArray ? "S(" : "A" + type.GetArrayRank() + "(") + Signature(type.GetElementType()!) + ")";
        var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        return "D" + Convert.ToBase64String(Encoding.UTF8.GetBytes(definition.Assembly.GetName().Name!)) + "#"
            + definition.MetadataToken.ToString("X8") + "(" + string.Join(",", type.GetGenericArguments().Select(Signature)) + ")";
    }
}

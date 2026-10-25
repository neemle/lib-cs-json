// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Text;

namespace Neemle.DBAL.Tools;

internal static class NativeAttributeQueryEmitter
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    internal sealed record Summary(IReadOnlyList<NativeAttributeQueryInference.Request> Queries, IReadOnlyList<string> ModelOwners, int Factories, IReadOnlyList<Type> ReceiverOwners);

    internal static Summary Emit(MetadataLoadContext context, IEnumerable<Assembly> inputs, IEnumerable<Type> inferred,
        ModuleBuilder module, TypeBuilder owner)
    {
        var assemblies = inputs.Distinct().ToArray();
        var shapes = inferred.ToArray();
        var parameterOwners = shapes.Where(type => !type.HasElementType && !type.IsGenericParameter && type.AssemblyQualifiedName != null)
            .Select(Normalize).Distinct().ToArray();
        var typeOwners = shapes.Where(type => !type.IsGenericParameter && type.AssemblyQualifiedName != null)
            .Select(Normalize).Distinct().OrderBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal).ToArray();
        var queries = NativeAttributeQueryInference.Infer(context, assemblies, shapes);
        var core = context.CoreAssembly!;
        var attribute = core.GetType("System.Attribute", true)!;
        var attributeArray = attribute.MakeArrayType();
        var typeType = core.GetType("System.Type", true)!;
        var memberInfo = core.GetType("System.Reflection.MemberInfo", true)!;
        var parameterInfo = core.GetType("System.Reflection.ParameterInfo", true)!;
        var fieldInfo = core.GetType("System.Reflection.FieldInfo", true)!;
        var propertyInfo = core.GetType("System.Reflection.PropertyInfo", true)!;
        var methodInfo = core.GetType("System.Reflection.MethodInfo", true)!;
        var assemblyType = core.GetType("System.Reflection.Assembly", true)!;
        var obj = core.GetType("System.Object", true)!;
        var boolean = core.GetType("System.Boolean", true)!;

        var typeFromHandle = NativePropertyAccessorEmitter.MethodForEmission(typeType.GetMethod("GetTypeFromHandle")!);
        var getAssembly = NativePropertyAccessorEmitter.MethodForEmission(typeType.GetProperty("Assembly")!.GetMethod!);
        var getType = NativePropertyAccessorEmitter.MethodForEmission(obj.GetMethod("GetType")!);
        var declaringType = NativePropertyAccessorEmitter.MethodForEmission(memberInfo.GetProperty("DeclaringType")!.GetMethod!);
        var metadataEquals = NativePropertyAccessorEmitter.MethodForEmission(memberInfo.GetMethod("HasSameMetadataDefinitionAs")!);
        var parameterMember = NativePropertyAccessorEmitter.MethodForEmission(parameterInfo.GetProperty("Member")!.GetMethod!);
        var position = NativePropertyAccessorEmitter.MethodForEmission(parameterInfo.GetProperty("Position")!.GetMethod!);
        var protocol = core.GetType("Neemle.DBAL.Runtime.NativeMemberHandles", true)!;
        var marker = NativePropertyAccessorEmitter.ConstructorForEmission(core.GetType("Neemle.DBAL.Runtime.NativeMemberHandleAttribute", true)!
            .GetConstructors(Declared).Single());
        var records = new Dictionary<(Type, int), FieldBuilder>();
        var factories = new Dictionary<string, MethodBuilder>(StringComparer.Ordinal);
        using var inheritance = new NativeAttributeInheritance(context);
        var modelOwners = Owners(context, assemblies).OrderBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal).ToArray();
        var representatives = assemblies.Select(assembly => assembly.GetTypes().FirstOrDefault(type => type.AssemblyQualifiedName != null))
            .Where(type => type != null).Cast<Type>().ToArray();
        var typeOwnerInventories = new Dictionary<string, MethodBuilder>(StringComparer.Ordinal);
        var methodOwnerInventories = new Dictionary<string, MethodBuilder>(StringComparer.Ordinal);
        var derivedOwnerInventories = new Dictionary<string, Type[]>(StringComparer.Ordinal);
        // JSON source-generated contexts advertise serialized values independently
        // of EF/model and ordinary reflection descriptor roots. Keep this domain
        // local to attributes from the selected canonical JSON implementation.
        var json = assemblies.SingleOrDefault(assembly => assembly.GetName().Name == "System.Text.Json");
        Type[] jsonOwners = json == null || !queries.Any(query => query.Receiver is "System.Type" or "System.Reflection.FieldInfo" && query.Attribute.Assembly == json)
            ? [] : NativeJsonAttributeOwners.Infer(json, assemblies, shapes)
            .Select(Normalize).Distinct().ToArray();
        var currentDefinitionOnly = false;
        foreach (var definitionOnly in new[] { false, true })
        {
        currentDefinitionOnly = definitionOnly;
        var method = owner.DefineMethod(definitionOnly ? "IsDefined" : "ReadAttributes", MethodAttributes.Public | MethodAttributes.Static,
            definitionOnly ? boolean : attributeArray, definitionOnly ? [obj, typeType, boolean] : [obj, typeType, boolean, boolean]);
        var il = method.GetILGenerator();
        var elementMember = il.DeclareLocal(memberInfo);
        var elementParameter = il.DeclareLocal(parameterInfo);
        var elementType = il.DeclareLocal(typeType);
        var elementAssembly = il.DeclareLocal(assemblyType);
        var source = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Brtrue, source);
        il.Emit(OpCodes.Ldstr, "element"); il.Emit(OpCodes.Newobj, core.GetType("System.ArgumentNullException", true)!.GetConstructor([core.GetType("System.String", true)!])!); il.Emit(OpCodes.Throw);
        il.MarkLabel(source);
        // Custom reflection providers must not impersonate a CLR owner via AsType,
        // MetadataToken or Equals. Their original virtual dispatch remains on JIT.
        var clr = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Callvirt, getType); il.Emit(OpCodes.Callvirt, getAssembly);
        TypeToken(il, attribute); il.Emit(OpCodes.Callvirt, getAssembly); il.Emit(OpCodes.Ceq); il.Emit(OpCodes.Brtrue, clr);
        Fail(il, "The native attribute query requires an inferred CLR owner or an inferred custom-provider override.");
        il.MarkLabel(clr);
        foreach (var query in queries.Where(query => query.DefinitionOnly == definitionOnly))
        {
            var receiverKey = string.Join("\n", query.ReceiverBases.Select(type => type.AssemblyQualifiedName));
            var derivedOwners = DerivedOwners(query.ReceiverBases, receiverKey);
            var isJsonTypeQuery = query.Receiver == "System.Type" && query.Attribute.Assembly == json;
            var isJsonFieldQuery = query.Receiver == "System.Reflection.FieldInfo" && query.Attribute.Assembly == json;
            var queryTypeOwners = typeOwners.Concat(derivedOwners).Concat(isJsonTypeQuery ? jsonOwners : []).Distinct()
                .OrderBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal).ToArray();
            var queryMemberOwners = parameterOwners.Concat(derivedOwners)
                .Concat(isJsonFieldQuery ? jsonOwners.Where(type => type.IsEnum) : []).Where(type => !type.HasElementType).Distinct()
                .OrderBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal).ToArray();
            var next = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_1); TypeToken(il, query.Attribute); il.Emit(OpCodes.Ceq); il.Emit(OpCodes.Brfalse, next);
            if (query.Receiver == "System.Reflection.Assembly")
            {
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Isinst, assemblyType); il.Emit(OpCodes.Stloc, elementAssembly);
                il.Emit(OpCodes.Ldloc, elementAssembly); il.Emit(OpCodes.Brfalse, next);
                foreach (var representative in representatives)
                {
                    var other = il.DefineLabel();
                    il.Emit(OpCodes.Ldloc, elementAssembly); TypeToken(il, representative); il.Emit(OpCodes.Callvirt, getAssembly);
                    il.Emit(OpCodes.Ceq); il.Emit(OpCodes.Brfalse, other);
                    Return(il, query.Attribute, representative.Assembly, false);
                    il.MarkLabel(other);
                }
                Fail(il, "The native attribute assembly was not in the verified compiler input universe.");
            }
            else if (query.Receiver == "System.Reflection.ParameterInfo")
            {
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Isinst, parameterInfo); il.Emit(OpCodes.Stloc, elementParameter);
                il.Emit(OpCodes.Ldloc, elementParameter); il.Emit(OpCodes.Brfalse, next);
                il.Emit(OpCodes.Ldloc, elementParameter); il.Emit(OpCodes.Callvirt, parameterMember); il.Emit(OpCodes.Stloc, elementMember);
                // Parameter queries in EF evaluate actual query methods. Enumerate matching
                // metadata from the verified closed input universe, not all attribute constructors.
                foreach (var type in parameterOwners)
                    foreach (var member in Members(type))
                        foreach (var parameter in NativeAttributeParameters.Enumerate(member))
                        {
                            var plain = inheritance.Plan(parameter, query.Attribute, false);
                            var inherited = inheritance.Plan(parameter, query.Attribute, true);
                            if (plain.Count == 0 && inherited.Count == 0) continue;
                            var other = il.DefineLabel();
                            il.Emit(OpCodes.Ldloc, elementParameter); il.Emit(OpCodes.Callvirt, position); il.Emit(OpCodes.Ldc_I4, parameter.Position); il.Emit(OpCodes.Bne_Un, other);
                            il.Emit(OpCodes.Ldloc, elementMember); MemberRecord(il, member); il.Emit(OpCodes.Callvirt, metadataEquals); il.Emit(OpCodes.Brfalse, other);
                            ReturnInheritance(il, query.Attribute, parameter, plain, inherited);
                            il.MarkLabel(other);
                        }
                // An empty result is valid only for a CLR declaration whose complete
                // parameters were inventoried. An input assembly alone proves no such closure.
                il.Emit(OpCodes.Ldloc, elementMember); il.Emit(OpCodes.Callvirt, declaringType); il.Emit(OpCodes.Stloc, elementType);
                var hasParameterOwner = il.DefineLabel();
                il.Emit(OpCodes.Ldloc, elementType); il.Emit(OpCodes.Brtrue, hasParameterOwner);
                Fail(il, "A native global parameter declaration was not inferred.");
                il.MarkLabel(hasParameterOwner);
                NormalizeType(il, elementType);
                foreach (var parameterOwner in parameterOwners)
                {
                    var other = il.DefineLabel();
                    il.Emit(OpCodes.Ldloc, elementType); TypeToken(il, parameterOwner); il.Emit(OpCodes.Ceq); il.Emit(OpCodes.Brfalse, other);
                    Empty(il, query.Attribute);
                    il.MarkLabel(other);
                }
                Fail(il, "The native attribute parameter was not in the verified compiler input universe.");
            }
            else if (query.Receiver == "System.Type")
            {
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Isinst, typeType); il.Emit(OpCodes.Stloc, elementType);
                il.Emit(OpCodes.Ldloc, elementType); il.Emit(OpCodes.Brfalse, next);
                NormalizeType(il, elementType);
                // Type-only queries include inferred projection and prebuilt owners.
                // They must not authorize querying those owners' unrelated members.
                foreach (var type in queryTypeOwners)
                {
                    var plain = inheritance.Plan(type, query.Attribute, false);
                    var inherited = inheritance.Plan(type, query.Attribute, true);
                    if (plain.Count == 0 && inherited.Count == 0) continue;
                    var other = il.DefineLabel();
                    il.Emit(OpCodes.Ldloc, elementType); TypeToken(il, type); il.Emit(OpCodes.Ceq); il.Emit(OpCodes.Brfalse, other);
                    ReturnInheritance(il, query.Attribute, type, plain, inherited);
                    il.MarkLabel(other);
                }
                // Share the complete empty-result inventory across typed queries,
                // rather than repeating every empty owner for every attribute.
                var knownEmpty = il.DefineLabel();
                il.Emit(OpCodes.Ldloc, elementType); il.Emit(OpCodes.Call, TypeOwnerInventory(queryTypeOwners, receiverKey + (isJsonTypeQuery ? "\njson-contracts" : ""))); il.Emit(OpCodes.Brtrue, knownEmpty);
                Fail(il, "The native attribute type has no inferred type or projection owner contract.");
                il.MarkLabel(knownEmpty);
                Empty(il, query.Attribute);
            }
            else if (query.Receiver == "System.Reflection.MethodInfo")
            {
                // Method-only callers (for example expression formatting) use actual
                // inferred query/token owners, without widening EF model member access.
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Isinst, methodInfo); il.Emit(OpCodes.Stloc, elementMember);
                il.Emit(OpCodes.Ldloc, elementMember); il.Emit(OpCodes.Brfalse, next);
                il.Emit(OpCodes.Ldloc, elementMember); il.Emit(OpCodes.Callvirt, declaringType); il.Emit(OpCodes.Stloc, elementType);
                var hasMethodOwner = il.DefineLabel();
                il.Emit(OpCodes.Ldloc, elementType); il.Emit(OpCodes.Brtrue, hasMethodOwner);
                Fail(il, "A native global method declaration was not inferred.");
                il.MarkLabel(hasMethodOwner);
                NormalizeType(il, elementType);
                foreach (var type in queryMemberOwners)
                    foreach (var candidate in type.GetMethods(Declared))
                    {
                        var plain = inheritance.Plan(candidate, query.Attribute, false);
                        var inherited = inheritance.Plan(candidate, query.Attribute, true);
                        if (plain.Count == 0 && inherited.Count == 0) continue;
                        var other = il.DefineLabel();
                        il.Emit(OpCodes.Ldloc, elementMember); MemberRecord(il, candidate); il.Emit(OpCodes.Callvirt, metadataEquals); il.Emit(OpCodes.Brfalse, other);
                        ReturnInheritance(il, query.Attribute, candidate, plain, inherited);
                        il.MarkLabel(other);
                    }
                var knownEmpty = il.DefineLabel();
                il.Emit(OpCodes.Ldloc, elementType); il.Emit(OpCodes.Call, MethodOwnerInventory(queryMemberOwners, receiverKey)); il.Emit(OpCodes.Brtrue, knownEmpty);
                Fail(il, "The native attribute method has no inferred query or token owner contract.");
                il.MarkLabel(knownEmpty);
                Empty(il, query.Attribute);
            }
            else if (query.Receiver == "System.Reflection.PropertyInfo")
            {
                // Raw PropertyInfo attribute APIs ignore inheritance, including the
                // IsDefined operation used by TraceLogging. Do not construct attributes
                // merely to answer a metadata-only definition test.
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Isinst, propertyInfo); il.Emit(OpCodes.Stloc, elementMember);
                il.Emit(OpCodes.Ldloc, elementMember); il.Emit(OpCodes.Brfalse, next);
                il.Emit(OpCodes.Ldloc, elementMember); il.Emit(OpCodes.Callvirt, declaringType); il.Emit(OpCodes.Stloc, elementType);
                var hasPropertyOwner = il.DefineLabel();
                il.Emit(OpCodes.Ldloc, elementType); il.Emit(OpCodes.Brtrue, hasPropertyOwner);
                Fail(il, "A native global property declaration was not inferred.");
                il.MarkLabel(hasPropertyOwner);
                NormalizeType(il, elementType);
                foreach (var type in parameterOwners)
                    foreach (var candidate in type.GetProperties(Declared))
                    {
                        var plain = inheritance.Plan(candidate, query.Attribute, false);
                        if (plain.Count == 0) continue;
                        var other = il.DefineLabel();
                        il.Emit(OpCodes.Ldloc, elementMember); MemberRecord(il, candidate); il.Emit(OpCodes.Callvirt, metadataEquals); il.Emit(OpCodes.Brfalse, other);
                        ReturnPlan(il, query.Attribute, plain);
                        il.MarkLabel(other);
                    }
                var knownEmpty = il.DefineLabel();
                il.Emit(OpCodes.Ldloc, elementType); il.Emit(OpCodes.Call, MethodOwnerInventory(parameterOwners, "")); il.Emit(OpCodes.Brtrue, knownEmpty);
                Fail(il, "The native attribute property has no inferred declaring owner contract.");
                il.MarkLabel(knownEmpty);
                Empty(il, query.Attribute);
            }
            else if (query.Receiver == "System.Reflection.FieldInfo")
            {
                // EnumConverter queries fields returned by its reflection type provider.
                // This contract inventories actual inferred field owners independently
                // from EF model/context/migration MemberInfo contracts.
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Isinst, fieldInfo); il.Emit(OpCodes.Stloc, elementMember);
                il.Emit(OpCodes.Ldloc, elementMember); il.Emit(OpCodes.Brfalse, next);
                il.Emit(OpCodes.Ldloc, elementMember); il.Emit(OpCodes.Callvirt, declaringType); il.Emit(OpCodes.Stloc, elementType);
                var hasFieldOwner = il.DefineLabel();
                il.Emit(OpCodes.Ldloc, elementType); il.Emit(OpCodes.Brtrue, hasFieldOwner);
                Fail(il, "A native global field declaration was not inferred.");
                il.MarkLabel(hasFieldOwner);
                NormalizeType(il, elementType);
                foreach (var type in queryMemberOwners)
                {
                    var otherType = il.DefineLabel();
                    il.Emit(OpCodes.Ldloc, elementType); TypeToken(il, type); il.Emit(OpCodes.Ceq); il.Emit(OpCodes.Brfalse, otherType);
                    foreach (var field in type.GetFields(Declared))
                    {
                        var plain = inheritance.Plan(field, query.Attribute, false);
                        var inherited = inheritance.Plan(field, query.Attribute, true);
                        if (plain.Count == 0 && inherited.Count == 0) continue;
                        var other = il.DefineLabel();
                        il.Emit(OpCodes.Ldloc, elementMember); MemberRecord(il, field); il.Emit(OpCodes.Callvirt, metadataEquals); il.Emit(OpCodes.Brfalse, other);
                        ReturnInheritance(il, query.Attribute, field, plain, inherited);
                        il.MarkLabel(other);
                    }
                    Empty(il, query.Attribute);
                    il.MarkLabel(otherType);
                }
                Fail(il, "The native attribute field was not in the verified compiler input universe.");
            }
            else
            {
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Isinst, memberInfo); il.Emit(OpCodes.Stloc, elementMember);
                il.Emit(OpCodes.Ldloc, elementMember); il.Emit(OpCodes.Brfalse, next);
                var notType = il.DefineLabel();
                var haveType = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Isinst, typeType); il.Emit(OpCodes.Stloc, elementType);
                il.Emit(OpCodes.Ldloc, elementType); il.Emit(OpCodes.Brfalse, notType);
                NormalizeType(il, elementType); il.Emit(OpCodes.Br, haveType);
                il.MarkLabel(notType);
                il.Emit(OpCodes.Ldloc, elementMember); il.Emit(OpCodes.Callvirt, declaringType); il.Emit(OpCodes.Stloc, elementType);
                NormalizeType(il, elementType);
                il.MarkLabel(haveType);
                foreach (var type in modelOwners)
                {
                    var otherType = il.DefineLabel();
                    il.Emit(OpCodes.Ldloc, elementType); TypeToken(il, type); il.Emit(OpCodes.Ceq); il.Emit(OpCodes.Brfalse, otherType);
                    var notTypeQuery = il.DefineLabel();
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Isinst, typeType); il.Emit(OpCodes.Brfalse, notTypeQuery);
                    ReturnInheritance(il, query.Attribute, type, inheritance.Plan(type, query.Attribute, false), inheritance.Plan(type, query.Attribute, true));
                    il.MarkLabel(notTypeQuery);
                    foreach (var member in Members(type))
                    {
                        var plain = inheritance.Plan(member, query.Attribute, false);
                        var inherited = inheritance.Plan(member, query.Attribute, true);
                        if (plain.Count == 0 && inherited.Count == 0) continue;
                        var other = il.DefineLabel();
                        il.Emit(OpCodes.Ldloc, elementMember); MemberRecord(il, member); il.Emit(OpCodes.Callvirt, metadataEquals); il.Emit(OpCodes.Brfalse, other);
                        ReturnInheritance(il, query.Attribute, member, plain, inherited);
                        il.MarkLabel(other);
                    }
                    Empty(il, query.Attribute);
                    il.MarkLabel(otherType);
                }
                Fail(il, "The native attribute member has no inferred model/context/migration owner contract.");
            }
            il.MarkLabel(next);
        }
        Fail(il, "The closed native attribute query was not inferred from a canonical helper call.");
        }
        return new Summary(queries, modelOwners.Select(type => type.AssemblyQualifiedName!).ToArray(), factories.Count,
            derivedOwnerInventories.Values.SelectMany(types => types).Distinct().OrderBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal).ToArray());

        void TypeToken(ILGenerator body, Type type)
        { body.Emit(OpCodes.Ldtoken, NativeEmissionTypeNames.ForEmission(type)); body.Emit(OpCodes.Call, typeFromHandle); }

        Type[] DerivedOwners(IReadOnlyList<Type> bases, string key)
        {
            if (bases.Count == 0) return [];
            if (derivedOwnerInventories.TryGetValue(key, out var existing)) return existing;
            // A closed framework receiver base closes runtime GetType() dispatch.
            // Only this query gains its selected-input subclasses; model/member and
            // unrelated typed-attribute domains are not expanded.
            var matches = assemblies.SelectMany(assembly => assembly.GetTypes())
                .Where(type => !type.HasElementType && !type.IsGenericParameter
                    && type.AssemblyQualifiedName != null && bases.Any(parent => parent.IsAssignableFrom(type)))
                .Select(Normalize).Distinct().OrderBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal).ToArray();
            derivedOwnerInventories.Add(key, matches);
            return matches;
        }

        MethodBuilder TypeOwnerInventory(IReadOnlyList<Type> typesForQuery, string key)
        {
            if (typeOwnerInventories.TryGetValue(key, out var existing)) return existing;
            var inventory = owner.DefineMethod("HasInferredAttributeTypeOwner_" + typeOwnerInventories.Count, MethodAttributes.Private | MethodAttributes.Static,
                boolean, [typeType]);
            var code = inventory.GetILGenerator();
            foreach (var type in typesForQuery)
            {
                var other = code.DefineLabel();
                code.Emit(OpCodes.Ldarg_0); TypeToken(code, type); code.Emit(OpCodes.Ceq); code.Emit(OpCodes.Brfalse, other);
                code.Emit(OpCodes.Ldc_I4_1); code.Emit(OpCodes.Ret);
                code.MarkLabel(other);
            }
            code.Emit(OpCodes.Ldc_I4_0); code.Emit(OpCodes.Ret);
            typeOwnerInventories.Add(key, inventory);
            return inventory;
        }

        MethodBuilder MethodOwnerInventory(IReadOnlyList<Type> typesForQuery, string key)
        {
            if (methodOwnerInventories.TryGetValue(key, out var existing)) return existing;
            var inventory = owner.DefineMethod("HasInferredAttributeMethodOwner_" + methodOwnerInventories.Count, MethodAttributes.Private | MethodAttributes.Static,
                boolean, [typeType]);
            var code = inventory.GetILGenerator();
            foreach (var type in typesForQuery)
            {
                var other = code.DefineLabel();
                code.Emit(OpCodes.Ldarg_0); TypeToken(code, type); code.Emit(OpCodes.Ceq); code.Emit(OpCodes.Brfalse, other);
                code.Emit(OpCodes.Ldc_I4_1); code.Emit(OpCodes.Ret);
                code.MarkLabel(other);
            }
            code.Emit(OpCodes.Ldc_I4_0); code.Emit(OpCodes.Ret);
            methodOwnerInventories.Add(key, inventory);
            return inventory;
        }

        void NormalizeType(ILGenerator body, LocalBuilder local)
        {
            var done = body.DefineLabel();
            body.Emit(OpCodes.Ldloc, local); body.Emit(OpCodes.Callvirt, NativePropertyAccessorEmitter.MethodForEmission(typeType.GetProperty("IsConstructedGenericType")!.GetMethod!)); body.Emit(OpCodes.Brfalse, done);
            body.Emit(OpCodes.Ldloc, local); body.Emit(OpCodes.Callvirt, NativePropertyAccessorEmitter.MethodForEmission(typeType.GetMethod("GetGenericTypeDefinition")!)); body.Emit(OpCodes.Stloc, local);
            body.MarkLabel(done);
        }

        void Empty(ILGenerator body, Type requested)
        { body.Emit(OpCodes.Ldc_I4_0); if (!currentDefinitionOnly) body.Emit(OpCodes.Newarr, requested); body.Emit(OpCodes.Ret); }

        void Return(ILGenerator body, Type requested, object element, bool inherit)
            => ReturnPlan(body, requested, inheritance.Plan(element, requested, inherit));

        void ReturnInheritance(ILGenerator body, Type requested, object element,
            IReadOnlyList<NativeAttributeInheritance.Construction> plain, IReadOnlyList<NativeAttributeInheritance.Construction> inherited)
        {
            var noInheritance = body.DefineLabel();
            body.Emit(OpCodes.Ldarg_2); body.Emit(OpCodes.Brfalse, noInheritance);
            ReturnPlan(body, requested, inherited);
            body.MarkLabel(noInheritance);
            ReturnPlan(body, requested, plain);
        }

        void ReturnPlan(ILGenerator body, Type requested, IReadOnlyList<NativeAttributeInheritance.Construction> plan)
        {
            if (currentDefinitionOnly)
            {
                body.Emit(plan.Any(step => step.Include) ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                body.Emit(OpCodes.Ret);
                return;
            }
            // Attribute arguments can contain identically named types from different assemblies.
            // Do not merge factories using CustomAttributeData.ToString(), which loses that identity.
            var key = plan.Count == 0 ? requested.AssemblyQualifiedName + "|empty" : "plan:" + factories.Count;
            if (!factories.TryGetValue(key, out var factory))
            {
                factory = owner.DefineMethod("AttributeFactory_" + factories.Count, MethodAttributes.Private | MethodAttributes.Static, attributeArray, [boolean]);
                var code = factory.GetILGenerator();
                var values = code.DeclareLocal(NativeEmissionTypeNames.ForEmission(requested.MakeArrayType()));
                var count = plan.Count(step => step.Include);
                code.Emit(OpCodes.Ldc_I4, count); code.Emit(OpCodes.Newarr, NativeEmissionTypeNames.ForEmission(requested)); code.Emit(OpCodes.Stloc, values);
                var index = 0;
                var constructed = code.DeclareLocal(attribute);
                foreach (var step in plan)
                {
                    // Named setters have CLR-compatible exception regions. Their entry
                    // stack must be empty: retain neither array nor index across the call.
                    NativeAttributeConstructionEmitter.Emit(code, step.Data, typeType, reflectionQuery: true);
                    if (step.Include)
                    {
                        code.Emit(OpCodes.Stloc, constructed);
                        code.Emit(OpCodes.Ldloc, values); code.Emit(OpCodes.Ldc_I4, index++);
                        code.Emit(OpCodes.Ldloc, constructed); code.Emit(OpCodes.Stelem_Ref);
                    }
                    else code.Emit(OpCodes.Pop);
                }
                if (count > 1)
                {
                    var multiple = code.DefineLabel();
                    code.Emit(OpCodes.Ldarg_0); code.Emit(OpCodes.Brfalse, multiple);
                    code.Emit(OpCodes.Ldloc, values); code.Emit(OpCodes.Ldc_I4_0); code.Emit(OpCodes.Ldelem_Ref);
                    var error = core.GetType("System.ThrowHelper", true)!.GetMethod("GetAmbiguousMatchException", Declared, null, [attribute], null)!;
                    code.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(error)); code.Emit(OpCodes.Throw);
                    code.MarkLabel(multiple);
                }
                code.Emit(OpCodes.Ldloc, values); code.Emit(OpCodes.Ret);
                factories.Add(key, factory);
            }
            body.Emit(OpCodes.Ldarg_3); body.Emit(OpCodes.Call, factory); body.Emit(OpCodes.Ret);
        }

        void MemberRecord(ILGenerator body, MemberInfo member)
        {
            var kind = member switch { PropertyInfo => 1, EventInfo => 2, ConstructorInfo => 6, FieldInfo => 7, MethodInfo => 5, _ => throw new NotSupportedException("Unsupported native attribute member: " + member) };
            var key = (member.DeclaringType!, member.MetadataToken);
            if (!records.TryGetValue(key, out var field))
            {
                field = owner.DefineInitializedData("AttributeMemberRecord_" + records.Count, new byte[16], FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly);
                var blob = new BlobBuilder(); blob.WriteUInt16(1); blob.WriteSerializedString(member.DeclaringType!.AssemblyQualifiedName!);
                blob.WriteInt32(member.MetadataToken); blob.WriteInt32(kind); blob.WriteSerializedString(""); blob.WriteUInt16(0);
                field.SetCustomAttribute(marker, blob.ToArray()); records.Add(key, field);
            }
            body.Emit(OpCodes.Ldsflda, field); body.Emit(OpCodes.Conv_U);
            var name = kind switch { 1 => "PropertyFromRecord", 2 => "EventFromRecord", 5 => "MethodFromRecord", 6 => "ConstructorFromRecord", _ => "FieldFromRecord" };
            body.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(protocol.GetMethod(name, Declared)!));
        }

        void Fail(ILGenerator body, string message)
        { body.Emit(OpCodes.Ldstr, message); body.Emit(OpCodes.Newobj, core.GetType("System.NotSupportedException", true)!.GetConstructor([core.GetType("System.String", true)!])!); body.Emit(OpCodes.Throw); }
    }

    private static Type Normalize(Type type) => type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;

    private static HashSet<Type> Owners(MetadataLoadContext context, IEnumerable<Assembly> assemblies)
    {
        var owners = new HashSet<Type>();
        var selected = assemblies.ToArray();
        foreach (var assembly in selected)
        {
            foreach (var attribute in assembly.GetCustomAttributesData())
                if (attribute.AttributeType.FullName == "System.Reflection.AssemblyMetadataAttribute"
                    && attribute.ConstructorArguments[0].Value as string == "Neemle.DBAL.AttributeOwnerContracts"
                    && attribute.ConstructorArguments[1].Value is string encoded)
                    foreach (var name in Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        Add(Type.GetType(name, context.LoadFromAssemblyName, static (assembly, name, ignoreCase) => assembly?.GetType(name, true, ignoreCase), true)!);
            foreach (var type in assembly.GetTypes())
                if (Base(type, "Microsoft.EntityFrameworkCore.Migrations.Migration", "Microsoft.EntityFrameworkCore.Relational")
                    || Base(type, "Microsoft.EntityFrameworkCore.Infrastructure.ModelSnapshot", "Microsoft.EntityFrameworkCore.Relational")) Add(type);
        }
        return owners;

        void Add(Type type)
        {
            type = Normalize(type);
            if (type.AssemblyQualifiedName == null || type.HasElementType || type.IsGenericParameter || !owners.Add(type)) return;
            if (type.BaseType is { } parent) Add(parent);
            foreach (var contract in type.GetInterfaces()) Add(contract);
        }
    }

    private static bool Base(Type type, string name, string assembly)
    {
        for (var current = type.BaseType; current != null; current = current.BaseType)
            if (current.FullName == name && current.Assembly.GetName().Name == assembly) return true;
        return false;
    }

    private static IEnumerable<MemberInfo> Members(Type type) => type.GetMembers(Declared)
        .Where(member => member is MethodInfo or ConstructorInfo or PropertyInfo or EventInfo or FieldInfo);

}

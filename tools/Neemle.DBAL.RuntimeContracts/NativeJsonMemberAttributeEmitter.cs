// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;

namespace Neemle.DBAL.Tools;

/// <summary>Constructs JSON attributes in the advertised serialization domain only.</summary>
internal static class NativeJsonMemberAttributeEmitter
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    internal sealed record Summary(IReadOnlyList<string> SerializedOwners, IReadOnlyList<string> HierarchyOwners, int Members, int Factories, int Attributes, IReadOnlyList<ConstructorRole> ConstructorReads, IReadOnlyList<OwnerReadRole> ReadRoles);
    internal sealed record ConstructorRole(string Owner, int? ConstructorToken, string Reason, bool Unsupported);
    internal sealed record OwnerReadRole(string Owner, bool ObjectMembers, bool FSharpRawType, bool RawType, string Reason, bool Unsupported);

    internal static Summary? Emit(MetadataLoadContext context, IEnumerable<Assembly> inputs, IEnumerable<Type> inferred,
        ModuleBuilder module, TypeBuilder owner)
    {
        var assemblies = inputs.Distinct().ToArray();
        var json = assemblies.SingleOrDefault(assembly => assembly.GetName().Name == "System.Text.Json");
        if (json?.GetType("Neemle.DBAL.Runtime.NativeJsonMemberAttributeQueries") == null) return null;
        var serialized = NativeJsonAttributeOwners.Infer(json, assemblies, inferred).ToArray();
        var core = context.CoreAssembly!;
        var obj = core.GetType("System.Object", true)!;
        var objArray = obj.MakeArrayType();
        var attribute = core.GetType("System.Attribute", true)!;
        var typeType = core.GetType("System.Type", true)!;
        var memberInfo = core.GetType("System.Reflection.MemberInfo", true)!;
        var boolean = core.GetType("System.Boolean", true)!;
        var integer = core.GetType("System.Int32", true)!;
        var text = core.GetType("System.String", true)!;
        var fromHandle = NativePropertyAccessorEmitter.MethodForEmission(typeType.GetMethod("GetTypeFromHandle")!);
        var getType = NativePropertyAccessorEmitter.MethodForEmission(obj.GetMethod("GetType")!);
        var getAssembly = NativePropertyAccessorEmitter.MethodForEmission(typeType.GetProperty("Assembly")!.GetMethod!);
        var declaringType = NativePropertyAccessorEmitter.MethodForEmission(memberInfo.GetProperty("DeclaringType")!.GetMethod!);
        var sameDefinition = NativePropertyAccessorEmitter.MethodForEmission(memberInfo.GetMethod("HasSameMetadataDefinitionAs")!);
        var assignable = NativePropertyAccessorEmitter.MethodForEmission(typeType.GetMethod("IsAssignableFrom", [typeType])!);
        var protocol = core.GetType("Neemle.DBAL.Runtime.NativeMemberHandles", true)!;
        var marker = NativePropertyAccessorEmitter.ConstructorForEmission(core.GetType("Neemle.DBAL.Runtime.NativeMemberHandleAttribute", true)!.GetConstructors(Declared).Single());
        var records = new Dictionary<(Type, int), FieldBuilder>();
        var hierarchy = new HashSet<Type>(serialized);
        foreach (var root in serialized)
            foreach (var parent in Hierarchy(root)) hierarchy.Add(parent);
        var types = hierarchy.OrderBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal).ToArray();
        var constructorPolicy = new NativeJsonConstructorMetadata(context, json);
        var selections = serialized.ToDictionary(type => type, constructorPolicy.Select);
        var readRoles = serialized.ToDictionary(type => type, constructorPolicy.Roles);
        var rawTypes = readRoles.Where(pair => pair.Value.FSharpTypeAttributes).Select(pair => pair.Key).ToHashSet();
        var memberRoles = new Dictionary<(Type Type, int Token), (MemberInfo Member, bool CanSerialize, bool Raw)>();
        foreach (var root in serialized.Where(type => readRoles[type].ObjectMembers))
        {
            var selected = selections[root].Constructor;
            var checkRequired = selected?.GetCustomAttributesData().Any(data => data.AttributeType.FullName == "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute") != true;
            var ignored = new Dictionary<string, Type>(StringComparer.Ordinal);
            foreach (var current in Hierarchy(root))
            {
                if (checkRequired) rawTypes.Add(current);
                foreach (var (member, canSerialize) in Members(current).Where(item => item.Member is not ConstructorInfo))
                {
                    if (member is PropertyInfo property && Virtual(property) && ignored.TryGetValue(property.Name, out var ignoredType) && property.PropertyType == ignoredType) continue;
                    var raw = canSerialize && !InvalidMemberType(member) && checkRequired && HasRequiredMarker(current)
                        && !member.GetCustomAttributesData().Any(data => data.AttributeType == json.GetType("System.Text.Json.Serialization.JsonRequiredAttribute"));
                    var key = (current, member.MetadataToken);
                    if (memberRoles.TryGetValue(key, out var previous)) raw |= previous.Raw;
                    memberRoles[key] = (member, canSerialize, raw);
                    if (canSerialize && member is PropertyInfo candidate && Virtual(candidate) && Ignored(candidate)) ignored[candidate.Name] = candidate.PropertyType;
                }
            }
        }
        using var inheritance = new NativeAttributeInheritance(context);
        var factories = 0;
        var attributes = 0;
        var members = 0;
        var method = owner.DefineMethod("ReadJsonAttributes", MethodAttributes.Public | MethodAttributes.Static,
            objArray, [obj, typeType, boolean, boolean, boolean]);
        var il = method.GetILGenerator();
        var element = il.DeclareLocal(memberInfo);
        var actualType = il.DeclareLocal(typeType);
        var notNull = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Brtrue, notNull); Null(il, "element"); il.MarkLabel(notNull);
        notNull = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Brtrue, notNull); Null(il, "attributeType"); il.MarkLabel(notNull);
        var clr = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Callvirt, getType); il.Emit(OpCodes.Callvirt, getAssembly);
        Token(il, attribute); il.Emit(OpCodes.Callvirt, getAssembly); il.Emit(OpCodes.Ceq); il.Emit(OpCodes.Brtrue, clr);
        Fail(il, "The native JSON attribute query requires an advertised CLR serialization owner; custom reflector virtual dispatch remains available on JIT.");
        il.MarkLabel(clr);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Isinst, memberInfo); il.Emit(OpCodes.Stloc, element);
        var isMember = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, element); il.Emit(OpCodes.Brtrue, isMember); Fail(il, "A native JSON attribute receiver must be a CLR type or member."); il.MarkLabel(isMember);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Isinst, typeType); il.Emit(OpCodes.Stloc, actualType);
        var memberBranch = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, actualType); il.Emit(OpCodes.Brfalse, memberBranch);
        foreach (var type in types.Where(type => selections.ContainsKey(type) || rawTypes.Contains(type)))
        {
            var next = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, actualType); Token(il, type); il.Emit(OpCodes.Ceq); il.Emit(OpCodes.Brfalse, next);
            Return(type, allowRaw: rawTypes.Contains(type), unsupported: readRoles.GetValueOrDefault(type)?.Unsupported == true ? readRoles[type].Reason : null);
            il.MarkLabel(next);
        }
        Fail(il, "The native JSON type has no advertised JsonSerializerContext/JsonTypeInfo serialization contract.");
        il.MarkLabel(memberBranch);
        il.Emit(OpCodes.Ldloc, element); il.Emit(OpCodes.Callvirt, declaringType); il.Emit(OpCodes.Stloc, actualType);
        foreach (var type in types.Where(type => !type.HasElementType))
        {
            var nextType = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, actualType); Token(il, type); il.Emit(OpCodes.Ceq); il.Emit(OpCodes.Brfalse, nextType);
            var selection = selections.GetValueOrDefault(type);
            var selectedConstructor = selection?.Constructor;
            var candidates = memberRoles.Where(pair => pair.Key.Type == type).Select(pair => pair.Value)
                .Concat(selections.ContainsKey(type) && (readRoles[type].ObjectMembers || readRoles[type].Unsupported)
                    ? Members(type).Where(item => item.Member is ConstructorInfo).Select(item => (item.Member, item.CanSerialize, false))
                    : []);
            foreach (var (member, canSerialize, rawMember) in candidates)
            {
                members++;
                var nextMember = il.DefineLabel();
                il.Emit(OpCodes.Ldloc, element); Record(il, member); il.Emit(OpCodes.Callvirt, sameDefinition); il.Emit(OpCodes.Brfalse, nextMember);
                var raw = member is ConstructorInfo constructor ? selectedConstructor?.MetadataToken == constructor.MetadataToken : rawMember;
                var markerOnly = member is ConstructorInfo && !raw ? json.GetType("System.Text.Json.Serialization.JsonConstructorAttribute")
                    : !canSerialize ? json.GetType("System.Text.Json.Serialization.JsonIncludeAttribute") : null;
                Return(member, raw, markerOnly, selection?.Unsupported == true && member is ConstructorInfo ? selection.Reason : null,
                    member is not ConstructorInfo && InvalidMemberType(member)
                        ? new[] { json.GetType("System.Text.Json.Serialization.JsonIncludeAttribute", true)!, json.GetType("System.Text.Json.Serialization.JsonIgnoreAttribute", true)! } : null);
                il.MarkLabel(nextMember);
            }
            Fail(il, readRoles.GetValueOrDefault(type)?.Unsupported == true ? readRoles[type].Reason : "The native JSON member has no inferred serialization-member role.");
            il.MarkLabel(nextType);
        }
        Fail(il, "The native JSON member owner has no advertised serialization contract.");
        return new(serialized.Select(type => type.AssemblyQualifiedName!).ToArray(), types.Select(type => type.AssemblyQualifiedName!).ToArray(), members, factories, attributes, selections.OrderBy(pair => pair.Key.AssemblyQualifiedName, StringComparer.Ordinal).Select(pair => new ConstructorRole(pair.Key.AssemblyQualifiedName!, pair.Value.Constructor?.MetadataToken, pair.Value.Reason, pair.Value.Unsupported)).ToArray(),
            readRoles.OrderBy(pair => pair.Key.AssemblyQualifiedName, StringComparer.Ordinal).Select(pair => new OwnerReadRole(pair.Key.AssemblyQualifiedName!, pair.Value.ObjectMembers, pair.Value.FSharpTypeAttributes, rawTypes.Contains(pair.Key), pair.Value.Reason, pair.Value.Unsupported)).ToArray());

        void Token(ILGenerator body, Type type)
        { body.Emit(OpCodes.Ldtoken, NativeEmissionTypeNames.ForEmission(type)); body.Emit(OpCodes.Call, fromHandle); }

        void Null(ILGenerator body, string name)
        { body.Emit(OpCodes.Ldstr, name); body.Emit(OpCodes.Newobj, NativePropertyAccessorEmitter.ConstructorForEmission(core.GetType("System.ArgumentNullException", true)!.GetConstructor([text])!)); body.Emit(OpCodes.Throw); }

        void Fail(ILGenerator body, string message)
        { body.Emit(OpCodes.Ldstr, message); body.Emit(OpCodes.Newobj, NativePropertyAccessorEmitter.ConstructorForEmission(core.GetType("System.NotSupportedException", true)!.GetConstructor([text])!)); body.Emit(OpCodes.Throw); }

        void Return(object declaration, bool allowRaw, Type? markerOnly = null, string? unsupported = null, IReadOnlyList<Type>? allowed = null)
        {
            var plain = Factory(inheritance.Plan(declaration, attribute, false), allowRaw, markerOnly, unsupported, allowed);
            // Raw property/event enumeration never takes the inherited factory. Only
            // typed static JSON queries traverse accessor ancestry here.
            var inherited = Factory(inheritance.Plan(declaration, attribute, true), allowRaw && declaration is not (PropertyInfo or EventInfo), markerOnly, unsupported, allowed);
            var noInheritance = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Brfalse, noInheritance);
            if (declaration is PropertyInfo or EventInfo)
            {
                // MemberInfo.GetCustomAttributes ignores property/event inheritance;
                // Attribute.GetCustomAttribute walks their accessor ancestry.
                il.Emit(OpCodes.Ldarg_S, (byte)4); il.Emit(OpCodes.Brfalse, noInheritance);
            }
            Call(inherited); il.MarkLabel(noInheritance); Call(plain);
        }

        void Call(MethodBuilder factory)
        { il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_3); il.Emit(OpCodes.Call, factory); il.Emit(OpCodes.Ret); }

        MethodBuilder Factory(IReadOnlyList<NativeAttributeInheritance.Construction> plan, bool allowRaw, Type? markerOnly, string? unsupported, IReadOnlyList<Type>? allowed)
        {
            if (!allowRaw) plan = plan.Where(step => JsonAttribute(step.Data.AttributeType)).ToArray();
            if (markerOnly != null) plan = plan.Where(step => markerOnly.IsAssignableFrom(step.Data.AttributeType)).ToArray();
            if (allowed != null) plan = plan.Where(step => allowed.Any(type => type.IsAssignableFrom(step.Data.AttributeType))).ToArray();
            attributes += plan.Count;
            var factory = owner.DefineMethod("JsonMemberAttributes_" + factories++, MethodAttributes.Private | MethodAttributes.Static, objArray, [typeType, boolean]);
            var code = factory.GetILGenerator();
            if (allowed != null)
            {
                var permitted = code.DefineLabel();
                foreach (var type in allowed) { code.Emit(OpCodes.Ldarg_0); Token(code, type); code.Emit(OpCodes.Ceq); code.Emit(OpCodes.Brtrue, permitted); }
                Fail(code, "Only JSON inclusion/ignore reads were inferred before this invalid member type is rejected."); code.MarkLabel(permitted);
            }
            if (markerOnly != null)
            {
                var markerQuery = code.DefineLabel();
                code.Emit(OpCodes.Ldarg_0); Token(code, markerOnly); code.Emit(OpCodes.Ceq); code.Emit(OpCodes.Brtrue, markerQuery);
                Fail(code, unsupported ?? "Only the JSON inclusion/constructor marker query was inferred for this non-serialization declaration."); code.MarkLabel(markerQuery);
            }
            if (!allowRaw)
            {
                var typed = code.DefineLabel();
                code.Emit(OpCodes.Ldarg_0); Token(code, attribute); code.Emit(OpCodes.Ceq); code.Emit(OpCodes.Brfalse, typed);
                Fail(code, "Raw JSON required-member attributes were not inferred for this declaration."); code.MarkLabel(typed);
            }
            var count = code.DeclareLocal(integer); var index = code.DeclareLocal(integer);
            var values = code.DeclareLocal(objArray); var constructed = code.DeclareLocal(attribute);
            code.Emit(OpCodes.Ldc_I4_0); code.Emit(OpCodes.Stloc, count);
            foreach (var step in plan.Where(step => step.Include))
            {
                var skip = code.DefineLabel(); Filter(code, step.Data.AttributeType, skip);
                code.Emit(OpCodes.Ldloc, count); code.Emit(OpCodes.Ldc_I4_1); code.Emit(OpCodes.Add); code.Emit(OpCodes.Stloc, count); code.MarkLabel(skip);
            }
            code.Emit(OpCodes.Ldloc, count); code.Emit(OpCodes.Newarr, obj); code.Emit(OpCodes.Stloc, values);
            code.Emit(OpCodes.Ldc_I4_0); code.Emit(OpCodes.Stloc, index);
            foreach (var step in plan)
            {
                var skip = code.DefineLabel(); Filter(code, step.Data.AttributeType, skip);
                NativeAttributeConstructionEmitter.Emit(code, step.Data, typeType, reflectionQuery: true);
                if (step.Include)
                {
                    code.Emit(OpCodes.Stloc, constructed); code.Emit(OpCodes.Ldloc, values); code.Emit(OpCodes.Ldloc, index); code.Emit(OpCodes.Ldloc, constructed); code.Emit(OpCodes.Stelem_Ref);
                    code.Emit(OpCodes.Ldloc, index); code.Emit(OpCodes.Ldc_I4_1); code.Emit(OpCodes.Add); code.Emit(OpCodes.Stloc, index);
                }
                else code.Emit(OpCodes.Pop);
                code.MarkLabel(skip);
            }
            var done = code.DefineLabel();
            code.Emit(OpCodes.Ldarg_1); code.Emit(OpCodes.Brfalse, done); code.Emit(OpCodes.Ldloc, count); code.Emit(OpCodes.Ldc_I4_1); code.Emit(OpCodes.Ble, done);
            code.Emit(OpCodes.Ldloc, values); code.Emit(OpCodes.Ldc_I4_0); code.Emit(OpCodes.Ldelem_Ref); code.Emit(OpCodes.Castclass, attribute);
            var error = core.GetType("System.ThrowHelper", true)!.GetMethod("GetAmbiguousMatchException", Declared, null, [attribute], null)!;
            code.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(error)); code.Emit(OpCodes.Throw);
            code.MarkLabel(done); code.Emit(OpCodes.Ldloc, values); code.Emit(OpCodes.Ret);
            return factory;
        }

        void Filter(ILGenerator code, Type actual, Label noMatch)
        { code.Emit(OpCodes.Ldarg_0); Token(code, actual); code.Emit(OpCodes.Callvirt, assignable); code.Emit(OpCodes.Brfalse, noMatch); }

        bool JsonAttribute(Type type)
        {
            for (var current = type; current != null && current != attribute; current = current.BaseType)
                if (current.Assembly == json) return true;
            return false;
        }

        bool HasRequiredMarker(Type type)
            => type.GetCustomAttributesData().Any(data => data.AttributeType.FullName == "System.Runtime.CompilerServices.RequiredMemberAttribute");

        // Matches the selected JSON Common/ReflectionExtensions.GetSortedTypeHierarchy:
        // linear classes and reverse childless-queue topological ordering for interfaces.
        IEnumerable<Type> Hierarchy(Type root)
        {
            if (!root.IsInterface)
            {
                for (var current = root; current != null && current.FullName is not ("System.Object" or "System.ValueType"); current = current.BaseType) yield return current;
                yield break;
            }
            var nodes = new[] { root }.Concat(root.GetInterfaces()).ToArray();
            var edges = nodes.Select(type => type.GetInterfaces().ToHashSet()).ToArray();
            var queue = new Queue<int>(Enumerable.Range(0, nodes.Length).Where(index => edges[index].Count == 0));
            var sorted = new List<Type>();
            while (queue.TryDequeue(out var index))
            {
                sorted.Add(nodes[index]);
                for (var parent = 0; parent < nodes.Length; parent++)
                    if (edges[parent].Remove(nodes[index]) && edges[parent].Count == 0) queue.Enqueue(parent);
            }
            if (sorted.Count != nodes.Length) throw new BadImageFormatException("Cyclic JSON interface hierarchy.");
            for (var index = sorted.Count - 1; index >= 0; index--) yield return sorted[index];
        }

        bool InvalidMemberType(MemberInfo member)
        {
            var type = member is PropertyInfo property ? property.PropertyType : member is FieldInfo field ? field.FieldType : null;
            return type != null && (type == core.GetType("System.Void", true) || type.IsPointer || type.IsByRef || type.IsByRefLike || type.ContainsGenericParameters);
        }

        bool Virtual(PropertyInfo property) => property.GetMethod?.IsVirtual == true || property.SetMethod?.IsVirtual == true;

        bool Ignored(PropertyInfo property)
        {
            var ignore = property.GetCustomAttributesData().FirstOrDefault(data => data.AttributeType == json.GetType("System.Text.Json.Serialization.JsonIgnoreAttribute"));
            if (ignore == null) return false;
            var condition = ignore.NamedArguments.FirstOrDefault(argument => argument.MemberName == "Condition");
            // JsonIgnore's parameterless default is Always; read the selected enum value.
            var always = json.GetType("System.Text.Json.Serialization.JsonIgnoreCondition", true)!.GetField("Always")!.GetRawConstantValue();
            return condition.MemberInfo == null || Equals(condition.TypedValue.Value, always);
        }

        IEnumerable<(MemberInfo Member, bool CanSerialize)> Members(Type type)
        {
            var include = json.GetType("System.Text.Json.Serialization.JsonIncludeAttribute");
            foreach (var constructor in type.GetConstructors(Declared).Where(ctor => !ctor.IsStatic)) yield return (constructor, true);
            foreach (var property in type.GetProperties(Declared).Where(property => (property.GetMethod ?? property.SetMethod)?.IsStatic != true
                && property.GetIndexParameters().Length == 0))
                yield return (property, property.GetMethod?.IsPublic == true || property.SetMethod?.IsPublic == true
                    || property.GetCustomAttributesData().Any(data => data.AttributeType == include));
            foreach (var field in type.GetFields(Declared).Where(field => !field.IsStatic))
                yield return (field, field.IsPublic || field.GetCustomAttributesData().Any(data => data.AttributeType == include));
        }

        void Record(ILGenerator body, MemberInfo member)
        {
            var kind = member is PropertyInfo ? 1 : member is ConstructorInfo ? 6 : 7;
            var key = (member.DeclaringType!, member.MetadataToken);
            if (!records.TryGetValue(key, out var field))
            {
                field = owner.DefineInitializedData("JsonAttributeMemberRecord_" + records.Count, new byte[16], FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly);
                var blob = new BlobBuilder(); blob.WriteUInt16(1); blob.WriteSerializedString(member.DeclaringType!.AssemblyQualifiedName!); blob.WriteInt32(member.MetadataToken); blob.WriteInt32(kind); blob.WriteSerializedString(""); blob.WriteUInt16(0);
                field.SetCustomAttribute(marker, blob.ToArray()); records.Add(key, field);
            }
            body.Emit(OpCodes.Ldsflda, field); body.Emit(OpCodes.Conv_U);
            var name = kind == 1 ? "PropertyFromRecord" : kind == 6 ? "ConstructorFromRecord" : "FieldFromRecord";
            body.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(protocol.GetMethod(name, Declared)!));
        }
    }
}

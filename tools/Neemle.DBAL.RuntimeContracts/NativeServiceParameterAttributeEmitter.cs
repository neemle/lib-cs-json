// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;

namespace Neemle.DBAL.Tools;

/// <summary>Generates eager parameter attribute reads for inferred DI implementations only.</summary>
internal static class NativeServiceParameterAttributeEmitter
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    internal sealed record Summary(int Implementations, int Constructors, int Parameters, int Attributes);

    internal static bool IsPresent(Assembly assembly)
        => assembly.GetName().Name == "Microsoft.Extensions.DependencyInjection"
            && assembly.GetType("Neemle.DBAL.Runtime.NativeServiceParameterAttributes") != null;

    internal static bool HasRetentionBridge(Assembly assembly)
        => assembly.GetName().Name == "Microsoft.EntityFrameworkCore"
            && assembly.GetType("Neemle.DBAL.EntityFrameworkCore.NativeAotServiceTypes")?
                .GetMethod("TryRetainServiceImplementation", PrivateStatic) != null;

    internal static Summary Emit(Assembly core, IEnumerable<Type> implementations, TypeBuilder owner, bool emitRawAttributes)
    {
        var parameterInfo = core.GetType("System.Reflection.ParameterInfo", true)!;
        var memberInfo = core.GetType("System.Reflection.MemberInfo", true)!;
        var constructorInfo = core.GetType("System.Reflection.ConstructorInfo", true)!;
        var attribute = core.GetType("System.Attribute", true)!;
        var type = core.GetType("System.Type", true)!;
        var obj = core.GetType("System.Object", true)!;
        var method = owner.DefineMethod("ReadServiceParameterAttributes", MethodAttributes.Public | MethodAttributes.Static,
            attribute.MakeArrayType(), [parameterInfo]);
        var body = method.GetILGenerator();
        var actual = body.DeclareLocal(memberInfo);
        var getMember = NativePropertyAccessorEmitter.MethodForEmission(parameterInfo.GetProperty("Member")!.GetMethod!);
        var getPosition = NativePropertyAccessorEmitter.MethodForEmission(parameterInfo.GetProperty("Position")!.GetMethod!);
        var getDeclaring = NativePropertyAccessorEmitter.MethodForEmission(memberInfo.GetProperty("DeclaringType")!.GetMethod!);
        var equalsDefinition = NativePropertyAccessorEmitter.MethodForEmission(memberInfo.GetMethod("HasSameMetadataDefinitionAs")!);
        var typeFromHandle = NativePropertyAccessorEmitter.MethodForEmission(type.GetMethod("GetTypeFromHandle")!);
        var getType = NativePropertyAccessorEmitter.MethodForEmission(obj.GetMethod("GetType")!);
        var getAssembly = NativePropertyAccessorEmitter.MethodForEmission(type.GetProperty("Assembly")!.GetMethod!);
        var protocol = core.GetType("Neemle.DBAL.Runtime.NativeMemberHandles", true)!;
        var constructorFromRecord = NativePropertyAccessorEmitter.MethodForEmission(protocol.GetMethod("ConstructorFromRecord", PrivateStatic)!);
        var marker = NativePropertyAccessorEmitter.ConstructorForEmission(core.GetType("Neemle.DBAL.Runtime.NativeMemberHandleAttribute", true)!
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Single());
        var notNull = body.DefineLabel();
        body.Emit(OpCodes.Ldarg_0); body.Emit(OpCodes.Brtrue, notNull);
        body.Emit(OpCodes.Ldstr, "element");
        body.Emit(OpCodes.Newobj, NativePropertyAccessorEmitter.ConstructorForEmission(core.GetType("System.ArgumentNullException", true)!
            .GetConstructor([core.GetType("System.String", true)!])!)); body.Emit(OpCodes.Throw);
        body.MarkLabel(notNull);
        var clr = body.DefineLabel();
        body.Emit(OpCodes.Ldarg_0); body.Emit(OpCodes.Callvirt, getType); body.Emit(OpCodes.Callvirt, getAssembly);
        Token(attribute); body.Emit(OpCodes.Callvirt, getAssembly); body.Emit(OpCodes.Ceq); body.Emit(OpCodes.Brtrue, clr);
        Fail("The native service parameter requires an inferred CLR constructor declaration.");
        body.MarkLabel(clr);
        body.Emit(OpCodes.Ldarg_0); body.Emit(OpCodes.Callvirt, getMember); body.Emit(OpCodes.Stloc, actual);
        var ctor = body.DefineLabel();
        body.Emit(OpCodes.Ldloc, actual); body.Emit(OpCodes.Isinst, constructorInfo); body.Emit(OpCodes.Brtrue, ctor);
        Fail("The native service attribute query requires a constructor parameter.");
        body.MarkLabel(ctor);
        var types = implementations.Where(candidate => !candidate.ContainsGenericParameters).Distinct()
            .OrderBy(candidate => candidate.AssemblyQualifiedName, StringComparer.Ordinal).ToArray();
        EmitRetainedTypes();
        if (!emitRawAttributes)
        {
            Fail("Native service parameter attributes require the matching DependencyInjection adapter.");
            return new(types.Length, 0, 0, 0);
        }
        var constructors = 0; var parameters = 0; var attributes = 0;
        foreach (var implementation in types)
        {
            var nextType = body.DefineLabel();
            body.Emit(OpCodes.Ldloc, actual); body.Emit(OpCodes.Callvirt, getDeclaring); Token(implementation);
            body.Emit(OpCodes.Ceq); body.Emit(OpCodes.Brfalse, nextType);
            foreach (var declaration in implementation.GetConstructors(BindingFlags.Public | BindingFlags.Instance).OrderBy(candidate => candidate.MetadataToken))
            {
                constructors++;
                var nextConstructor = body.DefineLabel();
                body.Emit(OpCodes.Ldloc, actual); Record(declaration); body.Emit(OpCodes.Callvirt, equalsDefinition);
                body.Emit(OpCodes.Brfalse, nextConstructor);
                foreach (var parameter in declaration.GetParameters())
                {
                    var nextParameter = body.DefineLabel();
                    body.Emit(OpCodes.Ldarg_0); body.Emit(OpCodes.Callvirt, getPosition); body.Emit(OpCodes.Ldc_I4, parameter.Position);
                    body.Emit(OpCodes.Bne_Un, nextParameter);
                    // MLC presents ordinary attributes followed by synthesized
                    // In/Out/Optional/MarshalAs attributes, matching the raw CLR
                    // GetCustomAttributes(bool) result used by DI. CoreCLR's
                    // own GetCustomAttributesData() has a different pseudo-first
                    // order; do not sort or emulate that metadata API here.
                    var data = parameter.GetCustomAttributesData();
                    attributes += data.Count;
                    var factory = owner.DefineMethod("ServiceParameterAttributes_" + parameters++, MethodAttributes.Private | MethodAttributes.Static,
                        attribute.MakeArrayType(), Type.EmptyTypes);
                    var code = factory.GetILGenerator();
                    var values = code.DeclareLocal(attribute.MakeArrayType());
                    var value = code.DeclareLocal(attribute);
                    code.Emit(OpCodes.Ldc_I4, data.Count); code.Emit(OpCodes.Newarr, attribute); code.Emit(OpCodes.Stloc, values);
                    for (var index = 0; index < data.Count; index++)
                    {
                        // Construct every attribute before DI starts looking for a
                        // key. Keep the evaluation stack empty across named setters.
                        NativeAttributeConstructionEmitter.Emit(code, data[index], type, reflectionQuery: true);
                        code.Emit(OpCodes.Stloc, value); code.Emit(OpCodes.Ldloc, values); code.Emit(OpCodes.Ldc_I4, index);
                        code.Emit(OpCodes.Ldloc, value); code.Emit(OpCodes.Stelem_Ref);
                    }
                    code.Emit(OpCodes.Ldloc, values); code.Emit(OpCodes.Ret);
                    body.Emit(OpCodes.Call, factory); body.Emit(OpCodes.Ret);
                    body.MarkLabel(nextParameter);
                }
                body.MarkLabel(nextConstructor);
            }
            body.MarkLabel(nextType);
        }
        Fail("The native service constructor parameter was not inferred from canonical service implementation registrations.");
        return new(types.Length, constructors, parameters, attributes);

        void EmitRetainedTypes()
        {
            // The same proven implementation role supplies constructor retention
            // when an EF replacement came through a prebuilt/generic wrapper.
            // Each return is a literal Type; unknown values are never asserted
            // to satisfy a DynamicallyAccessedMembers contract.
            var lookup = owner.DefineMethod("TryRetainServiceImplementation", MethodAttributes.Public | MethodAttributes.Static,
                type, [type]);
            var contract = core.GetType("System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembersAttribute", true)!;
            var members = core.GetType("System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes", true)!;
            var blob = new BlobBuilder(); blob.WriteUInt16(1);
            blob.WriteInt32(Convert.ToInt32(members.GetField("PublicConstructors")!.GetRawConstantValue())); blob.WriteUInt16(0);
            lookup.DefineParameter(0, ParameterAttributes.Retval, null).SetCustomAttribute(
                NativePropertyAccessorEmitter.ConstructorForEmission(contract.GetConstructor([members])!), blob.ToArray());
            var code = lookup.GetILGenerator();
            foreach (var implementation in types)
            {
                var next = code.DefineLabel();
                code.Emit(OpCodes.Ldarg_0); code.Emit(OpCodes.Ldtoken, NativeEmissionTypeNames.ForEmission(implementation));
                code.Emit(OpCodes.Call, typeFromHandle); code.Emit(OpCodes.Ceq); code.Emit(OpCodes.Brfalse, next);
                code.Emit(OpCodes.Ldtoken, NativeEmissionTypeNames.ForEmission(implementation)); code.Emit(OpCodes.Call, typeFromHandle);
                code.Emit(OpCodes.Ret); code.MarkLabel(next);
            }
            code.Emit(OpCodes.Ldnull); code.Emit(OpCodes.Ret);
        }

        void Token(Type value)
        {
            body.Emit(OpCodes.Ldtoken, NativeEmissionTypeNames.ForEmission(value)); body.Emit(OpCodes.Call, typeFromHandle);
        }

        void Record(ConstructorInfo value)
        {
            var field = owner.DefineInitializedData("ServiceConstructorRecord_" + constructors, new byte[16],
                FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly);
            var blob = new BlobBuilder(); blob.WriteUInt16(1); blob.WriteSerializedString(value.DeclaringType!.AssemblyQualifiedName!);
            blob.WriteInt32(value.MetadataToken); blob.WriteInt32(6); blob.WriteSerializedString(""); blob.WriteUInt16(0);
            field.SetCustomAttribute(marker, blob.ToArray()); body.Emit(OpCodes.Ldsflda, field); body.Emit(OpCodes.Conv_U);
            body.Emit(OpCodes.Call, constructorFromRecord);
        }

        void Fail(string message)
        {
            body.Emit(OpCodes.Ldstr, message);
            body.Emit(OpCodes.Newobj, NativePropertyAccessorEmitter.ConstructorForEmission(core.GetType("System.NotSupportedException", true)!
                .GetConstructor([core.GetType("System.String", true)!])!)); body.Emit(OpCodes.Throw);
        }
    }
}

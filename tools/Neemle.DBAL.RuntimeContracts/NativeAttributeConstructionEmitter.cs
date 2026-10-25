// Copyright (c) Neemle contributors. Licensed under the MIT License.
// Extracted unchanged construction/literal semantics from
// NativeConfigurationPropertyDescriptors (DBAL); no application code is run
// while inspecting CustomAttributeData or emitting these operations.
using System.Reflection;
using System.Reflection.Emit;

namespace Neemle.DBAL.Tools;

internal static class NativeAttributeConstructionEmitter
{
    internal static void Emit(ILGenerator body, CustomAttributeData data, Type typeType, bool reflectionQuery = false)
    {
        var parameters = data.Constructor.GetParameters();
        for (var index = 0; index < data.ConstructorArguments.Count; index++) Value(body, data.ConstructorArguments[index], parameters[index].ParameterType, typeType);
        body.Emit(OpCodes.Newobj, NativePropertyAccessorEmitter.ConstructorForEmission(data.Constructor));
        LocalBuilder? instance = null;
        if (reflectionQuery)
        {
            instance = body.DeclareLocal(NativeEmissionTypeNames.ForEmission(data.AttributeType));
            body.Emit(OpCodes.Stloc, instance);
        }
        foreach (var named in data.NamedArguments)
        {
            var protectedSetter = reflectionQuery && named.MemberInfo is PropertyInfo;
            if (protectedSetter) body.BeginExceptionBlock();
            if (instance == null) body.Emit(OpCodes.Dup); else body.Emit(OpCodes.Ldloc, instance);
            switch (named.MemberInfo)
            {
                case FieldInfo field:
                    Value(body, named.TypedValue, field.FieldType, typeType);
                    body.Emit(OpCodes.Stfld, NativePropertyAccessorEmitter.FieldForEmission(field));
                    break;
                case PropertyInfo property:
                    Value(body, named.TypedValue, property.PropertyType, typeType);
                    body.Emit(OpCodes.Callvirt, NativePropertyAccessorEmitter.MethodForEmission(property.SetMethod!));
                    break;
                default: throw new NotSupportedException("Unsupported inferred attribute named member: " + named.MemberInfo);
            }
            if (protectedSetter)
            {
                var core = typeType.Assembly;
                var exception = core.GetType("System.Exception", true)!;
                var invocation = core.GetType("System.Reflection.TargetInvocationException", true)!;
                var format = core.GetType("System.Reflection.CustomAttributeFormatException", true)!;
                var text = core.GetType("System.String", true)!;
                var obj = core.GetType("System.Object", true)!;
                var resources = core.GetType("System.SR", true)!;
                var message = resources.GetProperty("RFLCT_InvalidPropFail", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetMethod
                    ?? throw new NotSupportedException("The selected CLR has no named-attribute-property failure resource.");
                var error = body.DeclareLocal(exception);
                body.BeginCatchBlock(exception);
                body.Emit(OpCodes.Stloc, error);
                body.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(message));
                body.Emit(OpCodes.Ldstr, named.MemberName);
                body.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(text.GetMethod("Format", [text, obj])!));
                body.Emit(OpCodes.Ldloc, error);
                body.Emit(OpCodes.Newobj, NativePropertyAccessorEmitter.ConstructorForEmission(invocation.GetConstructor([exception])!));
                body.Emit(OpCodes.Newobj, NativePropertyAccessorEmitter.ConstructorForEmission(format.GetConstructor([text, exception])!));
                body.Emit(OpCodes.Throw);
                body.EndExceptionBlock();
            }
        }
        if (instance != null) body.Emit(OpCodes.Ldloc, instance);
    }
    private static void Value(ILGenerator body, CustomAttributeTypedArgument argument, Type expected, Type typeType)
    {
        var type = argument.ArgumentType;
        var value = argument.Value;
        if (value == null) { body.Emit(OpCodes.Ldnull); return; }
        if (type == typeType)
        {
            var literal = (Type)value;
            // A generic definition has a standalone definition token. An array,
            // pointer or partly constructed type containing unbound variables
            // would encode !0 outside its declaring scope and produce invalid IL.
            if (literal.ContainsGenericParameters && !literal.IsGenericTypeDefinition)
                throw new NotSupportedException("The inferred attribute Type literal '" + literal
                    + "' contains unbound generic variables and cannot be emitted outside its declaring scope. Use an actual closed Type or a generic type definition.");
            body.Emit(OpCodes.Ldtoken, NativeEmissionTypeNames.ForEmission(literal));
            body.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(typeType.GetMethod("GetTypeFromHandle")!));
        }
        else if (type.IsArray)
        {
            var values = ((IEnumerable<CustomAttributeTypedArgument>)value).ToArray();
            var element = type.GetElementType()!;
            body.Emit(OpCodes.Ldc_I4, values.Length);
            body.Emit(OpCodes.Newarr, NativeEmissionTypeNames.ForEmission(element));
            for (var index = 0; index < values.Length; index++)
            {
                body.Emit(OpCodes.Dup);
                body.Emit(OpCodes.Ldc_I4, index);
                Value(body, values[index], element, typeType);
                if (element.IsValueType) body.Emit(OpCodes.Stelem, NativeEmissionTypeNames.ForEmission(element)); else body.Emit(OpCodes.Stelem_Ref);
            }
        }
        else
        {
            var primitive = type.IsEnum ? type.GetEnumUnderlyingType() : type;
            switch (primitive.FullName)
            {
                case "System.String": body.Emit(OpCodes.Ldstr, (string)value); break;
                case "System.Boolean": body.Emit((bool)value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); break;
                // Ldc_I4 always has a four-byte operand. Emit's byte/short
                // overloads write fewer bytes and corrupt the next opcode.
                case "System.Byte": body.Emit(OpCodes.Ldc_I4, (int)(byte)value); break;
                case "System.SByte": body.Emit(OpCodes.Ldc_I4, (int)(sbyte)value); break;
                case "System.Char": body.Emit(OpCodes.Ldc_I4, (int)(char)value); break;
                case "System.Int16": body.Emit(OpCodes.Ldc_I4, (int)(short)value); break;
                case "System.UInt16": body.Emit(OpCodes.Ldc_I4, (int)(ushort)value); break;
                case "System.Int32": body.Emit(OpCodes.Ldc_I4, (int)value); break;
                case "System.UInt32": body.Emit(OpCodes.Ldc_I4, unchecked((int)(uint)value)); break;
                case "System.Int64": body.Emit(OpCodes.Ldc_I8, (long)value); break;
                case "System.UInt64": body.Emit(OpCodes.Ldc_I8, unchecked((long)(ulong)value)); break;
                case "System.Single": body.Emit(OpCodes.Ldc_R4, (float)value); break;
                case "System.Double": body.Emit(OpCodes.Ldc_R8, (double)value); break;
                default: throw new NotSupportedException("Unsupported configuration attribute literal: " + type);
            }
        }
        if (type.IsValueType && !expected.IsValueType) body.Emit(OpCodes.Box, NativeEmissionTypeNames.ForEmission(type));
    }

}

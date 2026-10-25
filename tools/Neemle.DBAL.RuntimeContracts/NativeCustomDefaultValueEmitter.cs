// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Neemle.DBAL.Tools;

/// <summary>Preserves custom-default construction and accessor effects with inferred operations.</summary>
internal static class NativeCustomDefaultValueEmitter
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    internal sealed record Summary(IReadOnlyList<string> Owners, IReadOnlyList<string> Operations);

    internal static Summary? Emit(MetadataLoadContext context, IEnumerable<Type> inferred, TypeBuilder owner)
    {
        var core = context.CoreAssembly!;
        if (core.GetType("Neemle.DBAL.Runtime.NativeCustomDefaultValues") == null) return null;
        var obj = core.GetType("System.Object", true)!;
        var typeType = core.GetType("System.Type", true)!;
        var memberInfo = core.GetType("System.Reflection.MemberInfo", true)!;
        var parameterInfo = core.GetType("System.Reflection.ParameterInfo", true)!;
        var propertyInfo = core.GetType("System.Reflection.PropertyInfo", true)!;
        var constant = core.GetType("System.Runtime.CompilerServices.CustomConstantAttribute", true)!;
        var decimalConstant = core.GetType("System.Runtime.CompilerServices.DecimalConstantAttribute", true)!;
        var protocol = core.GetType("Neemle.DBAL.Runtime.NativeMemberHandles", true)!;
        var marker = NativePropertyAccessorEmitter.ConstructorForEmission(core.GetType("Neemle.DBAL.Runtime.NativeMemberHandleAttribute", true)!
            .GetConstructors(Declared).Single());
        var shapes = inferred.Where(type => !type.HasElementType && !type.IsGenericParameter && type.AssemblyQualifiedName != null)
            .Select(type => type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type)
            .Distinct().OrderBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal).ToArray();
        var records = new Dictionary<(Type, int), FieldBuilder>();
        using var constants = new ConstantMetadata();
        var operations = new List<string>();
        var method = owner.DefineMethod("ReadCustomDefaultValue", MethodAttributes.Public | MethodAttributes.Static, obj, [obj]);
        var body = method.GetILGenerator();
        var member = body.DeclareLocal(memberInfo);
        var parameter = body.DeclareLocal(parameterInfo);
        var declaration = body.DeclareLocal(typeType);
        var property = body.DefineLabel();
        var known = body.DefineLabel();
        var getType = Method(obj.GetMethod("GetType")!);
        var assembly = Method(typeType.GetProperty("Assembly")!.GetMethod!);
        var declaring = Method(memberInfo.GetProperty("DeclaringType")!.GetMethod!);
        var sameDefinition = Method(memberInfo.GetMethod("HasSameMetadataDefinitionAs")!);

        var nonNull = body.DefineLabel();
        body.Emit(OpCodes.Ldarg_0); body.Emit(OpCodes.Brtrue, nonNull);
        body.Emit(OpCodes.Ldstr, "element");
        body.Emit(OpCodes.Newobj, core.GetType("System.ArgumentNullException", true)!.GetConstructor([core.GetType("System.String", true)!])!);
        body.Emit(OpCodes.Throw);
        body.MarkLabel(nonNull);
        var runtime = body.DefineLabel();
        body.Emit(OpCodes.Ldarg_0); body.Emit(OpCodes.Callvirt, getType); body.Emit(OpCodes.Callvirt, assembly);
        Token(obj); body.Emit(OpCodes.Callvirt, assembly); body.Emit(OpCodes.Ceq); body.Emit(OpCodes.Brtrue, runtime);
        Fail("Native custom default values require an inferred CLR parameter or property.");
        body.MarkLabel(runtime);
        body.Emit(OpCodes.Ldarg_0); body.Emit(OpCodes.Isinst, parameterInfo); body.Emit(OpCodes.Stloc, parameter);
        body.Emit(OpCodes.Ldloc, parameter); body.Emit(OpCodes.Brfalse, property);
        body.Emit(OpCodes.Ldloc, parameter); body.Emit(OpCodes.Callvirt, Method(parameterInfo.GetProperty("Member")!.GetMethod!)); body.Emit(OpCodes.Stloc, member);
        foreach (var shape in shapes)
        foreach (var candidate in shape.GetMethods(Declared).Cast<MethodBase>().Concat(shape.GetConstructors(Declared)))
        foreach (var item in candidate.GetParameters().Concat(candidate is MethodInfo info ? [info.ReturnParameter] : []))
        {
            // CoreLib checks the ECMA Constant row before custom attributes. A
            // constructor attached to such a declaration can never be selected.
            if (constants.HasConstant(candidate.Module.Assembly, item.MetadataToken)) continue;
            var selected = Select(item.GetCustomAttributesData());
            if (selected.Count == 0) continue;
            var next = body.DefineLabel();
            body.Emit(OpCodes.Ldloc, parameter); body.Emit(OpCodes.Callvirt, Method(parameterInfo.GetProperty("Position")!.GetMethod!));
            body.Emit(OpCodes.Ldc_I4, item.Position); body.Emit(OpCodes.Bne_Un, next);
            body.Emit(OpCodes.Ldloc, member); Record(candidate); body.Emit(OpCodes.Callvirt, sameDefinition); body.Emit(OpCodes.Brfalse, next);
            Return(selected, candidate.DeclaringType!.AssemblyQualifiedName + "|" + candidate.MetadataToken + "|" + item.Position);
            body.MarkLabel(next);
        }
        body.Emit(OpCodes.Br, known);
        body.MarkLabel(property);
        body.Emit(OpCodes.Ldarg_0); body.Emit(OpCodes.Isinst, propertyInfo); body.Emit(OpCodes.Stloc, member);
        var propertyValid = body.DefineLabel();
        body.Emit(OpCodes.Ldloc, member); body.Emit(OpCodes.Brtrue, propertyValid);
        Fail("Native custom default values require an inferred CLR parameter or property.");
        body.MarkLabel(propertyValid);
        // CoreCLR's public property constant APIs read ECMA Constant metadata
        // only. Custom constant attributes on properties are never constructors
        // or getter roots, even when the owner itself is inventoried.
        // No selected attribute is a known empty result only for a complete,
        // inventoried CLR owner. An arbitrary loaded assembly is not a contract.
        body.MarkLabel(known);
        body.Emit(OpCodes.Ldloc, member); body.Emit(OpCodes.Callvirt, declaring); body.Emit(OpCodes.Stloc, declaration);
        var hasOwner = body.DefineLabel();
        body.Emit(OpCodes.Ldloc, declaration); body.Emit(OpCodes.Brtrue, hasOwner);
        Fail("A native global custom-default declaration was not inferred.");
        body.MarkLabel(hasOwner);
        var normalized = body.DefineLabel();
        body.Emit(OpCodes.Ldloc, declaration); body.Emit(OpCodes.Callvirt, Method(typeType.GetProperty("IsConstructedGenericType")!.GetMethod!)); body.Emit(OpCodes.Brfalse, normalized);
        body.Emit(OpCodes.Ldloc, declaration); body.Emit(OpCodes.Callvirt, Method(typeType.GetMethod("GetGenericTypeDefinition")!)); body.Emit(OpCodes.Stloc, declaration);
        body.MarkLabel(normalized);
        foreach (var shape in shapes)
        {
            var next = body.DefineLabel();
            body.Emit(OpCodes.Ldloc, declaration); Token(shape); body.Emit(OpCodes.Ceq); body.Emit(OpCodes.Brfalse, next);
            body.Emit(OpCodes.Ldsfld, NativePropertyAccessorEmitter.FieldForEmission(core.GetType("System.DBNull", true)!.GetField("Value")!)); body.Emit(OpCodes.Ret);
            body.MarkLabel(next);
        }
        Fail("The native custom default-value owner was not inferred.");
        return new Summary(shapes.Select(type => type.AssemblyQualifiedName!).ToArray(), operations);

        IReadOnlyList<CustomAttributeData> Select(IEnumerable<CustomAttributeData> attributes)
        {
            var custom = new List<CustomAttributeData>();
            var decimals = new List<CustomAttributeData>();
            foreach (var data in attributes)
            {
                if (data.AttributeType.IsSubclassOf(constant)) custom.Add(data);
                else if (data.AttributeType == decimalConstant) decimals.Add(data);
            }
            return custom.Count != 0 ? custom : decimals;
        }

        void Return(IReadOnlyList<CustomAttributeData> selected, string identity)
        {
            // CoreCLR materializes the complete matching attribute array before
            // reading its first Value. Later constructor/setter effects and errors
            // must therefore precede that getter, including mixed subclasses.
            var first = body.DeclareLocal(selected[0].AttributeType == decimalConstant ? decimalConstant : constant);
            for (var index = 0; index < selected.Count; index++)
            {
                NativeAttributeConstructionEmitter.Emit(body, selected[index], typeType, reflectionQuery: true);
                if (index == 0) body.Emit(OpCodes.Stloc, first);
                else body.Emit(OpCodes.Pop);
                operations.Add(identity + "|" + index + "|" + selected[index].AttributeType.AssemblyQualifiedName);
            }
            body.Emit(OpCodes.Ldloc, first);
            var value = selected[0].AttributeType == decimalConstant ? decimalConstant.GetProperty("Value")! : constant.GetProperty("Value")!;
            body.Emit(OpCodes.Callvirt, Method(value.GetMethod!));
            if (value.PropertyType.IsValueType) body.Emit(OpCodes.Box, NativeEmissionTypeNames.ForEmission(value.PropertyType));
            body.Emit(OpCodes.Ret);
        }

        void Record(MemberInfo candidate)
        {
            var kind = candidate switch { PropertyInfo => 1, ConstructorInfo => 6, MethodInfo => 5, _ => throw new NotSupportedException("Invalid default-value owner: " + candidate) };
            var key = (candidate.DeclaringType!, candidate.MetadataToken);
            if (!records.TryGetValue(key, out var field))
            {
                field = owner.DefineInitializedData("CustomDefaultRecord_" + records.Count, new byte[16], FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly);
                var blob = new BlobBuilder(); blob.WriteUInt16(1); blob.WriteSerializedString(candidate.DeclaringType!.AssemblyQualifiedName!);
                blob.WriteInt32(candidate.MetadataToken); blob.WriteInt32(kind); blob.WriteSerializedString(""); blob.WriteUInt16(0);
                field.SetCustomAttribute(marker, blob.ToArray()); records.Add(key, field);
            }
            body.Emit(OpCodes.Ldsflda, field); body.Emit(OpCodes.Conv_U);
            body.Emit(OpCodes.Call, Method(protocol.GetMethod(kind == 1 ? "PropertyFromRecord" : kind == 6 ? "ConstructorFromRecord" : "MethodFromRecord", Declared)!));
        }

        void Token(Type type)
        {
            body.Emit(OpCodes.Ldtoken, NativeEmissionTypeNames.ForEmission(type));
            body.Emit(OpCodes.Call, Method(typeType.GetMethod("GetTypeFromHandle")!));
        }

        void Fail(string message)
        {
            body.Emit(OpCodes.Ldstr, message);
            body.Emit(OpCodes.Newobj, core.GetType("System.NotSupportedException", true)!.GetConstructor([core.GetType("System.String", true)!])!);
            body.Emit(OpCodes.Throw);
        }
    }

    private static MethodInfo Method(MethodInfo method) => NativePropertyAccessorEmitter.MethodForEmission(method);

    private sealed class ConstantMetadata : IDisposable
    {
        private readonly Dictionary<Assembly, PEReader> _images = new();

        internal bool HasConstant(Assembly assembly, int token)
        {
            var handle = MetadataTokens.EntityHandle(token);
            if (handle.IsNil) return false;
            if (!_images.TryGetValue(assembly, out var image))
                _images.Add(assembly, image = new PEReader(File.OpenRead(assembly.Location)));
            var reader = image.GetMetadataReader();
            return handle.Kind switch
            {
                HandleKind.Parameter => !reader.GetParameter((ParameterHandle)handle).GetDefaultValue().IsNil,
                HandleKind.PropertyDefinition => !reader.GetPropertyDefinition((PropertyDefinitionHandle)handle).GetDefaultValue().IsNil,
                _ => throw new InvalidDataException("A custom-default declaration has no parameter/property metadata identity.")
            };
        }

        public void Dispose()
        {
            foreach (var image in _images.Values) image.Dispose();
        }
    }
}

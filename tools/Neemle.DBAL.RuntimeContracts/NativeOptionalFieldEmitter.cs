// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Reflection;
using System.Reflection.Emit;

namespace Neemle.DBAL.Tools;

/// <summary>Infers optional named member operations from actual bound field requests.</summary>
internal static class NativeOptionalFieldEmitter
{
    internal sealed record Request(Type Owner, string Name, BindingFlags Flags, FieldInfo? Field);

    internal static Request[] Resolve(IEnumerable<Assembly> assemblies, IEnumerable<string> requests)
        => requests.SelectMany(request =>
        {
            var parts = request.Split('\t');
            if (parts.Length != 3 || !int.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var flags))
                throw new InvalidOperationException("Malformed inferred optional-field contract: " + request);
            return assemblies.Select(assembly => assembly.GetType(parts[0], false)).Where(type => type != null)
                .Select(type => new Request(type!, parts[1], (BindingFlags)flags, type!.GetField(parts[1], (BindingFlags)flags)));
        }).Distinct().OrderBy(request => request.Owner.AssemblyQualifiedName, StringComparer.Ordinal)
            .ThenBy(request => request.Name, StringComparer.Ordinal).ThenBy(request => request.Flags).ToArray();

    internal static void Emit(Assembly core, Type registry, TypeBuilder owner, ILGenerator registration, Request[] requests)
    {
        var obj = core.GetType("System.Object", true)!;
        var func = core.GetType("System.Func`1", true)!.MakeGenericType(obj);
        var typeType = core.GetType("System.Type", true)!;
        for (var index = 0; index < requests.Length; index++)
        {
            var request = requests[index];
            registration.Emit(OpCodes.Ldtoken, request.Owner);
            registration.Emit(OpCodes.Call, typeType.GetMethod("GetTypeFromHandle")!);
            registration.Emit(OpCodes.Ldstr, request.Name);
            registration.Emit(OpCodes.Ldc_I4, (int)request.Flags);
            if (request.Field == null) registration.Emit(OpCodes.Ldnull);
            else
            {
                if (request.Field.FieldType.IsByRefLike || request.Field.FieldType.IsPointer || request.Field.FieldType.IsFunctionPointer)
                    throw new NotSupportedException("The inferred optional field is not boxable: " + request.Field);
                var getter = owner.DefineMethod("OptionalField_" + index, MethodAttributes.Public | MethodAttributes.Static, obj, Type.EmptyTypes);
                var body = getter.GetILGenerator();
                if (!request.Field.IsStatic)
                {
                    TargetException error;
                    try { _ = typeof(InstanceProbe).GetField(nameof(InstanceProbe.Value))!.GetValue(null); throw new InvalidOperationException("The CLR accepted a null instance-field receiver."); }
                    catch (TargetException caught) { error = caught; }
                    body.Emit(OpCodes.Ldstr, error.Message);
                    body.Emit(OpCodes.Newobj, core.GetType(typeof(TargetException).FullName!, true)!.GetConstructor([core.GetType("System.String", true)!])!);
                    body.Emit(OpCodes.Throw);
                }
                else
                {
                    body.Emit(OpCodes.Ldsfld, request.Field);
                    if (request.Field.FieldType.IsValueType) body.Emit(OpCodes.Box, request.Field.FieldType);
                    body.Emit(OpCodes.Ret);
                }
                registration.Emit(OpCodes.Ldnull);
                registration.Emit(OpCodes.Ldftn, getter);
                registration.Emit(OpCodes.Newobj, func.GetConstructors().Single());
            }
            registration.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(registry.GetMethod("RegisterField")!));
        }
    }

    private sealed class InstanceProbe { public readonly object? Value = null; }
}

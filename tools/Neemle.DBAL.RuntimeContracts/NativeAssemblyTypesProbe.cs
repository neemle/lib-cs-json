// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text.Json;

namespace Neemle.DBAL.Tools;

/// <summary>Isolates assembly type arrays without rooting unrelated member bodies.</summary>
internal static class NativeAssemblyTypesProbe
{
    internal static void Generate(string manifest, string application, string output)
    {
        var paths = File.ReadAllLines(manifest).Append(application).Where(File.Exists)
            .Select(Path.GetFullPath).Distinct().ToArray();
        using var context = new MetadataLoadContext(new SelectedCompilerAssemblyResolver(paths), "System.Private.CoreLib");
        var selected = paths.Select(context.LoadFromAssemblyPath).Distinct().ToArray();
        Generate(context.CoreAssembly!, selected, output);
    }

    // Keep the original annotated operation whenever an implementation shape
    // cannot be proved complete from the selected metadata.
    internal static bool CanLower(Assembly core, IEnumerable<Assembly> selected)
        => UnprovenTypes(core, selected).Length == 0;

    internal static Type[] UnprovenTypes(Assembly core, IEnumerable<Assembly> selected)
    {
        var assemblyType = core.GetType("System.Reflection.Assembly", true)!;
        return selected.Where(assembly => assembly != core).SelectMany(assembly => assembly.GetTypes())
            .Where(type => !type.IsAbstract && assemblyType.IsAssignableFrom(type))
            .Where(type => type.ContainsGenericParameters || Operation(core, assemblyType, type, "DefinedTypes") == null
                || Operation(core, assemblyType, type, "ExportedTypes") == null).ToArray();
    }

    internal static void Generate(Assembly core, IEnumerable<Assembly> inputs, string output)
    {
        var selected = inputs.Distinct().ToArray();
        var assemblyType = core.GetType("System.Reflection.Assembly", true)!;
        var obj = core.GetType("System.Object", true)!;
        var str = core.GetType("System.String", true)!;
        var boolean = core.GetType("System.Boolean", true)!;
        var typeType = core.GetType("System.Type", true)!;
        var typeInfo = core.GetType("System.Reflection.TypeInfo", true)!;
        var getType = NativePropertyAccessorEmitter.MethodForEmission(typeType.GetMethod("GetTypeFromHandle")!);
        var getInfo = NativePropertyAccessorEmitter.MethodForEmission(core.GetType("System.Reflection.IntrospectionExtensions", true)!.GetMethod("GetTypeInfo")!);
        var equal = NativePropertyAccessorEmitter.MethodForEmission(str.GetMethod("op_Equality", [str, str])!);
        var getName = NativePropertyAccessorEmitter.MethodForEmission(assemblyType.GetProperty("FullName")!.GetMethod!);
        var rows = selected.OrderBy(assembly => assembly.FullName, StringComparer.Ordinal)
            .Select(assembly => (Assembly: assembly, Types: assembly.GetTypes())).ToArray();
        var custom = rows.Where(row => row.Assembly != core).SelectMany(row => row.Types)
            .Where(type => !type.IsAbstract && !type.ContainsGenericParameters && assemblyType.IsAssignableFrom(type))
            .Select(type => (Type: type, Defined: Operation(core, assemblyType, type, "DefinedTypes"), Exported: Operation(core, assemblyType, type, "ExportedTypes")))
            .Where(row => row.Defined != null && row.Exported != null).ToArray();
        var objectGetType = NativePropertyAccessorEmitter.MethodForEmission(obj.GetMethod("GetType")!);
        var typeAssembly = NativePropertyAccessorEmitter.MethodForEmission(typeType.GetProperty("Assembly")!.GetMethod!);
        // Runtime assembly identity is obtained from typeof(object).Assembly,
        // rather than assuming a private implementation name or trusting a
        // user-overridden Assembly.FullName. Unproven custom shapes stay outside
        // this probe; production integration must retain their original contract.
        NativePropertyAccessorEmitter.Generate(core, [], "Neemle.DBAL.AssemblyTypesProbe", output, (_, owner, _) =>
        {
            var read = owner.DefineMethod("ReadAssembly", MethodAttributes.Public | MethodAttributes.Static,
                obj, [assemblyType, boolean]);
            var body = read.GetILGenerator();
            var runtime = body.DefineLabel();
            body.Emit(OpCodes.Ldarg_0);
            body.Emit(OpCodes.Callvirt, objectGetType);
            body.Emit(OpCodes.Ldtoken, obj);
            body.Emit(OpCodes.Call, getType);
            body.Emit(OpCodes.Callvirt, typeAssembly);
            body.Emit(OpCodes.Callvirt, objectGetType);
            body.Emit(OpCodes.Ceq);
            body.Emit(OpCodes.Brtrue, runtime);
            foreach (var candidate in custom.OrderByDescending(row => Depth(row.Type)))
            {
                var next = body.DefineLabel();
                body.Emit(OpCodes.Ldarg_0);
                body.Emit(OpCodes.Isinst, NativeEmissionTypeNames.ForEmission(candidate.Type));
                body.Emit(OpCodes.Brfalse, next);
                var defined = body.DefineLabel();
                body.Emit(OpCodes.Ldarg_1);
                body.Emit(OpCodes.Brfalse, defined);
                Call(candidate.Exported!.Value);
                body.MarkLabel(defined);
                Call(candidate.Defined!.Value);
                body.MarkLabel(next);
                void Call((MethodInfo Getter, NativeConstantGetterBody? Body) operation)
                {
                    var getter = operation.Getter;
                    body.Emit(OpCodes.Ldarg_0);
                    body.Emit(OpCodes.Castclass, NativeEmissionTypeNames.ForEmission(getter.DeclaringType!));
                    if (operation.Body != null) { operation.Body.Emit(body); return; }
                    body.Emit(OpCodes.Callvirt, NativePropertyAccessorEmitter.MethodForEmission(getter));
                    body.Emit(OpCodes.Ret);
                }
            }
            body.Emit(OpCodes.Ldstr, "Custom Assembly getter operations were not inferred by this isolated probe.");
            body.Emit(OpCodes.Newobj, core.GetType("System.NotSupportedException", true)!.GetConstructor([str])!);
            body.Emit(OpCodes.Throw);
            body.MarkLabel(runtime);
            var name = body.DeclareLocal(str);
            body.Emit(OpCodes.Ldarg_0);
            body.Emit(OpCodes.Callvirt, getName);
            body.Emit(OpCodes.Stloc, name);
            foreach (var row in rows)
            {
                var next = body.DefineLabel();
                body.Emit(OpCodes.Ldloc, name);
                body.Emit(OpCodes.Ldstr, row.Assembly.FullName!);
                body.Emit(OpCodes.Call, equal);
                body.Emit(OpCodes.Brfalse, next);
                var defined = body.DefineLabel();
                body.Emit(OpCodes.Ldarg_1);
                body.Emit(OpCodes.Brfalse, defined);
                Array(body, row.Types.Where(type => type.IsVisible).ToArray(), typeType, false);
                body.MarkLabel(defined);
                Array(body, row.Types, typeInfo, true);
                body.MarkLabel(next);
            }
            body.Emit(OpCodes.Ldstr, "The assembly is outside this metadata probe's selected inputs.");
            body.Emit(OpCodes.Newobj, core.GetType("System.InvalidOperationException", true)!.GetConstructor([str])!);
            body.Emit(OpCodes.Throw);
        }, additionalAccess: selected);
        File.WriteAllText(output + ".assemblies.json", JsonSerializer.Serialize(new
        {
            nativeVerified = false,
            inferredCustomOverrides = custom.Select(row => new { receiver = row.Type.AssemblyQualifiedName,
                definedGetter = row.Defined!.Value.Getter.MetadataToken, exportedGetter = row.Exported!.Value.Getter.MetadataToken,
                definedLoweredBody = Proof(row.Defined.Value.Body), exportedLoweredBody = Proof(row.Exported.Value.Body) }),
            generatedAssemblySha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(output))),
            assemblies = rows.Select(row => new
            {
                identity = row.Assembly.FullName, source = row.Assembly.Location,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(row.Assembly.Location))),
                definitions = row.Types.Select(type => new { token = type.MetadataToken, name = type.FullName, visible = type.IsVisible })
            })
        }, new JsonSerializerOptions { WriteIndented = true }));

        static int Depth(Type type)
        {
            var depth = 0;
            for (var current = type; current != null; current = current.BaseType) depth++;
            return depth;
        }

        void Array(ILGenerator body, Type[] types, Type element, bool infos)
        {
            body.Emit(OpCodes.Ldc_I4, types.Length);
            body.Emit(OpCodes.Newarr, NativeEmissionTypeNames.ForEmission(element));
            for (var index = 0; index < types.Length; index++)
            {
                body.Emit(OpCodes.Dup);
                body.Emit(OpCodes.Ldc_I4, index);
                body.Emit(OpCodes.Ldtoken, NativeEmissionTypeNames.ForEmission(types[index]));
                body.Emit(OpCodes.Call, getType);
                if (infos) body.Emit(OpCodes.Call, getInfo);
                body.Emit(OpCodes.Stelem_Ref);
            }
            body.Emit(OpCodes.Ret);
        }
    }

    private static (MethodInfo Getter, NativeConstantGetterBody? Body)? Operation(Assembly core, Type assemblyType, Type type, string property)
    {
        if (FindOverride(assemblyType, type, property) is { } direct) return (direct, null);
        var getter = assemblyType.GetProperty(property)!.GetMethod!;
        return NativeConstantGetterBody.Infer(core, type, getter) is { Error: not null } body
            ? (getter, body) : null;
    }

    private static object? Proof(NativeConstantGetterBody? body)
        => body == null ? null : new { body.Source, body.Token, body.IlSha256, body.RuntimeArchive,
            errorToken = body.Error?.MetadataToken, errorOwner = body.Error?.DeclaringType?.AssemblyQualifiedName,
            exceptionConstructor = body.ExceptionConstructor?.MetadataToken };

    private static MethodInfo? FindOverride(Type assemblyType, Type type, string property)
    {
        var root = assemblyType.GetProperty(property)!.GetMethod!;
        for (var current = type; current != null && current != assemblyType; current = current.BaseType)
            foreach (var method in current.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (NativeVirtualGetterDispatch.Root(method) is { } slot
                    && slot.Module.ModuleVersionId == root.Module.ModuleVersionId && slot.MetadataToken == root.MetadataToken
                    && !method.ContainsGenericParameters && !method.GetCustomAttributesData().Any(attribute => attribute.AttributeType.FullName is
                        "System.Diagnostics.CodeAnalysis.RequiresUnreferencedCodeAttribute" or "System.Diagnostics.CodeAnalysis.RequiresDynamicCodeAttribute"))
                    return method;
        return null;
    }
}

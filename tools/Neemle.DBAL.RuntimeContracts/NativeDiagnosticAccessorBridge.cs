// Copyright (c) Neemle contributors. Licensed under the MIT License.

using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text.Json;

namespace Neemle.DBAL.Tools;

/// <summary>Compiles inferred payload lookup semantics and direct calls, without executing payload code.</summary>
internal static class NativeDiagnosticAccessorBridge
{
    private const BindingFlags Properties = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    internal static void Generate(string manifest, string application, string output)
    {
        var paths = File.ReadAllLines(manifest).Append(application).Where(File.Exists).Select(Path.GetFullPath).Distinct().ToArray();
        using var context = new MetadataLoadContext(new SelectedCompilerAssemblyResolver(paths), "System.Private.CoreLib");
        var assemblies = paths.Select(context.LoadFromAssemblyPath).ToArray();
        var roots = NativeDiagnosticPayloadMetadata.Resolve(context, assemblies);
        var registry = assemblies.Single(assembly => assembly.GetName().Name == "System.Diagnostics.DiagnosticSource")
            .GetType("Neemle.DBAL.Runtime.NativeDiagnosticPayloads", true)!;
        MethodInfo? typeRead = null;
        if (context.CoreAssembly!.GetType("Neemle.DBAL.Runtime.NativeMemberHandles", false) != null)
        {
            var universe = output + ".type-universe.json";
            var types = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "Neemle.DBAL.TypeMetadataProbe.dll");
            NativeDiagnosticMetadataUniverse.Generate(manifest, application, universe);
            NativeTypeMetadataProbe.Generate(manifest, application, types, true, false, universe);
            typeRead = context.LoadFromAssemblyPath(types).GetType(NativePropertyAccessorEmitter.Owner, true)!.GetMethod("Read")!;
        }
        Generate(context.CoreAssembly!, roots, registry, "Neemle.DBAL.Diagnostics." + AssemblyName.GetAssemblyName(application).Name, output, assemblies, context, typeRead);
    }

    internal static void Generate(Assembly core, Type[] roots, Type registry, string assemblyName, string output)
        => Generate(core, roots, registry, assemblyName, output, null);

    private static void Generate(Assembly core, Type[] roots, Type registry, string assemblyName, string output,
        IEnumerable<Assembly>? selected, MetadataLoadContext? context = null, MethodInfo? typeRead = null)
    {
        // Traverse actual closed property signatures; cyclic shapes are visited once.
        // Expanding generic graphs fail explicitly rather than silently dropping members.
        var owners = new Dictionary<string, Type>(StringComparer.Ordinal);
        var pending = new Queue<Type>(roots.Append(registry.GetMethod("CurrentActivity")!.ReturnType));
        while (pending.TryDequeue(out var type))
        {
            if (type.ContainsGenericParameters || type.IsByRefLike || type.IsPointer || type.IsFunctionPointer) continue;
            if (!owners.TryAdd(type.AssemblyQualifiedName!, type)) continue;
            if (owners.Count > 4096) throw new InvalidOperationException("The inferred DiagnosticSource property graph expands beyond 4096 closed shapes. Reduce the expanding payload graph; no members were silently omitted.");
            foreach (var property in type.GetProperties(Properties))
                if (Callable(property)) pending.Enqueue(property.PropertyType);
            if (type.IsArray) pending.Enqueue(type.GetElementType()!);
        }
        var ordered = owners.Values.OrderBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal).ToArray();
        var bindings = new List<object>();
        var properties = ordered.SelectMany(type => type.GetProperties(Properties | BindingFlags.DeclaredOnly)
            .Concat(type.GetProperties(Properties))).Where(Callable).Distinct().ToArray();
        MethodInfo? assemblyRead = null;
        if (context != null && selected != null && properties.Any(IsAssemblyEnumeration)
            && NativeAssemblyTypesProbe.CanLower(core, selected))
        {
            var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "Neemle.DBAL.AssemblyTypesProbe.dll");
            NativeAssemblyTypesProbe.Generate(core, selected, path);
            NativeEmissionTypeNames.ValidateReferences(path, context);
            assemblyRead = context.LoadFromAssemblyPath(Path.GetFullPath(path))
                .GetType(NativePropertyAccessorEmitter.Owner, true)!.GetMethod("ReadAssembly")!;
        }
        NativePropertyAccessorEmitter.Generate(core, properties, assemblyName, output, (_, owner, getters) =>
        {
            var methods = getters.ToDictionary(pair => (pair.Key.Module.ModuleVersionId, pair.Key.MetadataToken,
                pair.Key.DeclaringType!.AssemblyQualifiedName), pair => pair.Value);
            var obj = core.GetType("System.Object", true)!;
            var str = core.GetType("System.String", true)!;
            var typeType = core.GetType("System.Type", true)!;
            var func = core.GetType("System.Func`2", true)!.MakeGenericType(obj, obj);
            var funcConstructor = func.GetConstructors().Single();
            var descriptor = registry.Assembly.GetType("Neemle.DBAL.Runtime.NativeDiagnosticProperty", true)!;
            var descriptorConstructor = descriptor.GetConstructors().Single();
            var register = owner.DefineMethod("Register", MethodAttributes.Public | MethodAttributes.Static,
                core.GetType("System.Void", true)!, Type.EmptyTypes).GetILGenerator();
            foreach (var type in ordered)
            {
                var all = type.GetProperties(Properties);
                var names = all.Select(property => property.Name).Concat(type.GetProperties(Properties | BindingFlags.DeclaredOnly).Select(property => property.Name))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                register.Emit(OpCodes.Ldtoken, type);
                register.Emit(OpCodes.Call, typeType.GetMethod("GetTypeFromHandle")!);
                var implicitNames = all.Where(property => property.GetMethod is { } getter && getter.GetParameters().Length == 0)
                    .Select(property => property.Name).ToArray();
                Array(implicitNames.Length, str, index => register.Emit(OpCodes.Ldstr, implicitNames[index]));
                Array(names.Length, descriptor, index =>
                {
                    var name = names[index];
                    PropertyInfo? selected = null;
                    string? error = null;
                    var kind = 0;
                    try { selected = type.GetTypeInfo().GetDeclaredProperty(name) ?? all.FirstOrDefault(property => property.Name == name); }
                    catch (AmbiguousMatchException exception) { kind = 2; error = exception.Message; }
                    if (selected?.GetMethod?.IsStatic == true || selected?.SetMethod?.IsStatic == true) kind = 1;
                    else if (kind == 0 && selected?.GetMethod == null) kind = 4;
                    else if (kind == 0 && (selected!.PropertyType.IsPointer || selected.PropertyType.IsByRef || selected.PropertyType.IsFunctionPointer))
                    { kind = 5; error = InvalidGenericArgumentError(selected.PropertyType); }
                    else if (kind == 0 && selected!.GetIndexParameters().Length != 0)
                    { kind = 3; error = IndexerBindingError(); }
                    else if (kind == 0 && !Callable(selected!))
                        throw new NotSupportedException("DBAL cannot generate the DiagnosticSource getter " + type + "." + name
                            + ". Its signature is not a closed boxable instance getter; no replacement behavior was emitted.");
                    bindings.Add(new { owner = type.AssemblyQualifiedName, name, kind, error,
                        declaring = selected?.DeclaringType?.AssemblyQualifiedName, token = selected?.MetadataToken,
                        getter = selected?.GetMethod?.MetadataToken });
                    register.Emit(OpCodes.Ldstr, name);
                    register.Emit(OpCodes.Ldc_I4, kind);
                    if (kind == 0) Delegate(methods[(selected!.Module.ModuleVersionId, selected.MetadataToken,
                        selected.DeclaringType!.AssemblyQualifiedName)]); else register.Emit(OpCodes.Ldnull);
                    if (error == null) register.Emit(OpCodes.Ldnull); else register.Emit(OpCodes.Ldstr, error);
                    register.Emit(OpCodes.Newobj, descriptorConstructor);
                });
                var enumerable = type.GetInterfaces().FirstOrDefault(contract => contract.IsGenericType
                    && contract.GetGenericTypeDefinition() == core.GetType("System.Collections.Generic.IEnumerable`1", true));
                if (enumerable == null) register.Emit(OpCodes.Ldnull);
                else
                {
                    var method = owner.DefineMethod("Enumerate_" + System.Array.IndexOf(ordered, type), MethodAttributes.Public | MethodAttributes.Static,
                        obj, [obj]);
                    var body = method.GetILGenerator();
                    body.Emit(OpCodes.Ldarg_0);
                    body.Emit(OpCodes.Call, registry.GetMethod("FormatEnumerable")!.MakeGenericMethod(enumerable.GetGenericArguments()));
                    body.Emit(OpCodes.Ret);
                    Delegate(method);
                }
                register.Emit(OpCodes.Call, registry.GetMethod("Register")!);
            }
            register.Emit(OpCodes.Ret);

            void Delegate(MethodBuilder method)
            {
                register.Emit(OpCodes.Ldnull);
                register.Emit(OpCodes.Ldftn, method);
                register.Emit(OpCodes.Newobj, funcConstructor);
            }
            void Array(int count, Type element, Action<int> item)
            {
                register.Emit(OpCodes.Ldc_I4, count);
                register.Emit(OpCodes.Newarr, element);
                for (var index = 0; index < count; index++)
                {
                    register.Emit(OpCodes.Dup);
                    register.Emit(OpCodes.Ldc_I4, index);
                    item(index);
                    register.Emit(OpCodes.Stelem_Ref);
                }
            }
        }, virtualDispatch: selected == null ? null : new NativeVirtualGetterDispatch(core, selected),
            lowerGetter: (_, _, property, body) =>
            {
                if (typeRead != null && TypeMetadataKind(property) is { } kind)
                {
                    body.Emit(OpCodes.Ldarg_0);
                    body.Emit(OpCodes.Castclass, core.GetType("System.Reflection.TypeInfo", true)!);
                    body.Emit(OpCodes.Ldc_I4, kind);
                    body.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(typeRead));
                    body.Emit(OpCodes.Ret);
                    return true;
                }
                if (assemblyRead == null || !IsAssemblyEnumeration(property)) return false;
                body.Emit(OpCodes.Ldarg_0);
                body.Emit(OpCodes.Castclass, core.GetType("System.Reflection.Assembly", true)!);
                body.Emit(OpCodes.Ldc_I4, property.Name == "ExportedTypes" ? 1 : 0);
                body.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(assemblyRead));
                body.Emit(OpCodes.Ret);
                return true;
            });
        File.WriteAllText(output + ".bindings.json", JsonSerializer.Serialize(new
        {
            roots = roots.Select(type => type.AssemblyQualifiedName),
            owners = ordered.Select(type => new
            {
                type = type.AssemblyQualifiedName, type.Module.ModuleVersionId, source = type.Assembly.Location,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(type.Assembly.Location))),
                implicitNames = type.GetProperties(Properties).Where(property => property.GetMethod is { } getter && getter.GetParameters().Length == 0)
                    .Select(property => property.Name),
                enumerable = type.GetInterfaces().FirstOrDefault(contract => contract.IsGenericType
                    && contract.GetGenericTypeDefinition() == core.GetType("System.Collections.Generic.IEnumerable`1", true))?.AssemblyQualifiedName
            }),
            bindings, generatedAssemblyTypeOperations = assemblyRead != null,
            generatedTypeMetadataOperations = typeRead != null,
            unprovenAssemblyImplementations = selected == null ? [] : NativeAssemblyTypesProbe.UnprovenTypes(core, selected)
                .Select(type => type.AssemblyQualifiedName).ToArray(),
            registration = NativePropertyAccessorEmitter.Owner + ".Register",
            runtimeReflection = false, nativeVerified = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllLines(output + ".assembly-types.references.txt",
            new[] { assemblyRead, typeRead }.Where(method => method != null)
                .Select(method => method!.DeclaringType!.Assembly.Location));
    }

    private static bool IsAssemblyEnumeration(PropertyInfo property)
        => property.DeclaringType?.FullName == "System.Reflection.Assembly"
            && property.Name is "DefinedTypes" or "ExportedTypes";

    private static int? TypeMetadataKind(PropertyInfo property)
        => property.DeclaringType?.FullName != "System.Reflection.TypeInfo" ? null : property.Name switch
        {
            "DeclaredConstructors" => 0, "DeclaredMethods" => 1, "DeclaredFields" => 2,
            "DeclaredNestedTypes" => 3, "ImplementedInterfaces" => 4,
            "DeclaredProperties" => 7, "DeclaredEvents" => 8, "DeclaredMembers" => 9, _ => null
        };

    private static bool Callable(PropertyInfo property)
        => property.GetMethod is { IsStatic: false } getter && !getter.ContainsGenericParameters
            && property.GetIndexParameters().Length == 0 && !property.PropertyType.IsByRef
            && !property.PropertyType.IsByRefLike && !property.PropertyType.IsPointer && !property.PropertyType.IsFunctionPointer;

    private static string IndexerBindingError()
    {
        // Read the selected .NET host's real delegate error without loading or
        // invoking application code. No duplicated CLR message grammar.
        try { typeof(IndexerProbe).GetProperty("Item")!.GetMethod!.CreateDelegate(typeof(Func<IndexerProbe, int>)); }
        catch (ArgumentException exception) { return exception.Message; }
        throw new InvalidOperationException("The pinned .NET delegate binding contract changed.");
    }
    private sealed class IndexerProbe { public int this[int index] => throw new NotSupportedException(); }

    private static string InvalidGenericArgumentError(Type argument)
    {
        var probe = typeof(int).MakePointerType();
        try { typeof(List<>).MakeGenericType(probe); }
        catch (ArgumentException exception) { return exception.Message.Replace(probe.ToString(), argument.ToString(), StringComparison.Ordinal); }
        throw new InvalidOperationException("The pinned .NET generic argument validation contract changed.");
    }
}

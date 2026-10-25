// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Neemle.DBAL.Tools;

/// <summary>Emits constructor operations from inferred result contracts and actual closed member signatures.</summary>
internal static class NativeConstructorBridge
{
    internal const string ContractsKey = "Neemle.DBAL.NativeConstructorBases.v1";
    private const string TypesKey = "Neemle.DBAL.NativeConstructorTypes.v1";
    private const string RegistryKey = "Neemle.DBAL.NativeConstructorRegistry.v1";

    internal static void Generate(string manifest, string application, string output, bool configuration = false, bool oracle = false)
    {
        var paths = File.ReadAllLines(manifest).Append(application).Where(File.Exists)
            .Select(Path.GetFullPath).Distinct().ToArray();
        using var context = new MetadataLoadContext(new SelectedCompilerAssemblyResolver(paths), "System.Private.CoreLib");
        var assemblies = paths.Select(context.LoadFromAssemblyPath).ToArray();
        var core = context.CoreAssembly!;
        var marker = core.GetType("System.Reflection.AssemblyMetadataAttribute", true)!;
        var metadata = assemblies.SelectMany(assembly => assembly.GetCustomAttributesData())
            .Where(attribute => attribute.AttributeType == marker && attribute.ConstructorArguments.Count == 2).ToArray();
        string[] Values(string key) => metadata.Where(attribute => attribute.ConstructorArguments[0].Value as string == key)
            .SelectMany(attribute => ((string)attribute.ConstructorArguments[1].Value!).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assembly? defaultOwner = null;
        Type? Named(string name, bool required) => Type.GetType(name, context.LoadFromAssemblyName,
            (assembly, type, ignoreCase) => assembly?.GetType(type, false, ignoreCase)
                ?? (assembly == null ? defaultOwner?.GetType(type, false, ignoreCase) ?? core.GetType(type, false, ignoreCase) : null), required);
        var contractsKey = oracle ? "Neemle.DBAL.OracleConstructorBases.v1" : configuration ? "Neemle.DBAL.NativeConfigurationConstructorBases.v1" : ContractsKey;
        var typesKey = oracle ? "Neemle.DBAL.OracleConstructorTypes.v1" : configuration ? "Neemle.DBAL.NativeConfigurationConstructorTypes.v1" : TypesKey;
        var registryKey = oracle ? "Neemle.DBAL.OracleConstructorRegistry.v1" : configuration ? "Neemle.DBAL.NativeConfigurationConstructorRegistry.v1" : RegistryKey;
        var contracts = Values(contractsKey).Select(name => Named(name, true)!).ToArray();
        var argumentContracts = oracle ? Values("Neemle.DBAL.OracleArgumentConstructorBases.v1").Select(name => Named(name, true)!).ToArray() : [];
        var fields = oracle ? NativeOptionalFieldEmitter.Resolve(assemblies, Values("Neemle.DBAL.OracleFieldRequests.v1")) : [];
        if (contracts.Length == 0 && fields.Length == 0) throw new InvalidOperationException("No inferred native constructor result or optional-field contract was supplied.");
        var registryNames = Values(registryKey);
        if (registryNames.Length > 1) throw new InvalidOperationException("Multiple constructor registries require separate generated bridges.");
        var registry = registryNames.Length == 0 ? null : Named(registryNames[0], true);
        defaultOwner = registry?.Assembly ?? context.LoadFromAssemblyPath(Path.GetFullPath(application));
        var requested = new HashSet<Type>();
        foreach (var name in Values(typesKey))
        {
            try { if (Named(name, false) is { } type) requested.Add(type); }
            catch (Exception error) when (error is ArgumentException or FileNotFoundException or TypeLoadException) { }
        }
        const BindingFlags members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var candidates = new Dictionary<string, Type>(StringComparer.Ordinal);
        var eligible = new HashSet<Assembly>(contracts.Select(type => type.Assembly)) { context.LoadFromAssemblyPath(Path.GetFullPath(application)) };
        bool changed;
        do
        {
            changed = false;
            foreach (var assembly in assemblies)
                if (!eligible.Contains(assembly) && assembly.GetReferencedAssemblies().Any(reference => eligible.Any(owner => owner.GetName().Name == reference.Name)))
                    changed |= eligible.Add(assembly);
        } while (changed);
        foreach (var assembly in assemblies.Where(eligible.Contains))
        {
            foreach (var type in assembly.GetTypes())
            {
                Visit(type);
                foreach (var field in type.GetFields(members)) Visit(field.FieldType);
                foreach (var property in type.GetProperties(members)) Visit(property.PropertyType);
                foreach (var method in type.GetMethods(members))
                {
                    Visit(method.ReturnType);
                    foreach (var parameter in method.GetParameters()) Visit(parameter.ParameterType);
                }
            }
            // Closed types used only by typeof/newobj inside prebuilt method bodies
            // need not appear in any field or public signature. Decode the actual
            // TypeSpec table without invoking library getters or initializers.
            using var stream = File.OpenRead(assembly.Location);
            using var pe = new PEReader(stream);
            var reader = pe.GetMetadataReader();
            var signature = new NativeParameterMetadata.SignatureTypes(context, assembly);
            for (var row = 1; row <= reader.GetTableRowCount(TableIndex.TypeSpec); row++)
                if (reader.GetTypeSpecification(MetadataTokens.TypeSpecificationHandle(row)).DecodeSignature(signature, null) is { } closed)
                    Visit(closed);
        }
        var selected = candidates.Values.Where(type => type.IsClass && contracts.Any(contract => contract.IsAssignableFrom(type)))
            .Concat(requested).Distinct().OrderBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal).ToArray();
        var properties = configuration ? new NativeConfigurationPropertyDescriptors(context, registry!, candidates.Values,
            Values("Neemle.DBAL.NativeConfigurationValues.v1").Concat(Values("Neemle.DBAL.NativeConfigurationPropertyTypes.v1"))
                .Distinct(StringComparer.Ordinal).Select(name => Named(name, true)!)) : null;
        var converters = properties == null ? null : NativeTypeConverterEmitter.Create(context, properties,
            Values("Neemle.DBAL.NativeTypeConverters.v1"));
        var rows = new List<object>();
        var errors = new List<object>();
        var invocationErrors = new List<object>();
        NativePropertyAccessorEmitter.Generate(core, [], (oracle ? "Neemle.DBAL.Oracle.Constructors." : configuration ? "Neemle.DBAL.Configuration.Constructors." : "Neemle.DBAL.Constructors.") + AssemblyName.GetAssemblyName(application).Name,
            output, (_, owner, _) =>
            {
                var obj = core.GetType("System.Object", true)!;
                var exception = core.GetType("System.Exception", true)!;
                var typeType = core.GetType("System.Type", true)!;
                var func = core.GetType("System.Func`1", true)!;
                var createDelegate = func.MakeGenericType(obj).GetConstructors().Single();
                var errorDelegate = func.MakeGenericType(exception).GetConstructors().Single();
                var registration = registry == null ? null : owner.DefineMethod("Register", MethodAttributes.Public | MethodAttributes.Static,
                    core.GetType("System.Void", true)!, Type.EmptyTypes).GetILGenerator();
                if (configuration && registration != null)
                {
                    // The default FileNotFoundException message differs between JIT
                    // and NativeAOT. Preserve configuration's ordinary loader error,
                    // including FileName, with the selected CLR's own format.
                    FileNotFoundException missing;
                    try { _ = Type.GetType("Neemle.DBAL.Inference.Unresolvable, Neemle.DBAL.Inference.MissingAssembly.3d978a409966470496677eaab2e48a7c", true); throw new InvalidOperationException("The missing-assembly probe unexpectedly resolved."); }
                    catch (FileNotFoundException error) { missing = error; }
                    var file = missing.FileName ?? throw new InvalidOperationException("The CLR loader omitted the missing assembly identity.");
                    var position = missing.Message.IndexOf(file, StringComparison.Ordinal);
                    if (position < 0 || missing.Message.IndexOf(file, position + file.Length, StringComparison.Ordinal) >= 0)
                        throw new InvalidOperationException("The CLR loader error format changed.");
                    var str = core.GetType("System.String", true)!;
                    var failureType = core.GetType(typeof(FileNotFoundException).FullName!, true)!;
                    var failure = owner.DefineMethod("MissingAssembly", MethodAttributes.Public | MethodAttributes.Static, failureType, [str]);
                    var body = failure.GetILGenerator();
                    body.Emit(OpCodes.Ldstr, missing.Message[..position]);
                    body.Emit(OpCodes.Ldarg_0);
                    body.Emit(OpCodes.Ldstr, missing.Message[(position + file.Length)..]);
                    body.Emit(OpCodes.Call, str.GetMethod("Concat", [str, str, str])!);
                    body.Emit(OpCodes.Ldarg_0);
                    body.Emit(OpCodes.Newobj, failureType.GetConstructor([str, str])!);
                    body.Emit(OpCodes.Ret);
                    registration.Emit(OpCodes.Ldnull);
                    registration.Emit(OpCodes.Ldftn, failure);
                    registration.Emit(OpCodes.Newobj, core.GetType("System.Func`2", true)!.MakeGenericType(str, failureType).GetConstructors().Single());
                    TypeLoadException emptyName;
                    try { _ = Type.GetType(string.Empty, true); throw new InvalidOperationException("The CLR accepted an empty type name."); }
                    catch (TypeLoadException error) { emptyName = error; }
                    var emptyType = core.GetType(typeof(TypeLoadException).FullName!, true)!;
                    var empty = owner.DefineMethod("EmptyTypeName", MethodAttributes.Public | MethodAttributes.Static, emptyType, Type.EmptyTypes);
                    var emptyBody = empty.GetILGenerator();
                    emptyBody.Emit(OpCodes.Ldstr, emptyName.Message);
                    emptyBody.Emit(OpCodes.Newobj, emptyType.GetConstructor([str])!);
                    emptyBody.Emit(OpCodes.Ret);
                    registration.Emit(OpCodes.Ldnull);
                    registration.Emit(OpCodes.Ldftn, empty);
                    registration.Emit(OpCodes.Newobj, func.MakeGenericType(emptyType).GetConstructors().Single());
                    registration.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(registry!.GetMethod("RegisterResolutionFailure")!));
                }
                for (var index = 0; index < selected.Length; index++)
                {
                    var type = selected[index];
                    var constructor = type.IsClass || type.IsValueType
                        ? type.GetConstructor(BindingFlags.Public | BindingFlags.Instance | (configuration ? BindingFlags.NonPublic : 0), null, Type.EmptyTypes, null) : null;
                    var invalid = type.IsAbstract || type.ContainsGenericParameters || type.IsByRefLike || type.IsPointer || type.IsByRef
                        || type == core.GetType("System.Void", true) || constructor == null && !type.IsValueType;
                    var method = owner.DefineMethod((invalid ? "Error_" : "New_") + index, MethodAttributes.Public | MethodAttributes.Static,
                        invalid ? exception : obj, Type.EmptyTypes);
                    var il = method.GetILGenerator();
                    if (invalid)
                    {
                        var probe = ValidationProbe(type, constructor != null, configuration);
                        EmitValidation(il, core, type, probe);
                        errors.Add(new { method = method.Name, type = type.AssemblyQualifiedName, exception = probe.Error.GetType().FullName });
                    }
                    else
                    {
                        if (constructor != null) il.Emit(OpCodes.Newobj, constructor);
                        else
                        {
                            var value = il.DeclareLocal(type);
                            il.Emit(OpCodes.Ldloca, value);
                            il.Emit(OpCodes.Initobj, type);
                            il.Emit(OpCodes.Ldloc, value);
                        }
                        if (type.IsValueType) il.Emit(OpCodes.Box, type);
                        il.Emit(OpCodes.Ret);
                        rows.Add(new { method = method.Name, type = type.AssemblyQualifiedName, token = constructor?.MetadataToken,
                            module = type.Module.ModuleVersionId, source = type.Assembly.Location,
                            sourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(type.Assembly.Location))) });
                    }
                    if (registration != null)
                    {
                        MethodBuilder? invokeError = null;
                        if (configuration && invalid && constructor != null)
                        {
                            invokeError = owner.DefineMethod("InvokeError_" + index, MethodAttributes.Public | MethodAttributes.Static,
                                exception, Type.EmptyTypes);
                            var probe = ValidationProbe(type, constructor != null, nonPublic: true, invoke: true);
                            EmitValidation(invokeError.GetILGenerator(), core, type, probe);
                            invocationErrors.Add(new { method = invokeError.Name, type = type.AssemblyQualifiedName, exception = probe.Error.GetType().FullName });
                        }
                        registration.Emit(OpCodes.Ldtoken, type);
                        registration.Emit(OpCodes.Call, typeType.GetMethod("GetTypeFromHandle")!);
                        if (invalid) registration.Emit(OpCodes.Ldnull);
                        registration.Emit(OpCodes.Ldnull);
                        registration.Emit(OpCodes.Ldftn, method);
                        registration.Emit(OpCodes.Newobj, invalid ? errorDelegate : createDelegate);
                        if (!invalid) registration.Emit(OpCodes.Ldnull);
                        if (configuration) registration.Emit(constructor != null ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                        if (configuration)
                        {
                            registration.Emit(OpCodes.Ldnull);
                            if (invokeError != null)
                            {
                                registration.Emit(OpCodes.Ldftn, invokeError);
                                registration.Emit(OpCodes.Newobj, errorDelegate);
                            }
                        }
                        registration.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(registry!.GetMethod("Register")!));
                    }
                }
                if (properties != null) properties.Emit(owner, registration!);
                converters?.Emit(owner, registration!);
                if (oracle && argumentContracts.Length != 0)
                    NativeArgumentConstructorEmitter.Emit(core, registry!, owner, registration!,
                        selected.Where(type => argumentContracts.Any(contract => contract.IsAssignableFrom(type)) || requested.Contains(type)), rows);
                if (oracle && fields.Length != 0) NativeOptionalFieldEmitter.Emit(core, registry!, owner, registration!, fields);
                registration?.Emit(OpCodes.Ret);
            }, selected.Select(type => type.Assembly).Concat(properties?.AccessAssemblies ?? [])
                .Concat(converters?.AccessAssemblies ?? [])
                .Concat(fields.SelectMany(field => new[] { field.Owner.Assembly, field.Field?.DeclaringType?.Assembly ?? field.Owner.Assembly })));
        File.WriteAllText(output + ".constructors.json", JsonSerializer.Serialize(new
        {
            metadataOnly = true, runtimeReflection = false, constructors = rows, validationErrors = errors, invocationErrors,
            contracts = contracts.Select(type => type.AssemblyQualifiedName), registry = registry?.AssemblyQualifiedName,
            nonPublicConstructors = configuration,
            optionalFields = fields.Select(field => new { owner = field.Owner.AssemblyQualifiedName, field.Name, flags = (int)field.Flags,
                declaring = field.Field?.DeclaringType?.AssemblyQualifiedName, token = field.Field?.MetadataToken }),
            propertyBags = properties?.Records,
            converterPolicies = converters?.Records,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))), nativeVerified = false
        }, new JsonSerializerOptions { WriteIndented = true }));

        void Visit(Type type)
        {
            if (type.IsByRef || type.IsPointer || type.IsArray) { Visit(type.GetElementType()!); return; }
            if (type.ContainsGenericParameters && !type.IsGenericTypeDefinition || type.IsFunctionPointer || type.IsByRefLike) return;
            if (!candidates.TryAdd(type.AssemblyQualifiedName!, type)) return;
            if (type.IsConstructedGenericType) foreach (var argument in type.GenericTypeArguments) Visit(argument);
        }
    }

    internal static (Type Type, Exception Error) ValidationProbe(Type target, bool publicConstructor, bool nonPublic = false, bool invoke = false)
    {
        var probe = target.ContainsGenericParameters ? typeof(OpenProbe<>)
            : target.IsInterface ? typeof(IProbe)
            : target.IsPointer ? typeof(MissingProbe<int>).MakePointerType()
            : target.IsByRef ? typeof(MissingProbe<int>).MakeByRefType()
            : target.IsByRefLike ? typeof(RefLikeProbe)
            : target.IsAbstract ? publicConstructor ? typeof(AbstractPublicProbe<int>) : typeof(AbstractProtectedProbe<int>)
            : target.IsArray ? typeof(MissingProbe<int>[])
            : target.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null) != null
                ? typeof(MissingProbe<int>) : typeof(NoConstructorProbe<int>);
        // Framework-only invalid construction cannot execute an application
        // initializer. Use the selected host's real special-type validations.
        if (target.Assembly.GetName().Name == typeof(object).Assembly.GetName().Name
            && typeof(object).Assembly.GetType(target.FullName!, false) is { } framework
            && (framework.IsAbstract || framework.ContainsGenericParameters || framework.IsByRefLike
                || framework.GetConstructor(Type.EmptyTypes) == null)) probe = framework;
        var probeConstructor = invoke ? probe.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null) : null;
        if (invoke && probeConstructor == null) throw new InvalidOperationException("The constructor invocation validation probe has no constructor.");
        try
        {
            _ = invoke ? probeConstructor!.Invoke(null)
                : nonPublic ? Activator.CreateInstance(probe, true) : Activator.CreateInstance(probe);
        }
        catch (Exception error) { return (probe, error); }
        throw new InvalidOperationException("The selected CLR did not reject an invalid constructor.");
    }

    internal static void EmitValidation(ILGenerator il, Assembly core, Type target, (Type Type, Exception Error) probe)
    {
        var text = probe.Error.Message;
        var parameter = (probe.Error as ArgumentException)?.ParamName;
        if (parameter != null)
        {
            var suffix = new ArgumentException(string.Empty, parameter).Message;
            if (!text.EndsWith(suffix, StringComparison.Ordinal)) throw new InvalidOperationException("The CLR argument resource changed.");
            text = text[..^suffix.Length];
        }
        var full = probe.Type.FullName!;
        var display = probe.Type.ToString();
        var usesFullName = text.Contains(full, StringComparison.Ordinal)
            && (!text.Contains(display, StringComparison.Ordinal) || full.Length >= display.Length);
        var name = usesFullName ? full : probe.Type.ToString();
        var offset = text.IndexOf(name, StringComparison.Ordinal);
        if (offset < 0) il.Emit(OpCodes.Ldstr, text);
        else
        {
            il.Emit(OpCodes.Ldstr, text[..offset]);
            var typeType = core.GetType("System.Type", true)!;
            il.Emit(OpCodes.Ldtoken, target);
            il.Emit(OpCodes.Call, typeType.GetMethod("GetTypeFromHandle")!);
            il.Emit(OpCodes.Callvirt, usesFullName ? typeType.GetProperty("FullName")!.GetMethod! : typeType.GetMethod("ToString", Type.EmptyTypes)!);
            il.Emit(OpCodes.Ldstr, text[(offset + name.Length)..]);
            var str = core.GetType("System.String", true)!;
            il.Emit(OpCodes.Call, str.GetMethod("Concat", [str, str, str])!);
        }
        var exceptionType = core.GetType(probe.Error.GetType().FullName!, true)!;
        var stringType = core.GetType("System.String", true)!;
        if (parameter != null)
        {
            il.Emit(OpCodes.Ldstr, parameter);
            il.Emit(OpCodes.Newobj, exceptionType.GetConstructor([stringType, stringType])!);
        }
        else il.Emit(OpCodes.Newobj, exceptionType.GetConstructor([stringType])!);
        il.Emit(OpCodes.Ret);
    }

    private sealed class NoConstructorProbe<T> { private NoConstructorProbe(int value) { } }
    private sealed class MissingProbe<T> { private MissingProbe() { } }
    private abstract class AbstractPublicProbe<T> { public AbstractPublicProbe() { } }
    private abstract class AbstractProtectedProbe<T> { protected AbstractProtectedProbe() { } }
    private sealed class OpenProbe<T> { public OpenProbe() { } }
    private interface IProbe { }
    private ref struct RefLikeProbe { }
}

// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Reflection;
using System.Reflection.Emit;

namespace Neemle.DBAL.Tools;

/// <summary>Compiles actual configuration converter and attribute policies into lazy executable operations.</summary>
internal sealed class NativeTypeConverterEmitter
{
    private readonly Assembly core, implementation;
    private readonly Type registry, converter, attribute, provider, typeType;
    private readonly NativeConfigurationPropertyDescriptors attributes;
    private readonly Type[] targets, owners, converters;
    private readonly Dictionary<Type, CustomAttributeData[]> raw;
    private readonly Dictionary<CustomAttributeData, Type?> providers = new();
    private readonly List<object> records = new();
    internal IReadOnlyList<object> Records => records;
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    internal static NativeTypeConverterEmitter? Create(MetadataLoadContext context, NativeConfigurationPropertyDescriptors properties, IEnumerable<string> requests)
    {
        var registry = properties.ConverterType.Assembly.GetType("Neemle.DBAL.Runtime.NativeTypeConverterOperations", false);
        return registry == null ? null : new(context, properties, registry, requests);
    }

    private NativeTypeConverterEmitter(MetadataLoadContext context, NativeConfigurationPropertyDescriptors properties, Type registry, IEnumerable<string> requests)
    {
        this.registry = registry;
        attributes = properties;
        core = context.CoreAssembly!;
        implementation = registry.Assembly;
        converter = properties.ConverterType;
        attribute = core.GetType("System.Attribute", true)!;
        typeType = core.GetType("System.Type", true)!;
        provider = implementation.GetType("System.ComponentModel.TypeDescriptionProvider", true)!;
        var converterAttribute = implementation.GetType("System.ComponentModel.TypeConverterAttribute", false)
            ?? context.LoadFromAssemblyName("System.ObjectModel").GetType("System.ComponentModel.TypeConverterAttribute", true)!;
        var providerAttribute = context.LoadFromAssemblyName("System.ObjectModel").GetType("System.ComponentModel.TypeDescriptionProviderAttribute", true)!;
        targets = properties.PropertyTypes.Where(type => !type.ContainsGenericParameters && !type.IsByRefLike && !type.IsPointer && !type.IsByRef
                && (type == properties.ElementType || !properties.ElementType.IsAssignableFrom(type)))
            .SelectMany(Underlying).Distinct().OrderBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal).ToArray();
        var pending = new Queue<Type>(targets);
        var closure = new HashSet<Type>();
        while (pending.TryDequeue(out var type))
        {
            if (!closure.Add(type)) continue;
            if (closure.Count > 4096) throw new InvalidOperationException("The inferred native configuration attribute graph exceeds4096closed shapes.");
            if (type.BaseType is { } parent) pending.Enqueue(parent);
            foreach (var contract in Interfaces(type)) pending.Enqueue(contract);
        }
        owners = closure.OrderBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal).ToArray();
        raw = owners.ToDictionary(type => type, type => type.GetCustomAttributesData().ToArray());
        var requested = new HashSet<Type>();
        foreach (var owner in owners)
            foreach (var data in raw[owner])
            {
                if (data.AttributeType == converterAttribute && Operand(data, owner) is { } selected) requested.Add(selected);
                if (data.AttributeType == providerAttribute) providers[data] = Operand(data, owner);
            }
        foreach (var request in requests)
            if (Named(request, null) is { } selected) requested.Add(selected);
        converters = requested.OrderBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal).ToArray();

        Type? Operand(CustomAttributeData data, Type owner)
        {
            if (data.ConstructorArguments.Count != 1) throw new NotSupportedException("Unexpected converter/provider attribute constructor: " + data);
            return data.ConstructorArguments[0].Value switch { Type type => type, string name => Named(name, owner), null => null,
                _ => throw new NotSupportedException("Unexpected converter/provider attribute operand: " + data) };
        }
        Type? Named(string name, Type? owner)
        {
            try
            {
                return Type.GetType(name, context.LoadFromAssemblyName,
                    (assembly, name, ignoreCase) => assembly?.GetType(name, false, ignoreCase)
                        ?? (assembly == null ? owner?.Assembly.GetType(name, false, ignoreCase)
                            ?? implementation.GetType(name, false, ignoreCase) ?? core.GetType(name, false, ignoreCase) : null), false);
            }
            catch (Exception error) when (error is FileNotFoundException or TypeLoadException or ArgumentException) { return null; }
        }
        IEnumerable<Type> Underlying(Type type)
        {
            yield return type;
            if (type.IsConstructedGenericType && type.GetGenericTypeDefinition() == core.GetType("System.Nullable`1"))
                yield return type.GenericTypeArguments[0];
        }
    }

    internal IEnumerable<Assembly> AccessAssemblies => owners.Select(type => type.Assembly).Concat(converters.Select(type => type.Assembly))
        .Concat(raw.Values.SelectMany(values => values).Select(data => data.AttributeType.Assembly))
        .Concat(providers.Values.OfType<Type>().Select(type => type.Assembly)).Append(implementation).Distinct();

    internal void Emit(TypeBuilder owner, ILGenerator registration)
    {
        var obj = core.GetType("System.Object", true)!;
        var exception = core.GetType("System.Exception", true)!;
        var func = core.GetType("System.Func`1", true)!;
        var configurationConverters = attributes.ElementType.Assembly.GetType("Neemle.DBAL.Runtime.NativeConfigurationConverters", true)!;
        registration.Emit(OpCodes.Ldnull);
        registration.Emit(OpCodes.Ldftn, NativePropertyAccessorEmitter.MethodForEmission(registry.GetMethod("GetConfigurationConverter")!));
        registration.Emit(OpCodes.Newobj, core.GetType("System.Func`2", true)!.MakeGenericType(typeType, converter).GetConstructors().Single());
        registration.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(configurationConverters.GetMethod("Register")!));
        var providerFunction = func.MakeGenericType(provider);
        var providerArray = providerFunction.MakeArrayType();
        for (var index = 0; index < owners.Length; index++)
        {
            var type = owners[index];
            var attributeFactory = owner.DefineMethod("ConverterAttributes_" + index, MethodAttributes.Public | MethodAttributes.Static, attribute.MakeArrayType(), Type.EmptyTypes);
            var body = attributeFactory.GetILGenerator();
            Array(body, attribute, raw[type].Length, position => attributes.EmitAttribute(body, raw[type][position]));
            body.Emit(OpCodes.Ret);
            var actualProviders = raw[type].Where(providers.ContainsKey).Reverse().ToArray();
            var providerFactories = new List<MethodBuilder>();
            foreach (var data in actualProviders)
            {
                var factory = owner.DefineMethod("ConverterProvider_" + index + "_" + providerFactories.Count,
                    MethodAttributes.Public | MethodAttributes.Static, provider, Type.EmptyTypes);
                var il = factory.GetILGenerator();
                var selected = providers[data];
                if (selected == null || !provider.IsAssignableFrom(selected)) { il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Ret); }
                else EmitConstruction(il, selected, null);
                providerFactories.Add(factory);
            }
            var prepare = owner.DefineMethod("PrepareConverterProviders_" + index, MethodAttributes.Public | MethodAttributes.Static, providerArray, Type.EmptyTypes);
            var preparation = prepare.GetILGenerator();
            // The CLR constructs all matching provider attributes before it
            // creates the first provider. Provider creation remains sequential.
            foreach (var data in raw[type].Where(providers.ContainsKey)) { attributes.EmitAttribute(preparation, data); preparation.Emit(OpCodes.Pop); }
            Array(preparation, providerFunction, providerFactories.Count, position => Delegate(preparation, providerFactories[position], provider));
            preparation.Emit(OpCodes.Ret);
            MethodBuilder? intrinsic = null;
            if (type.IsEnum)
            {
                intrinsic = owner.DefineMethod("ConverterIntrinsic_" + index, MethodAttributes.Public | MethodAttributes.Static, converter, Type.EmptyTypes);
                var il = intrinsic.GetILGenerator();
                TypeToken(il, type);
                il.Emit(OpCodes.Newobj, NativePropertyAccessorEmitter.ConstructorForEmission(implementation.GetType("System.ComponentModel.EnumConverter", true)!.GetConstructor([typeType])!));
                il.Emit(OpCodes.Ret);
            }
            TypeToken(registration, type);
            Delegate(registration, attributeFactory, attribute.MakeArrayType());
            if (type.BaseType == null) registration.Emit(OpCodes.Ldnull); else TypeToken(registration, type.BaseType);
            var interfaces = Interfaces(type);
            Array(registration, typeType, interfaces.Length, position => TypeToken(registration, interfaces[position]));
            if (intrinsic == null) registration.Emit(OpCodes.Ldnull); else Delegate(registration, intrinsic, converter);
            Delegate(registration, prepare, providerArray);
            registration.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(registry.GetMethod("Register")!));
            records.Add(new { owner = type.AssemblyQualifiedName, attributes = raw[type].Select(data => data.AttributeType.AssemblyQualifiedName),
                interfaces = interfaces.Select(type => type.AssemblyQualifiedName), providers = actualProviders.Select(data => providers[data]?.AssemblyQualifiedName), method = attributeFactory.Name });
        }
        var counter = 0;
        foreach (var selected in converters)
            foreach (var target in targets)
            {
                MethodBuilder? factory = null;
                MethodBuilder? error = null;
                if (converter.IsAssignableFrom(selected))
                {
                    var constructor = selected.GetConstructor([typeType]) ?? selected.GetConstructor(Type.EmptyTypes);
                    if (selected.ContainsGenericParameters || selected.IsAbstract || constructor == null)
                    {
                        error = owner.DefineMethod("ConverterError_" + counter++, MethodAttributes.Public | MethodAttributes.Static, exception, Type.EmptyTypes);
                        var validation = constructor?.GetParameters().Length == 1 && selected.IsAbstract
                            ? AbstractValidation() : NativeConstructorBridge.ValidationProbe(selected, constructor != null);
                        NativeConstructorBridge.EmitValidation(error.GetILGenerator(), core, selected, validation);
                    }
                    else
                    {
                        factory = owner.DefineMethod("ConverterNew_" + counter++, MethodAttributes.Public | MethodAttributes.Static, converter, Type.EmptyTypes);
                        EmitConstruction(factory.GetILGenerator(), selected, target);
                    }
                }
                TypeToken(registration, selected);
                TypeToken(registration, target);
                if (factory == null) registration.Emit(OpCodes.Ldnull); else Delegate(registration, factory, converter);
                if (error == null) registration.Emit(OpCodes.Ldnull); else Delegate(registration, error, exception);
                registration.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(registry.GetMethod("RegisterConverter")!));
                records.Add(new { converter = selected.AssemblyQualifiedName, target = target.AssemblyQualifiedName, method = factory?.Name, validation = error?.Name });
            }

        void EmitConstruction(ILGenerator il, Type selected, Type? target)
        {
            var constructor = (target == null ? null : selected.GetConstructor([typeType])) ?? selected.GetConstructor(Type.EmptyTypes);
            if (selected.ContainsGenericParameters || selected.IsAbstract || constructor == null)
            {
                var error = NativeConstructorBridge.ValidationProbe(selected, constructor != null);
                // EmitValidation returns an Exception; a constructor operation
                // throws it directly, before the outer invocation wrapper.
                var validation = owner.DefineMethod("ProviderValidation_" + providerValidationIndex++, MethodAttributes.Public | MethodAttributes.Static, exception, Type.EmptyTypes);
                NativeConstructorBridge.EmitValidation(validation.GetILGenerator(), core, selected, error);
                il.Emit(OpCodes.Call, validation);
                il.Emit(OpCodes.Throw);
                return;
            }
            var marker = constructor.GetCustomAttributesData().SingleOrDefault(data => data.AttributeType.Assembly == implementation
                && data.AttributeType.FullName == "Neemle.DBAL.Runtime.NativeTypeConverterConstructorAttribute");
            MethodInfo? replacement = null;
            if (marker != null)
            {
                var name = (string)marker.ConstructorArguments.Single().Value!;
                replacement = selected.GetMethod(name, Members, null, constructor.GetParameters().Select(parameter => parameter.ParameterType).ToArray(), null);
                if (replacement == null || !replacement.IsStatic || !selected.IsAssignableFrom(replacement.ReturnType))
                    throw new NotSupportedException("The native converter constructor factory does not match its actual signature: " + constructor);
            }
            LocalBuilder? result = null;
            if (target == null)
            {
                result = il.DeclareLocal(selected);
                il.BeginExceptionBlock();
            }
            if (constructor.GetParameters().Length == 1) TypeToken(il, target!);
            if (replacement != null) il.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(replacement));
            else il.Emit(OpCodes.Newobj, NativePropertyAccessorEmitter.ConstructorForEmission(constructor));
            if (result != null)
            {
                il.Emit(OpCodes.Stloc, result);
                il.BeginCatchBlock(exception);
                il.Emit(OpCodes.Newobj, core.GetType("System.Reflection.TargetInvocationException", true)!.GetConstructor([exception])!);
                il.Emit(OpCodes.Throw);
                il.EndExceptionBlock();
                il.Emit(OpCodes.Ldloc, result);
            }
            il.Emit(OpCodes.Ret);
        }
        void Delegate(ILGenerator il, MethodBuilder method, Type result)
        {
            il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Ldftn, method);
            il.Emit(OpCodes.Newobj, func.MakeGenericType(result).GetConstructors().Single());
        }
        void TypeToken(ILGenerator il, Type type)
        {
            il.Emit(OpCodes.Ldtoken, type);
            il.Emit(OpCodes.Call, typeType.GetMethod("GetTypeFromHandle")!);
        }
        static void Array(ILGenerator il, Type element, int count, Action<int> emit)
        {
            il.Emit(OpCodes.Ldc_I4, count); il.Emit(OpCodes.Newarr, element);
            for (var index = 0; index < count; index++) { il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldc_I4, index); emit(index); il.Emit(OpCodes.Stelem_Ref); }
        }
    }
    private int providerValidationIndex;
    private static Type[] Interfaces(Type type) => type.GetInterfaces().Where(contract =>
        (contract.Attributes & (TypeAttributes.Public | TypeAttributes.NestedPublic)) != 0).ToArray();
    private static (Type, Exception) AbstractValidation()
    {
        try { _ = typeof(AbstractConverterProbe).GetConstructor([typeof(Type)])!.Invoke([typeof(object)]); }
        catch (MemberAccessException error) { return (typeof(AbstractConverterProbe), error); }
        throw new InvalidOperationException("The CLR accepted an abstract converter constructor.");
    }
    private abstract class AbstractConverterProbe { public AbstractConverterProbe(Type type) { } }
}

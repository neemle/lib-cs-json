// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;

namespace Neemle.DBAL.Tools;

/// <summary>Emits executable public constructors; retained signatures serve only the original CLR overload binder.</summary>
internal static class NativeArgumentConstructorEmitter
{
    internal static void Emit(Assembly core, Type registry, TypeBuilder owner, ILGenerator registration,
        IEnumerable<Type> inferred, List<object> records)
    {
        var obj = core.GetType("System.Object", true)!;
        var exception = core.GetType("System.Exception", true)!;
        var typeType = core.GetType("System.Type", true)!;
        var methodBase = core.GetType("System.Reflection.MethodBase", true)!;
        var constructorInfo = core.GetType("System.Reflection.ConstructorInfo", true)!;
        var getType = typeType.GetMethod("GetTypeFromHandle")!;
        var getMethod = methodBase.GetMethods().Single(method => method.Name == "GetMethodFromHandle" && method.GetParameters().Length == 2);
        var factoryType = core.GetType("System.Func`2", true)!.MakeGenericType(obj.MakeArrayType(), obj);
        var errorType = core.GetType("System.Func`1", true)!.MakeGenericType(exception);
        var convert = registry.GetMethod("ConvertArgument")!;
        var index = 0;
        foreach (var type in inferred.Distinct().OrderBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal))
        {
            var missing = owner.DefineMethod("MissingArgumentConstructor_" + index++, MethodAttributes.Public | MethodAttributes.Static, exception, Type.EmptyTypes);
            var probe = type.ContainsGenericParameters ? typeof(OpenProbe<>) : typeof(MissingProbe<int>);
            Exception error;
            try { _ = Activator.CreateInstance(probe, "argument", 1); throw new InvalidOperationException("The selected CLR accepted a missing constructor."); }
            catch (Exception caught) when (caught is MissingMethodException or ArgumentException) { error = caught; }
            NativeConstructorBridge.EmitValidation(missing.GetILGenerator(), core, type, (probe, error));
            TypeToken(type);
            Error(missing);
            registration.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(registry.GetMethod("RegisterArgumentType")!));
            if (type.ContainsGenericParameters || type.IsInterface || type.IsArray || type.IsByRefLike || type.IsPointer || type.IsByRef) continue;
            foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).OrderBy(member => member.MetadataToken))
            {
                var parameters = constructor.GetParameters();
                if ((constructor.CallingConvention & CallingConventions.VarArgs) != 0
                    || parameters.Any(parameter => Invalid(parameter.ParameterType)))
                    throw new NotSupportedException("The inferred native constructor has an unrepresentable argument signature: " + constructor);
                var factory = owner.DefineMethod("NewArguments_" + index++, MethodAttributes.Public | MethodAttributes.Static, obj, [obj.MakeArrayType()]);
                var body = factory.GetILGenerator();
                var byrefs = new List<(int Index, Type Type, LocalBuilder Local)>();
                for (var position = 0; position < parameters.Length; position++)
                {
                    var parameter = parameters[position].ParameterType;
                    var valueType = parameter.IsByRef ? parameter.GetElementType()! : parameter;
                    body.Emit(OpCodes.Ldarg_0);
                    body.Emit(OpCodes.Ldc_I4, position);
                    body.Emit(OpCodes.Ldelem_Ref);
                    body.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(convert.MakeGenericMethod(valueType)));
                    if (parameter.IsByRef)
                    {
                        var local = body.DeclareLocal(valueType);
                        body.Emit(OpCodes.Stloc, local);
                        body.Emit(OpCodes.Ldloca, local);
                        byrefs.Add((position, valueType, local));
                    }
                }
                if (type.IsAbstract)
                {
                    // The generated validation operation runs before this factory.
                    body.Emit(OpCodes.Ldnull);
                    body.Emit(OpCodes.Throw);
                }
                else
                {
                    body.Emit(OpCodes.Newobj, NativePropertyAccessorEmitter.ConstructorForEmission(constructor));
                    if (type.IsValueType) body.Emit(OpCodes.Box, type);
                    if (byrefs.Count != 0)
                    {
                        var result = body.DeclareLocal(obj);
                        body.Emit(OpCodes.Stloc, result);
                        foreach (var argument in byrefs)
                        {
                            body.Emit(OpCodes.Ldarg_0);
                            body.Emit(OpCodes.Ldc_I4, argument.Index);
                            body.Emit(OpCodes.Ldloc, argument.Local);
                            if (argument.Type.IsValueType) body.Emit(OpCodes.Box, argument.Type);
                            body.Emit(OpCodes.Stelem_Ref);
                        }
                        body.Emit(OpCodes.Ldloc, result);
                    }
                    body.Emit(OpCodes.Ret);
                }
                MethodBuilder? validation = null;
                if (type.IsAbstract)
                {
                    validation = owner.DefineMethod("ArgumentValidation_" + index++, MethodAttributes.Public | MethodAttributes.Static, exception, Type.EmptyTypes);
                    Exception abstractError;
                    try { _ = Activator.CreateInstance(typeof(AbstractProbe<int>), "argument", 1); throw new InvalidOperationException("The selected CLR accepted an abstract constructor."); }
                    catch (MemberAccessException caught) { abstractError = caught; }
                    NativeConstructorBridge.EmitValidation(validation.GetILGenerator(), core, type, (typeof(AbstractProbe<int>), abstractError));
                }
                TypeToken(type);
                // ILGenerator's ConstructorInfo overload accepts call/newobj but
                // rejects ldtoken. Obtain the actual MemberRef from the same
                // persisted module, then emit that token's raw four-byte operand.
                var constructorToken = ((ModuleBuilder)owner.Module).GetMethodMetadataToken(
                    NativePropertyAccessorEmitter.ConstructorForEmission(constructor));
                registration.Emit(OpCodes.Ldtoken, constructorToken);
                registration.Emit(OpCodes.Ldtoken, type);
                registration.Emit(OpCodes.Call, getMethod);
                registration.Emit(OpCodes.Castclass, constructorInfo);
                registration.Emit(OpCodes.Ldnull);
                registration.Emit(OpCodes.Ldftn, factory);
                registration.Emit(OpCodes.Newobj, factoryType.GetConstructors().Single());
                if (validation == null) registration.Emit(OpCodes.Ldnull); else Error(validation);
                registration.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(registry.GetMethod("RegisterArgumentConstructor")!));
                records.Add(new { method = factory.Name, type = type.AssemblyQualifiedName, token = constructor.MetadataToken,
                    module = type.Module.ModuleVersionId, source = type.Assembly.Location,
                    sourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(type.Assembly.Location))),
                    argumentConstructor = true, parameters = parameters.Select(parameter => parameter.ParameterType.AssemblyQualifiedName) });
            }
        }

        void TypeToken(Type type)
        {
            registration.Emit(OpCodes.Ldtoken, type);
            registration.Emit(OpCodes.Call, getType);
        }
        void Error(MethodBuilder method)
        {
            registration.Emit(OpCodes.Ldnull);
            registration.Emit(OpCodes.Ldftn, method);
            registration.Emit(OpCodes.Newobj, errorType.GetConstructors().Single());
        }
        static bool Invalid(Type type)
            => type.IsPointer || type.IsFunctionPointer || type.IsByRefLike || type.ContainsGenericParameters
                || type.IsByRef && Invalid(type.GetElementType()!);
    }

    private sealed class MissingProbe<T> { private MissingProbe(int ignored) { } }
    private sealed class OpenProbe<T> { public OpenProbe(string name, int value) { } }
    private abstract class AbstractProbe<T> { public AbstractProbe(string name, int value) { } }
}

// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Neemle.DBAL.Tools;

/// <summary>Infers closed compiler initializer operations from actual accessor-handle IL.</summary>
internal static class NativeExpressionInitializerOperations
{
    internal const string AttributeName = "Neemle.DBAL.Inferred.NativeExpressionInitializerAttribute";
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    internal sealed record Site(string Source, Guid Module, int MethodToken, int Offset, string PeSha256,
        string IlSha256, PropertyInfo Property, MethodInfo Accessor)
    {
        internal string Name => $"Bind_{Module:N}_{MethodToken:x8}_{Offset:x8}";
    }
    private sealed record Value(EntityHandle Token, MethodInfo? Accessor = null);
    private sealed record Instruction(int Offset, OpCode Code, int Operand, int End);
    private static readonly Dictionary<ushort, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
        .GroupBy(code => unchecked((ushort)code.Value)).ToDictionary(group => group.Key, group => group.First());

    internal static IReadOnlyList<Site> Infer(MetadataLoadContext context, IEnumerable<Assembly> assemblies)
    {
        var result = new List<Site>();
        var expression = context.LoadFromAssemblyName("System.Linq.Expressions").GetType("System.Linq.Expressions.Expression");
        foreach (var assembly in assemblies.OrderBy(value => value.FullName, StringComparer.Ordinal))
        {
            using var image = new PEReader(File.OpenRead(assembly.Location));
            var reader = image.GetMetadataReader();
            // No source names or application type catalogs participate in selection.
            if (!reader.MemberReferences.Any(handle => reader.GetString(reader.GetMemberReference(handle).Name) == "Bind")) continue;
            var signatures = new NativeParameterMetadata.SignatureTypes(context, assembly);
            var mvid = reader.GetGuid(reader.GetModuleDefinition().Mvid);
            var peHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)));
            foreach (var handle in reader.MethodDefinitions)
            {
                var definition = reader.GetMethodDefinition(handle);
                if (definition.RelativeVirtualAddress == 0 || definition.GetGenericParameters().Count != 0) continue;
                var declaring = Type(definition.GetDeclaringType());
                if (declaring == null || declaring.ContainsGenericParameters) continue;
                var body = image.GetMethodBody(definition.RelativeVirtualAddress);
                var bytes = body.GetILBytes()!;
                var instructions = Read(bytes);
                // Only straight-line provenance within a basic block is accepted.
                // Branch targets and exceptional entries discard all tracked values.
                var entries = new HashSet<int>(body.ExceptionRegions.SelectMany(region => new[] { region.HandlerOffset, region.FilterOffset }));
                foreach (var instruction in instructions)
                {
                    if (instruction.Code.OperandType == OperandType.ShortInlineBrTarget)
                        entries.Add(instruction.End + unchecked((sbyte)instruction.Operand));
                    else if (instruction.Code.OperandType == OperandType.InlineBrTarget)
                        entries.Add(instruction.End + instruction.Operand);
                    else if (instruction.Code.OperandType == OperandType.InlineSwitch)
                    {
                        var count = BitConverter.ToInt32(bytes, instruction.Offset + instruction.Code.Size);
                        for (var index = 0; index < count; index++) entries.Add(instruction.End + BitConverter.ToInt32(bytes, instruction.Offset + instruction.Code.Size + 4 + index * 4));
                    }
                }
                var stack = new List<Value?>();
                foreach (var instruction in instructions)
                {
                    if (entries.Contains(instruction.Offset)) stack.Clear();
                    var code = instruction.Code;
                    if (code == OpCodes.Ldtoken) { stack.Add(new(MetadataTokens.EntityHandle(instruction.Operand))); continue; }
                    if (code == OpCodes.Dup) { stack.Add(stack.Count == 0 ? null : stack[^1]); continue; }
                    if (code == OpCodes.Castclass)
                    {
                        var prior = Pop();
                        stack.Add(Type(MetadataTokens.EntityHandle(instruction.Operand)) == context.CoreAssembly!.GetType("System.Reflection.MethodInfo") ? prior : null);
                        continue;
                    }
                    if (code == OpCodes.Call || code == OpCodes.Callvirt || code == OpCodes.Newobj)
                    {
                        var target = MetadataTokens.EntityHandle(instruction.Operand);
                        if (!Signature(target, out var owner, out var name, out var signature)) { stack.Clear(); continue; }
                        var arguments = new Value?[signature.ParameterTypes.Length];
                        for (var index = arguments.Length - 1; index >= 0; index--) arguments[index] = Pop();
                        if (signature.Header.IsInstance && code != OpCodes.Newobj) _ = Pop();
                        Value? returned = null;
                        if (owner == context.CoreAssembly!.GetType("System.Reflection.MethodBase") && name == "GetMethodFromHandle"
                            && arguments.Length is 1 or 2 && arguments[0] is { Accessor: null } methodToken)
                        {
                            var method = Method(methodToken.Token);
                            if (method is { ContainsGenericParameters: false } && (arguments.Length == 1
                                || arguments[1] is { } typeToken && Type(typeToken.Token) == method.DeclaringType)) returned = new(default, method);
                        }
                        if (owner == expression && name == "Bind" && arguments.Length == 2
                            && signature.ParameterTypes[0] == context.CoreAssembly.GetType("System.Reflection.MethodInfo")
                            && signature.ParameterTypes[1] == expression && arguments[0]?.Accessor is { } accessor
                            && accessor.DeclaringType is { ContainsGenericParameters: false } accessorOwner)
                        {
                            var matches = accessorOwner.GetProperties(Declared).Where(property => property.GetMethod == accessor || property.SetMethod == accessor).ToArray();
                            // A true, closed accessor is enough for the ordinary Bind validation;
                            // arbitrary methods and custom MethodInfo implementations remain unproved.
                            if (matches.Length == 1 && !accessor.IsGenericMethodDefinition)
                                result.Add(new(assembly.Location, mvid, MetadataTokens.GetToken(handle), instruction.Offset, peHash,
                                    Convert.ToHexString(SHA256.HashData(bytes)), matches[0], accessor));
                        }
                        if (code == OpCodes.Newobj || signature.ReturnType != context.CoreAssembly.GetType("System.Void")) stack.Add(returned);
                        continue;
                    }
                    var pop = Count(code.StackBehaviourPop, false);
                    var push = Count(code.StackBehaviourPush, true);
                    if (pop < 0 || push < 0) stack.Clear();
                    else { for (var index = 0; index < pop; index++) _ = Pop(); for (var index = 0; index < push; index++) stack.Add(null); }
                    if (code.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Return or FlowControl.Throw) stack.Clear();
                }
                Value? Pop() { if (stack.Count == 0) return null; var value = stack[^1]; stack.RemoveAt(stack.Count - 1); return value; }
            }

            Type? Type(EntityHandle handle) => handle.Kind switch
            {
                HandleKind.TypeDefinition => signatures.GetTypeFromDefinition(reader, (TypeDefinitionHandle)handle, 0),
                HandleKind.TypeReference => signatures.GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0),
                HandleKind.TypeSpecification => signatures.GetTypeFromSpecification(reader, null, (TypeSpecificationHandle)handle, 0),
                _ => null
            };
            bool Signature(EntityHandle handle, out Type? owner, out string name, out MethodSignature<Type?> signature)
            {
                owner = null; name = ""; signature = default;
                if (handle.Kind == HandleKind.MethodSpecification) handle = reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
                if (handle.Kind == HandleKind.MethodDefinition)
                {
                    var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle); owner = Type(method.GetDeclaringType()); name = reader.GetString(method.Name);
                    signature = method.DecodeSignature(signatures, null); return true;
                }
                if (handle.Kind != HandleKind.MemberReference) return false;
                var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                if (member.GetKind() != MemberReferenceKind.Method) return false;
                owner = Type(member.Parent); name = reader.GetString(member.Name);
                signature = member.DecodeMethodSignature(signatures, owner?.IsGenericType == true
                    ? new NativeParameterMetadata.GenericContext([..owner.GetGenericArguments()], []) : null); return true;
            }
            MethodInfo? Method(EntityHandle handle)
            {
                if (!Signature(handle, out var owner, out var name, out var signature) || owner == null) return null;
                var matches = owner.GetMethods(Declared).Where(method => method.Name == name && !method.ContainsGenericParameters
                    && method.ReturnType == signature.ReturnType && method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(signature.ParameterTypes)).ToArray();
                return matches.Length == 1 ? matches[0] : null;
            }
        }
        return result.OrderBy(site => site.Module).ThenBy(site => site.MethodToken).ThenBy(site => site.Offset).ToArray();
    }

    internal static void Emit(Assembly core, Assembly expressions, ModuleBuilder module, TypeBuilder owner, IReadOnlyList<Site> sites)
    {
        if (sites.Count == 0) return;
        var text = core.GetType("System.String", true)!;
        var integer = core.GetType("System.Int32", true)!;
        var attribute = core.GetType("System.Attribute", true)!;
        var annotation = module.DefineType(AttributeName, TypeAttributes.Public | TypeAttributes.Sealed, attribute);
        var annotationConstructor = annotation.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard,
            [text, integer, integer, text, text, text, integer]);
        var annotationBody = annotationConstructor.GetILGenerator();
        annotationBody.Emit(OpCodes.Ldarg_0);
        annotationBody.Emit(OpCodes.Call, NativePropertyAccessorEmitter.ConstructorForEmission(attribute.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, [], null)!));
        annotationBody.Emit(OpCodes.Ret);
        annotation.CreateType();
        var protocol = core.GetType("Neemle.DBAL.Runtime.NativeMemberHandles", true)!;
        var marker = core.GetType("Neemle.DBAL.Runtime.NativeMemberHandleAttribute", true)!.GetConstructors(Declared).Single();
        var propertyFactory = protocol.GetMethod("PropertyFromRecord", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new NotSupportedException("Initializer operation lowering requires the selected native property-record protocol.");
        var expression = expressions.GetType("System.Linq.Expressions.Expression", true)!;
        var memberInfo = core.GetType("System.Reflection.MemberInfo", true)!;
        var bind = expression.GetMethod("Bind", [memberInfo, expression])!;
        var records = new Dictionary<(Type, int), FieldBuilder>();
        foreach (var site in sites)
        {
            var key = (site.Property.DeclaringType!, site.Property.MetadataToken);
            if (!records.TryGetValue(key, out var record))
            {
                record = owner.DefineInitializedData("InitializerProperty_" + records.Count, new byte[16], FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly);
                var descriptor = new BlobBuilder(); descriptor.WriteUInt16(1);
                descriptor.WriteSerializedString(site.Property.DeclaringType!.AssemblyQualifiedName!);
                descriptor.WriteInt32(site.Property.MetadataToken); descriptor.WriteInt32(1); descriptor.WriteSerializedString(""); descriptor.WriteUInt16(0);
                record.SetCustomAttribute(NativePropertyAccessorEmitter.ConstructorForEmission(marker), descriptor.ToArray()); records.Add(key, record);
            }
            var method = owner.DefineMethod(site.Name, MethodAttributes.Public | MethodAttributes.Static,
                expressions.GetType("System.Linq.Expressions.MemberAssignment", true)!, [core.GetType("System.Reflection.MethodInfo", true)!, expression]);
            var proof = new BlobBuilder(); proof.WriteUInt16(1); proof.WriteSerializedString(site.Module.ToString("N"));
            proof.WriteInt32(site.MethodToken); proof.WriteInt32(site.Offset); proof.WriteSerializedString(site.PeSha256); proof.WriteSerializedString(site.IlSha256);
            proof.WriteSerializedString(site.Accessor.DeclaringType!.AssemblyQualifiedName!); proof.WriteInt32(site.Accessor.MetadataToken); proof.WriteUInt16(0);
            method.SetCustomAttribute(annotationConstructor, proof.ToArray());
            var il = method.GetILGenerator();
            // The incoming accessor has already been evaluated by the original IL.
            // Its exact identity is proved by the build operation. Construct the
            // real CLR PropertyInfo directly, then retain ordinary Bind validation.
            il.Emit(OpCodes.Ldsflda, record); il.Emit(OpCodes.Conv_U);
            il.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(propertyFactory));
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Call, NativePropertyAccessorEmitter.MethodForEmission(bind)); il.Emit(OpCodes.Ret);
        }
    }

    private static IReadOnlyList<Instruction> Read(byte[] bytes)
    {
        var result = new List<Instruction>(); var offset = 0;
        while (offset < bytes.Length)
        {
            var start = offset; var value = (ushort)bytes[offset++]; if (value == 0xfe) value = (ushort)(0xfe00 | bytes[offset++]);
            if (!Codes.TryGetValue(value, out var code)) throw new BadImageFormatException("Unknown initializer IL opcode.");
            var size = code.OperandType switch { OperandType.InlineNone => 0, OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2, OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => checked(4 + BitConverter.ToInt32(bytes, offset) * 4), _ => 4 };
            if (size < 0 || offset + size > bytes.Length) throw new BadImageFormatException("Truncated initializer IL.");
            var operand = size == 0 ? 0 : size == 1 ? bytes[offset] : size == 2 ? BitConverter.ToUInt16(bytes, offset) : BitConverter.ToInt32(bytes, offset);
            offset += size; result.Add(new(start, code, operand, offset));
        }
        return result;
    }
    private static int Count(StackBehaviour behavior, bool push)
        => behavior == StackBehaviour.Pop0 || behavior == StackBehaviour.Push0 ? 0
            : behavior is StackBehaviour.Varpop or StackBehaviour.Varpush ? -1
            : behavior.ToString().Split('_').Length;
}

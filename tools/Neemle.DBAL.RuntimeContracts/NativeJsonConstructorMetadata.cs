// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Neemle.DBAL.Tools;

/// <summary>Plans the selected JSON object factory's constructor-attribute role without running application code.</summary>
internal sealed class NativeJsonConstructorMetadata
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private readonly MetadataLoadContext context;
    private readonly Assembly json;
    private readonly HashSet<Type> simple = new();
    private readonly Type enumerable;
    private readonly Type node;
    private readonly Type pair;
    private readonly Type jsonConstructor;
    private readonly string mappingName;
    private readonly long recordFlag;
    private readonly long kindMask;
    private readonly long unionFlag;
    private readonly Type flagsUnderlyingType;
    private readonly HashSet<string> fsharpSpecialNames = new(StringComparer.Ordinal);
    internal sealed record Selection(ConstructorInfo? Constructor, string Reason, bool Unsupported = false);
    internal sealed record ReadRoles(bool ObjectMembers, bool FSharpTypeAttributes, string Reason, bool Unsupported = false);

    internal ReadRoles Roles(Type type)
    {
        var core = context.CoreAssembly!;
        var converterAttribute = json.GetType("System.Text.Json.Serialization.JsonConverterAttribute", true)!;
        var custom = type.GetCustomAttributesData().FirstOrDefault(data => converterAttribute.IsAssignableFrom(data.AttributeType));
        if (custom != null)
        {
            var converter = custom.AttributeType == converterAttribute && custom.ConstructorArguments.Count == 1
                ? custom.ConstructorArguments[0].Value as Type : null;
            var factory = json.GetType("System.Text.Json.Serialization.JsonConverterFactory", true)!;
            if (converter != null && !factory.IsAssignableFrom(converter))
                for (var current = converter; current != null; current = current.BaseType)
                    if (current.IsConstructedGenericType && current.GetGenericTypeDefinition() == json.GetType("System.Text.Json.Serialization.JsonConverter`1", true))
                        return new(false, false, "canonical attributed value converter");
            return new(false, false, "The advertised JSON converter has no statically proven object/member attribute-read contract; dynamic converter factories and overridden attribute factories require an explicit serialization contract.", true);
        }
        var nullable = core.GetType("System.Nullable`1", true)!;
        var member = core.GetType("System.Reflection.MemberInfo", true)!;
        var serializationInfo = core.GetType("System.Runtime.Serialization.SerializationInfo", true)!;
        if (simple.Contains(type) || type.IsEnum || node.IsAssignableFrom(type)
            || type.IsConstructedGenericType && type.GetGenericTypeDefinition() == nullable
            || member.IsAssignableFrom(type) || core.GetType("System.Delegate", true)!.IsAssignableFrom(type)
            || type == serializationInfo || type == core.GetType("System.IntPtr", true) || type == core.GetType("System.UIntPtr", true))
            return new(false, false, "canonical converter selected before the FSharp factory");
        var mapping = type.GetCustomAttributesData().FirstOrDefault(data => data.AttributeType.FullName == mappingName);
        if (mapping != null)
        {
            var definition = type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;
            if (definition.Assembly == mapping.AttributeType.Assembly && fsharpSpecialNames.Contains(definition.FullName!))
                return new(false, true, "canonical FSharp special converter");
            if (!TryFlags(mapping, out var flags))
                return new(false, true, "The advertised FSharp compilation mapping has no statically proven SourceConstructFlags getter/constructor contract; this native member-attribute operation requires a proven serialization role.", true);
            if ((flags & kindMask) == recordFlag) return new(true, true, "proven FSharp record object converter");
            if ((flags & kindMask) == unionFlag) return new(false, true, "canonical unsupported FSharp union converter");
        }
        var memory = core.GetType("System.Memory`1", true)!;
        var readOnlyMemory = core.GetType("System.ReadOnlyMemory`1", true)!;
        var asyncEnumerable = core.GetType("System.Collections.Generic.IAsyncEnumerable`1", true)!;
        if (type.HasElementType || enumerable.IsAssignableFrom(type)
            || type.IsConstructedGenericType && (type.GetGenericTypeDefinition() == memory || type.GetGenericTypeDefinition() == readOnlyMemory)
            || type.GetInterfaces().Any(contract => contract.IsConstructedGenericType && contract.GetGenericTypeDefinition() == asyncEnumerable))
            return new(false, true, "canonical collection converter after the FSharp factory");
        return new(true, true, "canonical object converter");
    }

    internal NativeJsonConstructorMetadata(MetadataLoadContext context, Assembly json)
    {
        this.context = context;
        this.json = json;
        enumerable = context.CoreAssembly!.GetType("System.Collections.IEnumerable", true)!;
        pair = context.CoreAssembly.GetType("System.Collections.Generic.KeyValuePair`2", true)!;
        node = json.GetType("System.Text.Json.Nodes.JsonNode", true)!;
        jsonConstructor = json.GetType("System.Text.Json.Serialization.JsonConstructorAttribute", true)!;
        var proxy = json.GetType("System.Text.Json.Serialization.Metadata.FSharpCoreReflectionProxy", true)!;
        mappingName = (string)proxy.GetField("CompilationMappingAttributeTypeName", Declared)!.GetRawConstantValue()!;
        var flags = proxy.GetNestedType("SourceConstructFlags", BindingFlags.NonPublic)!;
        flagsUnderlyingType = flags.GetEnumUnderlyingType();
        recordFlag = Convert.ToInt64(flags.GetField("RecordType")!.GetRawConstantValue());
        kindMask = Convert.ToInt64(flags.GetField("KindMask")!.GetRawConstantValue());
        unionFlag = Convert.ToInt64(flags.GetField("SumType")!.GetRawConstantValue());
        // Discover special FSharp converter receivers from the selected proxy's own Type fields and IL.
        var specialFields = Read(proxy.GetMethod("DetectFSharpKind", Declared)!).Where(item => item.Code == OpCodes.Ldfld).Select(item => item.Operand).ToHashSet();
        using (var image = new PEReader(File.OpenRead(json.Location)))
        {
            var metadata = image.GetMetadataReader();
            var instructions = Read(proxy.GetConstructors(Declared).Single(ctor => !ctor.IsStatic));
            for (var i = 0; i + 2 < instructions.Count; i++)
                if (instructions[i].Code == OpCodes.Ldstr && instructions[i + 1].Code == OpCodes.Callvirt
                    && instructions[i + 2].Code == OpCodes.Stfld && specialFields.Contains(instructions[i + 2].Operand))
                {
                    var call = MetadataTokens.EntityHandle((int)instructions[i + 1].Operand);
                    if (call.Kind != HandleKind.MemberReference) continue;
                    var reference = metadata.GetMemberReference((MemberReferenceHandle)call);
                    if (metadata.GetString(reference.Name) == "GetType")
                        fsharpSpecialNames.Add(metadata.GetUserString(MetadataTokens.UserStringHandle((int)instructions[i].Operand & 0x00ffffff)));
                }
        }
        // The simple converter domain comes from the selected producer's actual calls, never a CLR type catalog.
        var resolver = json.GetType("System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver", true)!;
        var method = resolver.GetMethod("GetDefaultSimpleConverters", Declared)!;
        var services = json.GetType("System.Text.Json.Serialization.Metadata.JsonMetadataServices", true)!;
        var converter = json.GetType("System.Text.Json.Serialization.JsonConverter`1", true)!;
        using var pe = new PEReader(File.OpenRead(json.Location));
        var reader = pe.GetMetadataReader();
        var definition = reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(method.MetadataToken & 0x00ffffff));
        var calls = MetadataMethodOperands.Read(pe.GetMethodBody(definition.RelativeVirtualAddress));
        foreach (var candidate in services.GetMethods(Declared))
            if (candidate.ReturnType.IsConstructedGenericType && candidate.ReturnType.GetGenericTypeDefinition() == converter
                && calls.Any(call => call.Code == OpCodes.Call && MetadataTokens.GetToken(call.Handle) == candidate.MetadataToken))
                simple.Add(candidate.ReturnType.GetGenericArguments()[0]);
        if (simple.Count == 0) throw new NotSupportedException("The selected JSON producer has no proven simple-converter metadata contract.");
    }

    internal Selection Select(Type type)
    {
        var roles = Roles(type);
        if (!roles.ObjectMembers) return new(null, roles.Reason, roles.Unsupported);
        if (type.IsInterface) return new(null, "canonical interface object converter");
        var mapping = type.GetCustomAttributesData().FirstOrDefault(data => data.AttributeType.FullName == mappingName);
        var record = false;
        if (mapping != null)
        {
            var definition = type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;
            if (definition.Assembly == mapping.AttributeType.Assembly && fsharpSpecialNames.Contains(definition.FullName!))
                return new(null, "canonical FSharp special converter");
            if (!TryFlags(mapping, out var flags))
            {
                return new(null, "The advertised FSharp compilation mapping has no statically proven SourceConstructFlags getter/constructor contract; this native constructor-attribute operation requires a proven serialization constructor.", true);
            }
            else
            {
                record = (flags & kindMask) == recordFlag;
                if ((flags & kindMask) == unionFlag) return new(null, "canonical unsupported FSharp union converter");
            }
        }
        if (!record && enumerable.IsAssignableFrom(type)) return new(null, "canonical enumerable converter");
        var candidates = type.GetConstructors(Declared).Where(ctor => !ctor.IsStatic).ToArray();
        var marked = candidates.Where(ctor => ctor.GetCustomAttributesData().Any(data => data.AttributeType == jsonConstructor)).ToArray();
        if (marked.Length != 0) return marked.Length == 1 ? new(marked[0], "JsonConstructor") : new(null, "duplicate JsonConstructor declarations");
        if (type.IsValueType && !(type.IsConstructedGenericType && type.GetGenericTypeDefinition() == pair) && !record)
            return new(null, "unannotated value type default constructor");
        var visible = candidates.Where(ctor => ctor.IsPublic).ToArray();
        var selected = visible.FirstOrDefault(ctor => ctor.GetParameters().Length == 0) ?? (visible.Length == 1 ? visible[0] : null);
        return new(selected, record ? "proven FSharp record object converter" : selected == null ? "no selected public constructor" : "public object constructor");
    }

    private bool TryFlags(CustomAttributeData data, out long flags)
    {
        flags = 0;
        if (data.NamedArguments.Count != 0) return false;
        var properties = data.AttributeType.GetProperties(Declared).Where(property => property.Name == "SourceConstructFlags" && property.GetIndexParameters().Length == 0).ToArray();
        if (properties.Length != 1) return false;
        var property = properties[0];
        var getter = property.GetMethod;
        if (getter == null || getter.IsStatic || getter.IsVirtual || !property.PropertyType.IsEnum
            || property.PropertyType.GetEnumUnderlyingType() != flagsUnderlyingType) return false;
        var body = Read(getter).Where(instruction => instruction.Code != OpCodes.Nop).ToArray();
        if (body.Length != 3 || body[0].Code != OpCodes.Ldarg_0 || body[1].Code != OpCodes.Ldfld || body[2].Code != OpCodes.Ret) return false;
        var field = data.AttributeType.GetFields(Declared).SingleOrDefault(candidate => candidate.MetadataToken == body[1].Operand);
        if (field == null || field.FieldType != property.PropertyType) return false;
        long? value = 0;
        var args = data.ConstructorArguments.Select(argument => Number(argument.Value)).ToArray();
        if (!Constructor(data.Constructor, args, field, new HashSet<int>(), ref value) || value == null) return false;
        flags = value.Value;
        return true;
    }

    private bool Constructor(ConstructorInfo ctor, long?[] args, FieldInfo field, HashSet<int> visiting, ref long? value)
    {
        if (!visiting.Add(ctor.MetadataToken)) return false;
        try
        {
            var stack = new Stack<(bool This, long? Number)>();
            foreach (var (code, operand) in Read(ctor))
            {
                if (code == OpCodes.Nop) continue;
                if (code == OpCodes.Ret) return stack.Count == 0;
                var argument = code == OpCodes.Ldarg_0 ? 0 : code == OpCodes.Ldarg_1 ? 1 : code == OpCodes.Ldarg_2 ? 2
                    : code == OpCodes.Ldarg_3 ? 3 : code == OpCodes.Ldarg || code == OpCodes.Ldarg_S ? (int)operand : -1;
                if (argument >= 0) { if (argument > args.Length) return false; stack.Push(argument == 0 ? (true, null) : (false, args[argument - 1])); continue; }
                if (code.Value >= OpCodes.Ldc_I4_M1.Value && code.Value <= OpCodes.Ldc_I4_8.Value) { stack.Push((false, code.Value - OpCodes.Ldc_I4_0.Value)); continue; }
                if (code == OpCodes.Ldc_I4 || code == OpCodes.Ldc_I4_S || code == OpCodes.Ldc_I8) { stack.Push((false, operand)); continue; }
                if (code == OpCodes.Ldnull || code == OpCodes.Ldstr) { stack.Push((false, null)); continue; }
                if (code == OpCodes.Dup) { if (!stack.TryPeek(out var top)) return false; stack.Push(top); continue; }
                if (code == OpCodes.Pop) { if (!stack.TryPop(out _)) return false; continue; }
                if (code == OpCodes.Stfld)
                {
                    if (!stack.TryPop(out var assigned) || !stack.TryPop(out var receiver) || !receiver.This) return false;
                    if (operand == field.MetadataToken) value = assigned.This ? null : assigned.Number;
                    continue;
                }
                if (code == OpCodes.Call || code == OpCodes.Callvirt)
                {
                    using var pe = new PEReader(File.OpenRead(ctor.DeclaringType!.Assembly.Location));
                    var reader = pe.GetMetadataReader();
                    var handle = MetadataTokens.EntityHandle((int)operand);
                    ConstructorInfo? target = null;
                    if (handle.Kind == HandleKind.MethodDefinition)
                        target = ctor.DeclaringType.GetConstructors(Declared).SingleOrDefault(candidate => candidate.MetadataToken == operand);
                    else if (handle.Kind == HandleKind.MemberReference)
                    {
                        var reference = reader.GetMemberReference((MemberReferenceHandle)handle);
                        if (reader.GetString(reference.Name) != ".ctor") return false;
                        var signatures = new NativeParameterMetadata.SignatureTypes(context, ctor.DeclaringType.Assembly);
                        var parent = reference.Parent.Kind switch {
                            HandleKind.TypeReference => signatures.GetTypeFromReference(reader, (TypeReferenceHandle)reference.Parent, 0),
                            HandleKind.TypeDefinition => signatures.GetTypeFromDefinition(reader, (TypeDefinitionHandle)reference.Parent, 0),
                            _ => null };
                        if (parent == null) return false;
                        var signature = reference.DecodeMethodSignature(signatures, new NativeParameterMetadata.GenericContext(ImmutableArray<Type?>.Empty, ImmutableArray<Type?>.Empty));
                        target = parent.GetConstructors(Declared).SingleOrDefault(candidate => candidate.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(signature.ParameterTypes));
                    }
                    if (target == null) return false;
                    var actual = new long?[target.GetParameters().Length];
                    for (var i = actual.Length - 1; i >= 0; i--) { if (!stack.TryPop(out var arg) || arg.This) return false; actual[i] = arg.Number; }
                    if (!stack.TryPop(out var self) || !self.This) return false;
                    if (target.DeclaringType == field.DeclaringType) { if (!Constructor(target, actual, field, visiting, ref value)) return false; }
                    else if (target.DeclaringType!.Assembly != context.CoreAssembly || target.DeclaringType.FullName is not ("System.Attribute" or "System.Object") || actual.Length != 0) return false;
                    continue;
                }
                return false;
            }
            return false;
        }
        finally { visiting.Remove(ctor.MetadataToken); }
    }

    private static long? Number(object? value)
    { try { return value == null ? null : Convert.ToInt64(value); } catch (Exception error) when (error is InvalidCastException or FormatException or OverflowException) { return null; } }

    private static IReadOnlyList<(OpCode Code, long Operand)> Read(MethodBase method)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray() ?? [];
        var result = new List<(OpCode, long)>();
        var codes = typeof(OpCodes).GetFields().Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!).ToDictionary(code => unchecked((ushort)code.Value));
        for (var offset = 0; offset < bytes.Length;)
        {
            ushort value = bytes[offset++];
            if (value == 0xfe) { if (offset == bytes.Length) throw new BadImageFormatException("Truncated JSON constructor metadata opcode."); value = (ushort)(0xfe00 | bytes[offset++]); }
            if (!codes.TryGetValue(value, out var code)) throw new BadImageFormatException("Unknown JSON constructor metadata opcode.");
            if (code.OperandType == OperandType.InlineSwitch && offset + 4 > bytes.Length) throw new BadImageFormatException("Truncated JSON constructor metadata switch.");
            var size = code.OperandType switch { OperandType.InlineNone => 0, OperandType.ShortInlineI or OperandType.ShortInlineVar or OperandType.ShortInlineBrTarget => 1, OperandType.InlineVar => 2, OperandType.InlineI8 or OperandType.InlineR => 8, OperandType.InlineSwitch => checked(4 + 4 * BitConverter.ToInt32(bytes, offset)), _ => 4 };
            if (size < 0 || offset + size > bytes.Length) throw new BadImageFormatException("Truncated JSON constructor metadata operand.");
            long operand = size switch { 0 => 0, 1 => code.OperandType == OperandType.ShortInlineI ? (sbyte)bytes[offset] : bytes[offset], 2 => BitConverter.ToUInt16(bytes, offset), 8 when code.OperandType != OperandType.InlineSwitch => BitConverter.ToInt64(bytes, offset), _ => BitConverter.ToInt32(bytes, offset) };
            result.Add((code, operand)); offset += size;
        }
        return result;
    }
}

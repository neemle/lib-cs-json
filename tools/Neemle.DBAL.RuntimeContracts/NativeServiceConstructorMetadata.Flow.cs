// Copyright (c) Neemle contributors. Licensed under the MIT License.

using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Security.Cryptography;
using System.Text;

namespace Neemle.DBAL.Tools;

internal static partial class NativeServiceConstructorMetadata
{
    private sealed record ClosureObject(string Identity, Type Type);
    private sealed class Value
    {
        internal static readonly Value Empty = new([], [], [], false);
        internal static readonly Value Unknown = new([], [], [], true);
        internal readonly HashSet<Type> Types;
        internal readonly HashSet<string> Strings;
        internal readonly HashSet<int> Integers;
        internal readonly bool Unresolved;
        internal readonly Value[]? Array;
        internal readonly HashSet<ClosureObject> Objects;
        private Value(IEnumerable<Type> types, IEnumerable<string> strings, IEnumerable<int> integers, bool unresolved, Value[]? array = null, IEnumerable<ClosureObject>? objects = null)
        { Types = types.ToHashSet(); Strings = strings.ToHashSet(StringComparer.Ordinal); Integers = integers.ToHashSet(); Unresolved = unresolved; Array = array; Objects = objects?.ToHashSet() ?? []; }
        internal static Value Of(Type? type) => type == null ? Unknown : new([type], [], [], false);
        internal static Value Of(string value) => new([], [value], [], false);
        internal static Value Of(int value) => new([], [], [value], false);
        internal static Value OfObject(ClosureObject value) => new([], [], [], false, objects: [value]);
        internal static Value OfArray(int count) => new([], [], [], false, Enumerable.Repeat(Empty, count).ToArray());
        internal Value Join(Value other)
        {
            if (ReferenceEquals(this, other) || EqualsTo(other)) return this;
            Value[]? array = null;
            if (Array != null && other.Array != null && Array.Length == other.Array.Length)
                array = Array.Select((value, index) => value.Join(other.Array[index])).ToArray();
            else if (Array != null && ReferenceEquals(other, Empty)) array = Array;
            else if (other.Array != null && ReferenceEquals(this, Empty)) array = other.Array;
            return new(Types.Concat(other.Types), Strings.Concat(other.Strings), Integers.Concat(other.Integers), Unresolved || other.Unresolved, array, Objects.Concat(other.Objects));
        }
        internal bool EqualsTo(Value other) => Unresolved == other.Unresolved && Types.SetEquals(other.Types) && Strings.SetEquals(other.Strings)
            && Integers.SetEquals(other.Integers) && Objects.SetEquals(other.Objects) && ((Array == null && other.Array == null) || (Array != null && other.Array != null
                && Array.Length == other.Array.Length && Array.Select((value, index) => value.EqualsTo(other.Array[index])).All(value => value)));
    }

    private sealed class State(string key, MethodBase method, Value[] arguments)
    {
        internal readonly string Key = key;
        internal readonly MethodBase Method = method;
        internal Value[] Arguments = arguments;
        internal Value Return = Value.Empty;
        internal readonly HashSet<State> Dependents = new();
    }

    private sealed class Frame(List<Value> stack, Value[] locals)
    {
        internal readonly List<Value> Stack = stack;
        internal readonly Value[] Locals = locals;
        internal Frame Copy() => new(new List<Value>(Stack), Locals.ToArray());
        internal Value Pop() { if (Stack.Count == 0) return Value.Unknown; var value = Stack[^1]; Stack.RemoveAt(Stack.Count - 1); return value; }
        internal bool Join(Frame other)
        {
            if (Stack.Count != other.Stack.Count) throw new BadImageFormatException("Inconsistent IL stack while inferring service constructor registrations.");
            var changed = false;
            for (var index = 0; index < Stack.Count; index++)
            { var joined = Stack[index].Join(other.Stack[index]); if (!Stack[index].EqualsTo(joined)) { Stack[index] = joined; changed = true; } }
            for (var index = 0; index < Locals.Length; index++)
            { var joined = Locals[index].Join(other.Locals[index]); if (!Locals[index].EqualsTo(joined)) { Locals[index] = joined; changed = true; } }
            return changed;
        }
    }

    private sealed record Instruction(int Offset, int Next, OpCode Code, int Integer, int[] Targets);
    private static readonly Dictionary<ushort, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
        .GroupBy(code => unchecked((ushort)code.Value)).ToDictionary(group => group.Key, group => group.First());

    private sealed partial class Analysis
    {
        private void Enqueue(State state) { if (_queued.Add(state.Key)) _pending.Enqueue(state); }
        private State Schedule(MethodBase method, Value[] arguments, State? caller)
        {
            var key = MethodKey(method) + ":arguments=" + string.Join("|", arguments.Select(Fingerprint));
            if (!_states.TryGetValue(key, out var state))
            {
                state = new State(key, method, arguments);
                _states.Add(key, state);
                Enqueue(state);
            }
            if (caller != null) state.Dependents.Add(caller);
            return state;
        }
        private static string Fingerprint(Value value)
        {
            static string Pack(IEnumerable<string> parts) => string.Concat(parts.OrderBy(part => part, StringComparer.Ordinal).Select(part => part.Length + ":" + part));
            return (value.Unresolved ? "?" : "!") + "T" + Pack(value.Types.Select(TypeIdentity)) + "S" + Pack(value.Strings)
                + "I" + Pack(value.Integers.Select(number => number.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                + "O" + Pack(value.Objects.Select(item => item.Identity))
                + (value.Array == null ? "N" : "A" + Pack(value.Array.Select((element, index) => index + ":" + Fingerprint(element))));
        }

        private static string TypeFingerprint(Value value)
        {
            static string Pack(IEnumerable<string> parts) => string.Concat(parts.OrderBy(part => part, StringComparer.Ordinal).Select(part => part.Length + ":" + part));
            return (value.Unresolved ? "?" : "!") + "T" + Pack(value.Types.Select(TypeIdentity)) + "S" + Pack(value.Strings)
                + "O" + Pack(value.Objects.Select(item => item.Identity))
                + (value.Array == null ? "N" : "A" + Pack(value.Array.Select((element, index) => index + ":" + TypeFingerprint(element))));
        }

        private void Interpret(State state)
        {
            if (!TryBody(state.Method, out var body)) return;
            var instructions = Decode(body).ToDictionary(instruction => instruction.Offset);
            if (instructions.Count == 0) return;
            var localCount = body.LocalSignature.IsNil ? 0 : _readers[state.Method.DeclaringType!.Assembly]
                .GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(_signatures[state.Method.DeclaringType.Assembly], Environment(state.Method)).Length;
            var frames = new Dictionary<(int Offset, string Values), Frame>();
            var pending = new Queue<(int Offset, string Values)>();
            Add(0, new Frame([], Enumerable.Repeat(Value.Empty, localCount).ToArray()));
            foreach (var region in body.ExceptionRegions)
            {
                Add(region.HandlerOffset, new Frame(region.Kind is ExceptionRegionKind.Catch or ExceptionRegionKind.Filter ? [Value.Unknown] : [],
                    Enumerable.Repeat(Value.Unknown, localCount).ToArray()));
                if (region.Kind == ExceptionRegionKind.Filter)
                    Add(region.FilterOffset, new Frame([Value.Unknown], Enumerable.Repeat(Value.Unknown, localCount).ToArray()));
            }
            var returned = Value.Empty;
            var points = 0;
            while (pending.TryDequeue(out var point))
            {
                if (++points % 10000 == 0) Trace($"method paths={points}, queued={pending.Count}, frames={frames.Count}, method={MethodKey(state.Method)}");
                var offset = point.Offset;
                if (!instructions.TryGetValue(offset, out var instruction)) continue;
                var frame = frames[point].Copy();
                var code = instruction.Code;
                var name = code.Name!;
                var terminal = false;
                bool? takeBranch = null;
                if (name.StartsWith("ldarg", StringComparison.Ordinal))
                {
                    var index = Index(name, instruction.Integer);
                    frame.Stack.Add(index < state.Arguments.Length ? state.Arguments[index] : Value.Unknown);
                }
                else if (name.StartsWith("starg", StringComparison.Ordinal)) _ = frame.Pop();
                else if (name.StartsWith("ldloc", StringComparison.Ordinal))
                { var index = Index(name, instruction.Integer); frame.Stack.Add(index < frame.Locals.Length ? frame.Locals[index] : Value.Unknown); }
                else if (name.StartsWith("stloc", StringComparison.Ordinal))
                { var value = frame.Pop(); var index = Index(name, instruction.Integer); if (index < frame.Locals.Length) frame.Locals[index] = value; }
                else if (code == OpCodes.Ldtoken) frame.Stack.Add(Value.Of(ResolveType(state.Method, MetadataTokens.EntityHandle(instruction.Integer))));
                else if (code == OpCodes.Ldstr) frame.Stack.Add(Value.Of(_readers[state.Method.DeclaringType!.Assembly].GetUserString(MetadataTokens.UserStringHandle(instruction.Integer & 0x00ffffff))));
                else if (code == OpCodes.Ldnull) frame.Stack.Add(Value.Empty);
                else if (name.StartsWith("ldc.i4", StringComparison.Ordinal)) frame.Stack.Add(Value.Of(code == OpCodes.Ldc_I4_M1 ? -1 : Index(name, instruction.Integer)));
                else if (code == OpCodes.Dup) { var value = frame.Pop(); frame.Stack.Add(value); frame.Stack.Add(value); }
                else if (code == OpCodes.Pop) _ = frame.Pop();
                else if (code == OpCodes.Castclass || code == OpCodes.Isinst || code == OpCodes.Box || code == OpCodes.Unbox_Any) { }
                else if (code == OpCodes.Newarr)
                { var count = frame.Pop(); frame.Stack.Add(ResolveType(state.Method, MetadataTokens.EntityHandle(instruction.Integer)) == _type
                    && count.Integers.Count == 1 && count.Integers.Single() is >= 0 and <= 256 ? Value.OfArray(count.Integers.Single()) : Value.Unknown); }
                else if (name.StartsWith("stelem", StringComparison.Ordinal))
                {
                    var value = frame.Pop(); var indices = frame.Pop(); var array = frame.Pop();
                    if (array.Array != null) foreach (var index in indices.Integers)
                        if ((uint)index < (uint)array.Array.Length) array.Array[index] = array.Array[index].Join(value);
                }
                else if (name.StartsWith("ldelem", StringComparison.Ordinal))
                {
                    var indices = frame.Pop(); var array = frame.Pop(); var value = Value.Empty;
                    if (array.Array != null) foreach (var index in indices.Integers)
                        if ((uint)index < (uint)array.Array.Length) value = value.Join(array.Array[index]);
                    frame.Stack.Add(array.Array == null ? Value.Unknown : value);
                }
                else if (code == OpCodes.Ldsfld || code == OpCodes.Ldfld)
                {
                    var field = ResolveField(state.Method, MetadataTokens.EntityHandle(instruction.Integer));
                    var receiver = code == OpCodes.Ldfld ? frame.Pop() : Value.Empty;
                    if (field?.IsStatic == true && _fields.TryGetValue(FieldKey(field), out var value)) frame.Stack.Add(value);
                    else if (field?.FieldType == _type && !field.IsStatic && receiver.Objects.Count > 0)
                    {
                        var loaded = Value.Empty;
                        foreach (var instance in receiver.Objects)
                        {
                            var key = (instance.Identity, FieldKey(field));
                            if (!_closureReaders.TryGetValue(key, out var readers)) _closureReaders.Add(key, readers = []);
                            readers.Add(state);
                            loaded = loaded.Join(_closureFields.GetValueOrDefault(key, Value.Empty));
                        }
                        frame.Stack.Add(loaded);
                    }
                    else if (field?.FieldType == _type && field.IsStatic && field.DeclaringType!.TypeInitializer is { } initializer)
                    {
                        if (!initializer.ContainsGenericParameters) Schedule(initializer, [], state);
                        frame.Stack.Add(Value.Empty);
                    }
                    else frame.Stack.Add(Value.Unknown);
                }
                else if (code == OpCodes.Stsfld || code == OpCodes.Stfld)
                {
                    var value = frame.Pop(); var receiver = code == OpCodes.Stfld ? frame.Pop() : Value.Empty;
                    var field = ResolveField(state.Method, MetadataTokens.EntityHandle(instruction.Integer));
                    if (code == OpCodes.Stfld && field?.FieldType == _type)
                        foreach (var instance in receiver.Objects)
                        {
                            var key = (instance.Identity, FieldKey(field));
                            var old = _closureFields.GetValueOrDefault(key, Value.Empty); var joined = old.Join(value);
                            if (!old.EqualsTo(joined))
                            {
                                _closureFields[key] = joined;
                                if (_closureReaders.TryGetValue(key, out var readers)) foreach (var reader in readers) Enqueue(reader);
                            }
                        }
                    if (code == OpCodes.Stsfld && field?.FieldType == _type)
                    {
                        var key = FieldKey(field); var old = _fields.GetValueOrDefault(key, Value.Empty); var joined = old.Join(value);
                        if (!old.EqualsTo(joined)) { _fields[key] = joined; foreach (var known in _states.Values) Enqueue(known); }
                    }
                }
                else if (code == OpCodes.Ldftn || code == OpCodes.Ldvirtftn)
                {
                    var target = ResolveMethod(state.Method, MetadataTokens.EntityHandle(instruction.Integer));
                    var receiver = code == OpCodes.Ldvirtftn ? frame.Pop() : frame.Stack.LastOrDefault() ?? Value.Unknown;
                    if (target != null && !target.ContainsGenericParameters && _needed.Contains(DefinitionKey(target)))
                    {
                        var arguments = Enumerable.Repeat(Value.Unknown, target.GetParameters().Length + (target.IsStatic ? 0 : 1)).ToArray();
                        if (!target.IsStatic && receiver.Objects.Any(instance => instance.Type == target.DeclaringType)) arguments[0] = receiver;
                        Schedule(target, arguments, state);
                    }
                    frame.Stack.Add(Value.Unknown);
                }
                else if (code == OpCodes.Call || code == OpCodes.Callvirt || code == OpCodes.Newobj)
                {
                    var target = ResolveMethod(state.Method, MetadataTokens.EntityHandle(instruction.Integer));
                    if (target == null) throw new InvalidOperationException("Cannot resolve service-registration call: " + MethodKey(state.Method)
                        + " IL_" + offset.ToString("x4") + " token=0x" + instruction.Integer.ToString("x8"));
                    var arguments = new Value[target.GetParameters().Length];
                    for (var index = arguments.Length - 1; index >= 0; index--) arguments[index] = frame.Pop();
                    var instance = !target.IsStatic && code != OpCodes.Newobj ? frame.Pop() : Value.Unknown;
                    Value result = Value.Unknown;
                    if (IsRequest(target))
                    {
                        if (target.IsGenericMethod && target.GetGenericArguments().Length == 1 && !target.GetGenericArguments()[0].ContainsGenericParameters)
                            _requestShapes.Add(target.GetGenericArguments()[0]);
                        else
                            foreach (var pair in target.GetParameters().Select((parameter, index) => (parameter, index)))
                                if (pair.parameter.ParameterType == _type)
                                    foreach (var request in arguments[pair.index].Types.Where(type => !type.ContainsGenericParameters)) _requestShapes.Add(request);
                    }
                    if (Sink(target) is { } implementationIndex)
                    {
                        var service = arguments[0]; var implementation = arguments[implementationIndex];
                        var provenance = state.Key + " IL_" + offset.ToString("x4");
                        var site = provenance + " -> " + target;
                        if (implementation.Types.Count == 0 && implementation.Unresolved) _unresolved.Add(site);
                        else if (implementation.Types.Count != 0) _resolvedSites.Add(site);
                        foreach (var type in implementation.Types)
                        {
                            if (service.Types.Count == 0) _registrations.Add((null, type, provenance, target.ToString()!));
                            foreach (var serviceType in service.Types) _registrations.Add((serviceType, type, provenance, target.ToString()!));
                        }
                    }
                    if ((target.DeclaringType == _type && target.Name is "op_Equality" or "op_Inequality") && arguments.Length == 2)
                    {
                        result = Equal(arguments[0], arguments[1]);
                        if (target.Name == "op_Inequality" && result.Integers.Count == 1) result = Value.Of(result.Integers.Single() == 0 ? 1 : 0);
                    }
                    else if (target.DeclaringType == _type && target.Name == "GetTypeFromHandle" && arguments.Length == 1) result = arguments[0];
                    else if (target.DeclaringType == _type && target.Name == "MakeGenericType" && arguments.Length == 1)
                    {
                        result = Value.Empty;
                        if (arguments[0].Array is { } elements && elements.All(value => value.Types.Count == 1))
                            foreach (var definition in instance.Types.Where(type => type.IsGenericTypeDefinition))
                            {
                                if (definition.GetGenericArguments().Length != elements.Length) continue;
                                result = result.Join(Value.Of(definition.MakeGenericType(elements.Select(value => value.Types.Single()).ToArray())));
                            }
                    }
                    else if (target.DeclaringType == _type && target.Name == "GetType" && target.IsStatic && arguments.Length > 0)
                    {
                        result = Value.Empty;
                        foreach (var typeName in arguments[0].Strings)
                            result = result.Join(Value.Of(Type.GetType(typeName, _context.LoadFromAssemblyName,
                                static (owner, type, ignoreCase) => owner?.GetType(type, false, ignoreCase), false)));
                    }
                    else if ((_needed.Contains(DefinitionKey(target)) || target is MethodInfo returnedType && returnedType.ReturnType == _type)
                        && !target.ContainsGenericParameters && _readers.ContainsKey(target.DeclaringType!.Assembly)
                        && !target.GetParameters().Select((parameter, index) => parameter.ParameterType == _type && IsEmpty(arguments[index])).Any(value => value))
                    {
                        var callArguments = target.IsStatic ? arguments : new[] { instance }.Concat(arguments).ToArray();
                        result = Schedule(target, callArguments, state).Return;
                    }
                    if (code == OpCodes.Newobj && target.DeclaringType!.GetCustomAttributesData().Any(attribute => attribute.AttributeType
                        == _context.CoreAssembly!.GetType("System.Runtime.CompilerServices.CompilerGeneratedAttribute")))
                    {
                        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state.Key + ":IL_" + offset.ToString("x4"))));
                        result = Value.OfObject(new ClosureObject(identity, target.DeclaringType));
                    }
                    if (code == OpCodes.Newobj || target is MethodInfo info && info.ReturnType.FullName != "System.Void") frame.Stack.Add(result);
                }
                else if (code == OpCodes.Ceq) { var right = frame.Pop(); frame.Stack.Add(Equal(frame.Pop(), right)); }
                else if (code == OpCodes.Brtrue || code == OpCodes.Brtrue_S || code == OpCodes.Brfalse || code == OpCodes.Brfalse_S)
                {
                    var condition = frame.Pop();
                    if (!condition.Unresolved && condition.Integers.Count == 1)
                        takeBranch = (condition.Integers.Single() != 0) == (code == OpCodes.Brtrue || code == OpCodes.Brtrue_S);
                }
                else if (code == OpCodes.Beq || code == OpCodes.Beq_S || code == OpCodes.Bne_Un || code == OpCodes.Bne_Un_S)
                {
                    var right = frame.Pop(); var equal = Equal(frame.Pop(), right);
                    if (equal.Integers.Count == 1) takeBranch = (equal.Integers.Single() != 0) == (code == OpCodes.Beq || code == OpCodes.Beq_S);
                }
                else if (code == OpCodes.Ret)
                {
                    if (state.Method is MethodInfo info && info.ReturnType.FullName != "System.Void") returned = returned.Join(frame.Pop());
                    terminal = true;
                }
                else
                {
                    for (var index = 0; index < PopCount(code.StackBehaviourPop); index++) _ = frame.Pop();
                    for (var index = 0; index < PushCount(code.StackBehaviourPush); index++) frame.Stack.Add(Value.Unknown);
                }
                if (code == OpCodes.Leave || code == OpCodes.Leave_S) frame.Stack.Clear();
                if (code.FlowControl is FlowControl.Throw or FlowControl.Return || code == OpCodes.Endfinally || code == OpCodes.Endfilter) terminal = true;
                if (terminal) continue;
                if (takeBranch != false) foreach (var target in instruction.Targets) Add(target, frame);
                if (code.FlowControl != FlowControl.Branch && takeBranch != true) Add(instruction.Next, frame);
            }
            var joinedReturn = state.Return.Join(returned);
            if (!state.Return.EqualsTo(joinedReturn))
            { state.Return = joinedReturn; foreach (var dependent in state.Dependents) Enqueue(dependent); }

            void Add(int offset, Frame frame)
            {
                if (!instructions.ContainsKey(offset)) return;
                var arrayIndexSensitive = frame.Stack.Concat(frame.Locals).Any(value => value.Array != null)
                    || instructions[offset].Code == OpCodes.Newarr && ResolveType(state.Method, MetadataTokens.EntityHandle(instructions[offset].Integer)) == _type;
                Func<Value, string> fingerprint = arrayIndexSensitive ? Fingerprint : TypeFingerprint;
                var key = (offset, string.Join("|", frame.Stack.Select(fingerprint)) + ":locals=" + string.Join("|", frame.Locals.Select(fingerprint)));
                if (frames.TryAdd(key, frame.Copy())) pending.Enqueue(key);
                else if (frames[key].Join(frame)) pending.Enqueue(key);
            }
        }

        private static bool IsEmpty(Value value) => !value.Unresolved && value.Types.Count == 0 && value.Strings.Count == 0 && value.Integers.Count == 0 && value.Array == null && value.Objects.Count == 0;
        private static Value Equal(Value left, Value right)
        {
            if (!left.Unresolved && !right.Unresolved)
            {
                if (left.Types.Count == 1 && right.Types.Count == 1) return Value.Of(left.Types.Single() == right.Types.Single() ? 1 : 0);
                if (left.Integers.Count == 1 && right.Integers.Count == 1) return Value.Of(left.Integers.Single() == right.Integers.Single() ? 1 : 0);
            }
            return Value.Unknown;
        }

        private static string FieldKey(FieldInfo field) => field.DeclaringType!.AssemblyQualifiedName + ":" + field.MetadataToken;
        private FieldInfo? ResolveField(MethodBase caller, EntityHandle handle)
        {
            var reader = _readers[caller.DeclaringType!.Assembly];
            if (handle.Kind == HandleKind.FieldDefinition)
            {
                var definition = reader.GetFieldDefinition((FieldDefinitionHandle)handle);
                var owner = ResolveType(caller, definition.GetDeclaringType());
                if (owner?.IsGenericTypeDefinition == true && caller.DeclaringType.IsGenericType && caller.DeclaringType.GetGenericTypeDefinition() == owner) owner = caller.DeclaringType;
                return owner?.GetFields(Declared).Single(field => field.MetadataToken == MetadataTokens.GetToken(handle));
            }
            if (handle.Kind != HandleKind.MemberReference) return null;
            var reference = reader.GetMemberReference((MemberReferenceHandle)handle);
            return ResolveType(caller, reference.Parent)?.GetField(reader.GetString(reference.Name), Declared);
        }
    }

    private static int Index(string name, int operand)
    { var suffix = name[(name.LastIndexOf('.') + 1)..]; return int.TryParse(suffix, out var index) ? index : operand; }
    private static int PopCount(StackBehaviour behavior) => behavior is StackBehaviour.Pop0 or StackBehaviour.Varpop ? 0 : behavior.ToString().Split('_').Length;
    private static int PushCount(StackBehaviour behavior) => behavior is StackBehaviour.Push0 or StackBehaviour.Varpush ? 0 : behavior.ToString().Split('_').Length;

    private static IEnumerable<Instruction> Decode(MethodBodyBlock body)
    {
        var bytes = body.GetILBytes() ?? throw new BadImageFormatException("Missing service-registration IL body.");
        var offset = 0;
        while (offset < bytes.Length)
        {
            var start = offset; var raw = (ushort)bytes[offset++];
            if (raw == 0xfe) raw = (ushort)(0xfe00 | bytes[offset++]);
            if (!Codes.TryGetValue(raw, out var code)) throw new BadImageFormatException("Unknown service-registration IL opcode.");
            var size = code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => checked(4 + 4 * BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4))),
                _ => 4
            };
            if (size < 0 || offset + size > bytes.Length) throw new BadImageFormatException("Truncated service-registration IL operand.");
            var integer = size switch { 1 => code.OperandType is OperandType.ShortInlineBrTarget or OperandType.ShortInlineI ? (sbyte)bytes[offset] : bytes[offset],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2)), >= 4 => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)), _ => 0 };
            var next = offset + size;
            var targets = code.OperandType switch
            {
                OperandType.ShortInlineBrTarget or OperandType.InlineBrTarget => new[] { checked(next + integer) },
                OperandType.InlineSwitch => Enumerable.Range(0, integer).Select(index => checked(next + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4 + 4 * index, 4)))).ToArray(),
                _ => []
            };
            offset = next;
            yield return new Instruction(start, next, code, integer, targets);
        }
    }
}

// Copyright (c) Neemle contributors. Licensed under the MIT License.

using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Neemle.DBAL.Tools;

/// <summary>Metadata-only implementation-type flow into canonical DI descriptors.</summary>
internal static partial class NativeServiceConstructorMetadata
{
    internal sealed record Registration(Type? ServiceType, Type ImplementationType, string Caller, string Sink, bool ClosedFromOpenGeneric);
    internal sealed record Summary(IReadOnlyList<Type> ImplementationTypes, IReadOnlyList<Registration> Registrations,
        IReadOnlyList<string> UnresolvedRegistrations);

    internal static Summary Infer(MetadataLoadContext context, IEnumerable<Assembly> inputs, IEnumerable<Type> inferred)
    {
        using var analysis = new Analysis(context, inputs, inferred);
        return analysis.Run();
    }

    private static void Trace(string message)
    {
        if (global::System.Environment.GetEnvironmentVariable("NEEMLE_TRACE_SERVICE_METADATA") == "1")
            Console.Error.WriteLine("Service metadata: " + message);
    }

    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private static string DefinitionKey(MethodBase method) => method.Module.Assembly.FullName + ":" + method.MetadataToken;
    private static string MethodKey(MethodBase method) => method.DeclaringType?.AssemblyQualifiedName + ":" + method.MetadataToken + ":"
        + string.Join(";", method.IsGenericMethod ? method.GetGenericArguments().Select(type => type.AssemblyQualifiedName ?? type.ToString()) : []);
    private static string TypeIdentity(Type? type)
    {
        if (type == null) return "?";
        if (type.IsGenericParameter) return (type.DeclaringType?.AssemblyQualifiedName ?? "") + ":"
            + type.DeclaringMethod?.MetadataToken + ":" + type.GenericParameterPosition;
        return type.AssemblyQualifiedName ?? type.ToString();
    }
    private static NativeParameterMetadata.GenericContext Environment(MethodBase method) => new(
        method.DeclaringType!.GetGenericArguments().Cast<Type?>().ToImmutableArray(),
        method.IsGenericMethod ? method.GetGenericArguments().Cast<Type?>().ToImmutableArray() : []);
    private static IEnumerable<MethodBase> Methods(Type type) => type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared));

    private sealed partial class Analysis : IDisposable
    {
        private readonly MetadataLoadContext _context;
        private readonly Dictionary<Assembly, PEReader> _images = new();
        private readonly Dictionary<Assembly, MetadataReader> _readers = new();
        private readonly Dictionary<Assembly, NativeParameterMetadata.SignatureTypes> _signatures = new();
        private readonly Dictionary<string, (Assembly Assembly, MethodDefinitionHandle Handle)> _allMethods = new(StringComparer.Ordinal);
        private readonly Dictionary<string, MethodBase> _definitions = new(StringComparer.Ordinal);
        private readonly Dictionary<Type, MethodBase[]> _members = new();
        private readonly Dictionary<(Assembly Assembly, int Token, string Environment), MethodBase?> _resolvedMethods = new();
        private readonly HashSet<string> _needed = new(StringComparer.Ordinal);
        private readonly HashSet<string> _roleMethods = new(StringComparer.Ordinal);
        private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
        private readonly Queue<State> _pending = new();
        private readonly HashSet<string> _queued = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Object, string Field), Value> _closureFields = new();
        private readonly Dictionary<(string Object, string Field), HashSet<State>> _closureReaders = new();
        private readonly Dictionary<string, Value> _fields = new(StringComparer.Ordinal);
        private readonly List<(Type? Service, Type Implementation, string Caller, string Sink)> _registrations = new();
        private readonly HashSet<string> _unresolved = new(StringComparer.Ordinal);
        private readonly HashSet<string> _resolvedSites = new(StringComparer.Ordinal);
        private readonly HashSet<Type> _requestShapes;
        private readonly Dictionary<Type, HashSet<Type>> _requestAncestors = new();
        private readonly Type _type;
        private readonly Type? _descriptor;
        private readonly Type? _coreOptions;
        private readonly Type? _serviceProvider;
        private readonly Type? _serviceExtensions;
        private readonly Type? _keyedExtensions;

        internal Analysis(MetadataLoadContext context, IEnumerable<Assembly> inputs, IEnumerable<Type> inferred)
        {
            _context = context;
            _type = context.CoreAssembly!.GetType("System.Type", true)!;
            var assemblies = inputs.Distinct().ToArray();
            var di = assemblies.SingleOrDefault(assembly => assembly.GetName().Name == "Microsoft.Extensions.DependencyInjection.Abstractions"
                && Convert.ToHexString(assembly.GetName().GetPublicKeyToken() ?? []).Equals("ADB9793829DDAE60", StringComparison.Ordinal));
            _descriptor = di?.GetType("Microsoft.Extensions.DependencyInjection.ServiceDescriptor");
            _serviceExtensions = di?.GetType("Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions");
            _keyedExtensions = di?.GetType("Microsoft.Extensions.DependencyInjection.ServiceProviderKeyedServiceExtensions");
            _serviceProvider = _serviceExtensions?.GetMethods().First(method => method.Name == "GetRequiredService" && method.IsGenericMethodDefinition)
                .GetParameters()[0].ParameterType;
            _coreOptions = assemblies.SingleOrDefault(assembly => assembly.GetName().Name == "Microsoft.EntityFrameworkCore"
                && Convert.ToHexString(assembly.GetName().GetPublicKeyToken() ?? []).Equals("ADB9793829DDAE60", StringComparison.Ordinal))
                ?.GetType("Microsoft.EntityFrameworkCore.Infrastructure.CoreOptionsExtension");
            _requestShapes = inferred.Where(type => !type.ContainsGenericParameters && !type.HasElementType).ToHashSet();
            foreach (var assembly in assemblies)
            {
                var image = new PEReader(File.OpenRead(assembly.Location));
                _images.Add(assembly, image);
                _readers.Add(assembly, image.GetMetadataReader());
                _signatures.Add(assembly, new NativeParameterMetadata.SignatureTypes(context, assembly));
            }
        }

        public void Dispose() { foreach (var image in _images.Values) image.Dispose(); }

        internal Summary Run()
        {
            if (_descriptor == null) return new Summary([], [], []);
            Trace("start selected-input registration graph");
            BuildCallGraph();
            Trace($"graph definitions={_definitions.Count}, needed={_needed.Count}");
            foreach (var pair in _definitions)
                if (_roleMethods.Contains(pair.Key) && !pair.Value.ContainsGenericParameters && !HasTypeArguments(pair.Value) && !HasCapturedTypeReceiver(pair.Value))
                    Schedule(pair.Value, Enumerable.Repeat(Value.Unknown, pair.Value.GetParameters().Length + (pair.Value.IsStatic ? 0 : 1)).ToArray(), null);
            foreach (var shape in _requestShapes.ToArray())
                foreach (var method in Members(shape))
                    if (!method.ContainsGenericParameters && _roleMethods.Contains(DefinitionKey(method)) && !HasTypeArguments(method) && !HasCapturedTypeReceiver(method))
                        Schedule(method, Enumerable.Repeat(Value.Unknown, method.GetParameters().Length + (method.IsStatic ? 0 : 1)).ToArray(), null);
            var steps = 0;
            while (_pending.TryDequeue(out var state))
            {
                if (++steps % 1000 == 0) Trace($"flow steps={steps}, states={_states.Count}, pending={_pending.Count}, method={MethodKey(state.Method)}");
                _queued.Remove(state.Key);
                Interpret(state);
            }
            Trace($"flow completed steps={steps}, states={_states.Count}, rawRegistrations={_registrations.Count}");
            var registrations = new Dictionary<string, Registration>(StringComparer.Ordinal);
            var uniqueRegistrations = _registrations.Distinct().ToArray();
            bool domainChanged;
            do
            {
                var previousCount = registrations.Count + _requestShapes.Count + _requestAncestors.Values.Sum(values => values.Count);
                foreach (var item in uniqueRegistrations)
                {
                    if (!item.Implementation.ContainsGenericParameters) Add(item.Service, item.Implementation, false, item);
                    else if (item.Service?.IsGenericTypeDefinition == true && item.Implementation.IsGenericTypeDefinition)
                    {
                        foreach (var request in _requestShapes.Where(type => type.IsGenericType && type.GetGenericTypeDefinition() == item.Service).ToArray())
                        {
                            if (item.Implementation.GetGenericArguments().Length != request.GetGenericArguments().Length
                                || !SatisfiesConstraints(item.Implementation, request.GetGenericArguments())) continue;
                            var closed = item.Implementation.MakeGenericType(request.GetGenericArguments());
                            Add(request, closed, true, item);
                        }
                    }
                }
                domainChanged = registrations.Count + _requestShapes.Count + _requestAncestors.Values.Sum(values => values.Count) != previousCount;
            } while (domainChanged);
            return new Summary(registrations.Values.Select(value => value.ImplementationType).Distinct()
                .OrderBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal).ToArray(),
                registrations.Values.OrderBy(value => value.ImplementationType.AssemblyQualifiedName, StringComparer.Ordinal)
                    .ThenBy(value => value.Caller, StringComparer.Ordinal).ThenBy(value => value.Sink, StringComparer.Ordinal).ToArray(),
                _unresolved.Except(_resolvedSites).OrderBy(value => value, StringComparer.Ordinal).ToArray());

            void Add(Type? service, Type implementation, bool closed, (Type? Service, Type Implementation, string Caller, string Sink) item)
            {
                if (implementation.ContainsGenericParameters || implementation.HasElementType || implementation.IsInterface || implementation.IsAbstract) return;
                var value = new Registration(service, implementation, item.Caller, item.Sink, closed);
                registrations.TryAdd(implementation.AssemblyQualifiedName + ":" + service?.AssemblyQualifiedName + ":" + item.Caller + ":" + item.Sink, value);
                foreach (var constructor in implementation.GetConstructors())
                    foreach (var parameter in constructor.GetParameters())
                    {
                        var template = implementation.IsGenericType
                            ? implementation.GetGenericTypeDefinition().GetConstructors().Single(candidate => candidate.MetadataToken == constructor.MetadataToken)
                                .GetParameters()[parameter.Position].ParameterType
                            : parameter.ParameterType;
                        Dependency(parameter.ParameterType, template);
                        if (parameter.ParameterType.IsGenericType && parameter.ParameterType.GetGenericTypeDefinition()
                            == _context.CoreAssembly!.GetType("System.Collections.Generic.IEnumerable`1"))
                            Dependency(parameter.ParameterType.GenericTypeArguments[0], template.GenericTypeArguments[0]);
                    }

                void Dependency(Type request, Type template)
                {
                    if (request.ContainsGenericParameters) return;
                    var ancestors = service == null ? new HashSet<Type>() : new HashSet<Type>(_requestAncestors.GetValueOrDefault(service) ?? []);
                    if (service != null) ancestors.Add(service);
                    if (template.ContainsGenericParameters)
                        foreach (var ancestor in ancestors)
                            if (Expands(request, ancestor)) throw new InvalidOperationException("Expanding generic DI constructor dependency: "
                                + ancestor + " -> " + request + " via " + implementation + ". This registration graph has no finite generated constructor domain.");
                    _requestShapes.Add(request);
                    if (!_requestAncestors.TryGetValue(request, out var existing)) _requestAncestors.Add(request, existing = []);
                    existing.UnionWith(ancestors);
                }
            }
        }

        private static bool Expands(Type request, Type ancestor)
        {
            if (!request.IsGenericType || !ancestor.IsGenericType || request.GetGenericTypeDefinition() != ancestor.GetGenericTypeDefinition()) return false;
            var before = ancestor.GenericTypeArguments;
            var after = request.GenericTypeArguments;
            return before.All(old => after.Any(current => Contains(current, old)))
                && after.Any(current => before.Any(old => current != old && Contains(current, old)));

            static bool Contains(Type current, Type old)
                => current == old || current.HasElementType && Contains(current.GetElementType()!, old)
                    || current.IsGenericType && current.GenericTypeArguments.Any(argument => Contains(argument, old));
        }

        private bool HasCapturedTypeReceiver(MethodBase method) => !method.IsStatic
            && method.DeclaringType!.GetCustomAttributesData().Any(attribute => attribute.AttributeType
                == _context.CoreAssembly!.GetType("System.Runtime.CompilerServices.CompilerGeneratedAttribute"))
            && method.DeclaringType.GetFields(Declared).Any(field => !field.IsStatic && field.FieldType == _type);

        private bool HasTypeArguments(MethodBase method) => method.GetParameters().Any(parameter => parameter.ParameterType == _type || parameter.ParameterType == _type.MakeArrayType());

        private static bool SatisfiesConstraints(Type definition, Type[] arguments)
        {
            var parameters = definition.GetGenericArguments();
            for (var index = 0; index < parameters.Length; index++)
            {
                var flags = parameters[index].GenericParameterAttributes;
                var argument = arguments[index];
                if ((flags & GenericParameterAttributes.ReferenceTypeConstraint) != 0 && argument.IsValueType) return false;
                if ((flags & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0 && (!argument.IsValueType
                    || argument.IsGenericType && argument.GetGenericTypeDefinition().FullName == "System.Nullable`1")) return false;
                if ((flags & GenericParameterAttributes.DefaultConstructorConstraint) != 0 && !argument.IsValueType
                    && (argument.IsAbstract || argument.GetConstructor(Type.EmptyTypes) == null)) return false;
                foreach (var constraint in parameters[index].GetGenericParameterConstraints())
                    if (!Substitute(constraint).IsAssignableFrom(argument)) return false;
            }
            return true;

            Type Substitute(Type type)
            {
                if (type.IsGenericParameter && type.DeclaringMethod == null) return arguments[type.GenericParameterPosition];
                if (type.IsArray) return type.IsSZArray ? Substitute(type.GetElementType()!).MakeArrayType()
                    : Substitute(type.GetElementType()!).MakeArrayType(type.GetArrayRank());
                return type.IsGenericType ? type.GetGenericTypeDefinition().MakeGenericType(type.GetGenericArguments().Select(Substitute).ToArray()) : type;
            }
        }

        private void BuildCallGraph()
        {
            var roleAssemblies = new HashSet<Assembly> { _descriptor!.Assembly };
            if (_coreOptions != null) roleAssemblies.Add(_coreOptions.Assembly);
            bool changed;
            do
            {
                changed = false;
                foreach (var assembly in _readers.Keys)
                    if (!roleAssemblies.Contains(assembly) && assembly.GetReferencedAssemblies().Any(reference =>
                        roleAssemblies.Any(role => role.GetName().Name == reference.Name))) changed |= roleAssemblies.Add(assembly);
            } while (changed);
            var operands = new Dictionary<string, IReadOnlyList<(OpCode Code, EntityHandle Handle)>>(StringComparer.Ordinal);
            var callers = new Dictionary<string, List<(string Caller, EntityHandle Handle, string Owner)>>(StringComparer.Ordinal);
            foreach (var pair in _readers.Where(pair => roleAssemblies.Contains(pair.Key)))
                foreach (var handle in pair.Value.MethodDefinitions)
                {
                    var key = pair.Key.FullName + ":" + MetadataTokens.GetToken(handle);
                    _allMethods.Add(key, (pair.Key, handle));
                    var definition = pair.Value.GetMethodDefinition(handle);
                    if (definition.RelativeVirtualAddress == 0) continue;
                    var read = MetadataMethodOperands.Read(_images[pair.Key].GetMethodBody(definition.RelativeVirtualAddress));
                    operands.Add(key, read);
                    foreach (var operand in read.Where(operand => operand.Code.OperandType == OperandType.InlineMethod))
                    {
                        var (name, owner) = OperandIdentity(pair.Key, operand.Handle);
                        if (!callers.TryGetValue(name, out var sites)) callers.Add(name, sites = []);
                        sites.Add((key, operand.Handle, owner));
                    }
                }
            var pending = new Queue<(MethodBase Method, bool Role)>();
            foreach (var name in new[] { ".ctor", "Describe", "DescribeKeyed", "WithReplacedService", "GetService", "GetRequiredService", "GetServices", "GetKeyedService", "GetRequiredKeyedService", "GetKeyedServices" })
                if (callers.TryGetValue(name, out var sites))
                    foreach (var site in sites.Where(site => site.Owner is "ServiceDescriptor" or "CoreOptionsExtension" or "IServiceProvider" or "ServiceProviderServiceExtensions" or "ServiceProviderKeyedServiceExtensions"))
                    {
                        var caller = Method(site.Caller);
                        if (ResolveMethod(caller, site.Handle) is { } target && (Sink(target) != null || IsRequest(target))) Add(caller, true);
                    }
            while (pending.TryDequeue(out var work))
            {
                var needed = work.Method;
                if (work.Role && (needed.ContainsGenericParameters || HasTypeArguments(needed)) && callers.TryGetValue(needed.Name, out var sites))
                    foreach (var site in sites.Where(site => site.Owner == needed.DeclaringType!.Name))
                    {
                        var caller = Method(site.Caller);
                        if (ResolveMethod(caller, site.Handle) is { } target && DefinitionKey(target) == DefinitionKey(needed)) Add(caller, true);
                    }
                if (operands.TryGetValue(DefinitionKey(needed), out var calls))
                    foreach (var call in calls.Where(call => call.Code.OperandType == OperandType.InlineMethod))
                        if (ResolveMethod(needed, call.Handle) is MethodInfo helper && helper.ReturnType == _type
                            && _allMethods.ContainsKey(DefinitionKey(helper))) Add(helper, false);
            }

            void Add(MethodBase method, bool role)
            {
                _definitions.TryAdd(DefinitionKey(method), method);
                var newMethod = _needed.Add(DefinitionKey(method));
                var newRole = role && _roleMethods.Add(DefinitionKey(method));
                if (newMethod || newRole) pending.Enqueue((method, role));
            }
        }

        private MethodBase Method(string key)
        {
            if (_definitions.TryGetValue(key, out var method)) return method;
            var (assembly, handle) = _allMethods[key];
            var definition = _readers[assembly].GetMethodDefinition(handle);
            var owner = _signatures[assembly].GetTypeFromDefinition(_readers[assembly], definition.GetDeclaringType(), 0)!;
            method = Members(owner).Single(candidate => candidate.MetadataToken == MetadataTokens.GetToken(handle));
            _definitions.Add(key, method);
            return method;
        }

        private (string Name, string Owner) OperandIdentity(Assembly assembly, EntityHandle handle)
        {
            var reader = _readers[assembly];
            if (handle.Kind == HandleKind.MethodSpecification) handle = reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
            if (handle.Kind == HandleKind.MethodDefinition)
            {
                var definition = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                return (reader.GetString(definition.Name), reader.GetString(reader.GetTypeDefinition(definition.GetDeclaringType()).Name));
            }
            if (handle.Kind != HandleKind.MemberReference) return ("", "");
            var reference = reader.GetMemberReference((MemberReferenceHandle)handle);
            return (reader.GetString(reference.Name), OwnerName(reference.Parent));

            string OwnerName(EntityHandle owner)
            {
                if (owner.Kind == HandleKind.TypeReference) return reader.GetString(reader.GetTypeReference((TypeReferenceHandle)owner).Name);
                if (owner.Kind == HandleKind.TypeDefinition) return reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)owner).Name);
                if (owner.Kind != HandleKind.TypeSpecification) return "";
                var signature = reader.GetBlobReader(reader.GetTypeSpecification((TypeSpecificationHandle)owner).Signature);
                var code = signature.ReadSignatureTypeCode();
                if (code == SignatureTypeCode.GenericTypeInstance) code = signature.ReadSignatureTypeCode();
                return code is SignatureTypeCode.TypeHandle ? OwnerName(signature.ReadTypeHandle()) : "";
            }
        }

        private bool IsRequest(MethodBase method)
        {
            if (method.DeclaringType == _serviceProvider && method.Name == "GetService" && method.GetParameters().Length == 1
                && method.GetParameters()[0].ParameterType == _type) return true;
            return (method.DeclaringType == _serviceExtensions || method.DeclaringType == _keyedExtensions)
                && method.Name is "GetService" or "GetRequiredService" or "GetServices" or "GetKeyedService" or "GetRequiredKeyedService" or "GetKeyedServices"
                && method.GetParameters().Length > 0 && method.GetParameters()[0].ParameterType == _serviceProvider;
        }

        private int? Sink(MethodBase method)
        {
            if (method.DeclaringType == _coreOptions && method.Name == "WithReplacedService" && method.GetParameters().Length == 3
                && method.GetParameters().All(parameter => parameter.ParameterType == _type)) return 1;
            if (method.DeclaringType != _descriptor || (method.Name != ".ctor" && method.Name != "Describe" && method.Name != "DescribeKeyed")) return null;
            var parameters = method.GetParameters();
            for (var index = 1; index < parameters.Length; index++)
                if (parameters[index].Name == "implementationType" && parameters[index].ParameterType == _type) return index;
            return null;
        }

        private bool TryBody(MethodBase method, out MethodBodyBlock body)
        {
            body = null!;
            if (method.DeclaringType == null || method.DeclaringType.IsArray || !_readers.TryGetValue(method.DeclaringType.Assembly, out var reader)) return false;
            var definition = reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(method.MetadataToken & 0x00ffffff));
            if (definition.RelativeVirtualAddress == 0) return false;
            body = _images[method.DeclaringType.Assembly].GetMethodBody(definition.RelativeVirtualAddress);
            return true;
        }

        private Type? ResolveType(MethodBase caller, EntityHandle handle)
        {
            var assembly = caller.DeclaringType!.Assembly;
            var reader = _readers[assembly];
            var signature = _signatures[assembly];
            return handle.Kind switch
            {
                HandleKind.TypeDefinition => signature.GetTypeFromDefinition(reader, (TypeDefinitionHandle)handle, 0),
                HandleKind.TypeReference => signature.GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0),
                HandleKind.TypeSpecification => signature.GetTypeFromSpecification(reader, Environment(caller), (TypeSpecificationHandle)handle, 0),
                _ => null
            };
        }

        private MethodBase[] Members(Type type)
        {
            if (!_members.TryGetValue(type, out var methods)) _members.Add(type, methods = Methods(type).ToArray());
            return methods;
        }

        private MethodBase? ResolveMethod(MethodBase caller, EntityHandle handle)
        {
            var environment = Environment(caller);
            var key = (caller.DeclaringType!.Assembly, MetadataTokens.GetToken(handle), string.Join(";", environment.TypeArguments.Select(type => "T:" + TypeIdentity(type))
                .Concat(environment.MethodArguments.Select(type => "M:" + TypeIdentity(type)))));
            if (_resolvedMethods.TryGetValue(key, out var cached)) return cached;
            var result = ResolveMethodCore(caller, handle);
            _resolvedMethods.Add(key, result);
            return result;
        }

        private MethodBase? ResolveMethodCore(MethodBase caller, EntityHandle handle)
        {
            var assembly = caller.DeclaringType!.Assembly;
            var reader = _readers[assembly];
            var signature = _signatures[assembly];
            var environment = Environment(caller);
            ImmutableArray<Type?> arguments = [];
            if (handle.Kind == HandleKind.MethodSpecification)
            {
                var specification = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                arguments = specification.DecodeSignature(signature, environment);
                if (arguments.Any(type => type == null)) return null;
                handle = specification.Method;
            }
            if (handle.Kind == HandleKind.MethodDefinition)
            {
                var definition = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                var owner = signature.GetTypeFromDefinition(reader, definition.GetDeclaringType(), 0)!;
                if (owner.IsGenericTypeDefinition && caller.DeclaringType!.IsGenericType && caller.DeclaringType.GetGenericTypeDefinition() == owner) owner = caller.DeclaringType;
                var candidate = Members(owner).Single(method => method.MetadataToken == MetadataTokens.GetToken(handle));
                return Close(candidate, arguments);
            }
            if (handle.Kind != HandleKind.MemberReference) return null;
            var reference = reader.GetMemberReference((MemberReferenceHandle)handle);
            if (reference.GetKind() != MemberReferenceKind.Method) return null;
            var referencedOwner = ResolveType(caller, reference.Parent);
            if (referencedOwner == null) return null;
            var name = reader.GetString(reference.Name);
            foreach (var candidate in Members(referencedOwner).Where(method => method.Name == name))
            {
                var closed = Close(candidate, arguments);
                if (closed == null) continue;
                var expected = reference.DecodeMethodSignature(signature, new NativeParameterMetadata.GenericContext(
                    referencedOwner.GetGenericArguments().Cast<Type?>().ToImmutableArray(),
                    arguments.Length == 0 && candidate.IsGenericMethod ? candidate.GetGenericArguments().Cast<Type?>().ToImmutableArray() : arguments));
                if (closed.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(expected.ParameterTypes)
                    && (closed is not MethodInfo info || info.ReturnType == expected.ReturnType)) return closed;
            }
            return null;
        }

        private static MethodBase? Close(MethodBase method, ImmutableArray<Type?> arguments)
            => arguments.Length == 0 ? method : method is MethodInfo { IsGenericMethodDefinition: true } generic
                && generic.GetGenericArguments().Length == arguments.Length ? generic.MakeGenericMethod(arguments.Select(type => type!).ToArray()) : null;
    }
}

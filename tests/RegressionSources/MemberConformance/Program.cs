// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

internal static partial class Program
{
    private static readonly List<string> Trace = [];
    private static int Main(string[] args)
    {
#if JSON_ROLE_SMOKE
        if (TryRunConstructorRoleMode(args, out var roleExit)) return roleExit;
        RunConstructorRoleChecks();
#endif
#if JSON_RAW_ROLE_SMOKE
        RunRawRoleChecks();
#endif
        if (args is ["--custom-reflector"])
        {
            if (!RuntimeFeature.IsDynamicCodeSupported) return 2;
            Trace.Clear();
            var reflector = new VirtualProperty();
            var result = Unique<ProbeAttribute>(null, reflector, true);
            Console.WriteLine(result?.Id + ":" + reflector.Calls + ":" + string.Join(",", Trace));
            return 0;
        }
        if (args is ["--unknown-native-owner"])
        {
            if (RuntimeFeature.IsDynamicCodeSupported) return 2;
            try { _ = Unique<ProbeAttribute>(null, typeof(Unrelated), false); return 3; }
            catch (NotSupportedException error) { Console.WriteLine(error.Message); return 0; }
        }
        Check("all-json-member-attributes", () => string.Join("|", new object?[] {
            SingleControl<JsonIncludeAttribute>(typeof(AllMemberAttributes).GetProperty(nameof(AllMemberAttributes.Included))!, false) != null,
            SingleControl<JsonIgnoreAttribute>(typeof(AllMemberAttributes).GetProperty(nameof(AllMemberAttributes.Ignored))!, false)?.Condition,
            SingleControl<JsonExtensionDataAttribute>(typeof(AllMemberAttributes).GetProperty(nameof(AllMemberAttributes.Extra))!, false) != null,
            SingleControl<JsonPropertyOrderAttribute>(typeof(AllMemberAttributes).GetProperty(nameof(AllMemberAttributes.Name))!, false)?.Order,
            SingleControl<JsonNumberHandlingAttribute>(typeof(AllMemberAttributes).GetProperty(nameof(AllMemberAttributes.Number))!, false)?.Handling,
            SingleControl<JsonObjectCreationHandlingAttribute>(typeof(AllMemberAttributes).GetProperty(nameof(AllMemberAttributes.Items))!, false)?.Handling,
            SingleControl<JsonPropertyNameAttribute>(typeof(AllMemberAttributes).GetProperty(nameof(AllMemberAttributes.Name))!, false)?.Name,
            SingleControl<JsonRequiredAttribute>(typeof(AllMemberAttributes).GetProperty(nameof(AllMemberAttributes.Name))!, false) != null,
            HasJsonConstructor(null, typeof(AllMemberAttributes).GetConstructor(Type.EmptyTypes)!),
            SingleControl<JsonPropertyNameAttribute>(typeof(ClosedProbe<Unrelated>).GetProperty(nameof(ClosedProbe<Unrelated>.Name))!, false)?.Name
        }));
        Check("raw-type", () => HasName(null, typeof(RequiredProbe), "System.Runtime.CompilerServices.RequiredMemberAttribute", false));
        Check("raw-required-property", () => HasName(null, typeof(RequiredProbe).GetProperty(nameof(RequiredProbe.Value))!, "System.Runtime.CompilerServices.RequiredMemberAttribute", true));
        Check("raw-property-inherit", () => Unique<ProbeAttribute>(null, typeof(DerivedProbe).GetProperty(nameof(DerivedProbe.Value))!, true)?.Id);
        Check("static-property-inherit", () => SingleControl<JsonPropertyNameAttribute>( typeof(DerivedProbe).GetProperty(nameof(DerivedProbe.Value))!, true)?.Name);
        Check("typed-type-inherit", () => Unique<ProbeAttribute>(null, typeof(DerivedProbe), true)?.Id);
        Check("runtime-filter-before-construct", () => RuntimeFilteredControl( typeof(ThrowConstructorProbe), typeof(UnusedAttribute), true).Length);
        Check("runtime-filter-matching", () => string.Join(",", RuntimeFilteredControl( typeof(RequiredProbe), typeof(ProbeAttribute), false).Cast<ProbeAttribute>().Select(attribute => attribute.Id)));
        Check("duplicate-unique", () => Unique<ProbeAttribute>(null, typeof(DuplicateProbe), false)?.Id);
        Check("duplicate-static", () => SingleControl<ProbeAttribute>( typeof(DuplicateProbe), false)?.Id);
        Check("constructor-throws", () => HasName(null, typeof(ThrowConstructorProbe), "missing", false));
        Check("setter-throws", () => HasName(null, typeof(ThrowSetterProbe), "missing", false));
        Check("selected-constructor-raw", () => HasName(null, typeof(RequiredProbe).GetConstructor(Type.EmptyTypes)!, "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute", false));
        Check("unused-private-constructor-marker", () => HasJsonConstructor(null, typeof(RequiredProbe).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, [typeof(int)], null)!));
        Check("closed-generic-unused-argument", () => HasName(null, typeof(ClosedProbe<Unrelated>), "missing", false));
        Check("fresh", () => !ReferenceEquals(RuntimeFilteredControl( typeof(RequiredProbe), typeof(ProbeAttribute), false), RuntimeFilteredControl( typeof(RequiredProbe), typeof(ProbeAttribute), false)));
        var value = new Payload { Name = "Ada", Count = 7 };
        var json = JsonSerializer.Serialize(value, Context.Default.Payload);
        Console.WriteLine("serialize:" + json);
        Console.WriteLine("deserialize:" + JsonSerializer.Deserialize(json, Context.Default.Payload)!.Name);
        Console.WriteLine("missing-required:" + Error(() => JsonSerializer.Deserialize("{}", Context.Default.Payload)));
        return 0;
    }

    private static void Check(string name, Func<object?> action)
    {
        Trace.Clear();
        var result = Error(action);
        Console.WriteLine(name + ":" + result + ":" + string.Join(",", Trace));
    }
    private static string Error(Func<object?> action)
    {
        try { return Convert.ToString(action(), System.Globalization.CultureInfo.InvariantCulture) ?? "null"; }
        catch (Exception error) { return error.GetType().Name + "|" + error.HResult + "|" + error.Message + "|" + error.InnerException?.GetType().Name; }
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "GetUniqueCustomAttribute")]
    private static extern TAttribute? Unique<TAttribute>([UnsafeAccessorType("System.Text.Json.Reflection.ReflectionExtensions,System.Text.Json")] object? target, MemberInfo element, bool inherit) where TAttribute : Attribute;
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "HasCustomAttributeWithName")]
    private static extern bool HasName([UnsafeAccessorType("System.Text.Json.Reflection.ReflectionExtensions,System.Text.Json")] object? target, MemberInfo element, string name, bool inherit);
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "HasJsonConstructorAttribute")]
    private static extern bool HasJsonConstructor([UnsafeAccessorType("System.Text.Json.Reflection.ReflectionExtensions,System.Text.Json")] object? target, ConstructorInfo element);
    private static TAttribute? SingleControl<TAttribute>(MemberInfo element, bool inherit) where TAttribute : Attribute
        => RuntimeFeature.IsDynamicCodeSupported ? element.GetCustomAttribute<TAttribute>(inherit) : Single<TAttribute>(null, element, inherit);
    private static object[] RuntimeFilteredControl(Type element, Type attributeType, bool inherit)
        => RuntimeFeature.IsDynamicCodeSupported ? element.GetCustomAttributes(attributeType, inherit) : RuntimeFiltered(null, element, attributeType, inherit);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "ReadSingle")]
    private static extern TAttribute? Single<TAttribute>([UnsafeAccessorType("Neemle.DBAL.Runtime.NativeJsonMemberAttributeQueries,System.Text.Json")] object? target, MemberInfo element, bool inherit) where TAttribute : Attribute;
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "ReadRuntimeFiltered")]
    private static extern object[] RuntimeFiltered([UnsafeAccessorType("Neemle.DBAL.Runtime.NativeJsonMemberAttributeQueries,System.Text.Json")] object? target, Type element, Type attributeType, bool inherit);

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = true)]
    private sealed class ProbeAttribute(string id) : Attribute
    {
        public string Id { get; } = Record(id);
        private static string Record(string id)
        {
            Trace.Add("new:" + id);
            if (id == "throw") throw new InvalidOperationException("probe constructor failed");
            return id;
        }
        public string? Note { get => null; set { Trace.Add("set:" + Id + ":" + value); if (value == "throw") throw new InvalidOperationException("probe setter failed"); } }
    }
    private sealed class UnusedAttribute : Attribute;

    [Probe("type", Note = "named")]
    private sealed class RequiredProbe
    {
        [Probe("property", Note = "named")]
        public required string Value { get; init; }
        [Probe("constructor")]
        public RequiredProbe() { }
        [Probe("private-never", Note = "throw")]
        private RequiredProbe(int _) { }
    }
    [Probe("base")]
    private class BaseProbe
    {
        [Probe("base-property"), JsonPropertyName("inherited-json")]
        public virtual required string Value { get; init; }
    }
    [Probe("derived")]
    private sealed class DerivedProbe : BaseProbe
    {
        [Probe("derived-property")]
        public override required string Value { get; init; }
    }
    [Probe("first"), Probe("second", Note = "named")]
    private sealed class DuplicateProbe;
    [Probe("throw")]
    private sealed class ThrowConstructorProbe;
    [Probe("setter", Note = "throw")]
    private sealed class ThrowSetterProbe;
    [Probe("closed")]
    private sealed class ClosedProbe<T> { [JsonPropertyName("closed_name")] public string? Name { get; set; } }
    [Probe("unrelated-never")]
    private sealed class Unrelated;
    private sealed class Payload
    {
        public required string Name { get; init; }
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int Count { get; init; }
    }
    private sealed class AllMemberAttributes
    {
        [JsonConstructor] public AllMemberAttributes() { }
        [JsonInclude] public string? Included { get; internal set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Ignored { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
        [JsonPropertyOrder(7), JsonPropertyName("wire_name"), JsonRequired] public string? Name { get; set; }
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public int Number { get; set; }
        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)] public List<int> Items { get; } = [];
    }
#if JSON_ROLE_SMOKE
    #if JSON_RAW_ROLE_SMOKE
[JsonSerializable(typeof(Neemle.DBAL.JsonRawRoles.EnumOwner))]
[JsonSerializable(typeof(Neemle.DBAL.JsonRawRoles.CollectionOwner))]
[JsonSerializable(typeof(Neemle.DBAL.JsonRawRoles.GuardedDerived))]
[JsonSerializable(typeof(Neemle.DBAL.JsonRawRoles.ConvertedOwner))]
[JsonSerializable(typeof(Neemle.DBAL.JsonRawRoles.JsonRequiredOwner))]
[JsonSerializable(typeof(Neemle.DBAL.JsonRawRoles.IgnoredDerived))]
[JsonSerializable(typeof(Neemle.DBAL.JsonRawRoles.MemberBoundaries))]
[JsonSerializable(typeof(Neemle.DBAL.JsonRawRoles.InvalidIgnoredOwner))]
#endif
[JsonSerializable(typeof(global::Neemle.DBAL.JsonMemberConformance.UnannotatedStruct))]
    [JsonSerializable(typeof(global::Neemle.DBAL.JsonMemberConformance.AdvertisedDerived))]
    [JsonSerializable(typeof(KeyValuePair<string, int>))]
    [JsonSerializable(typeof(global::Neemle.DBAL.JsonMemberConformance.UnknownMappingStruct))]
#endif
    [JsonSerializable(typeof(AllMemberAttributes))]
    [JsonSerializable(typeof(RequiredProbe))]
    [JsonSerializable(typeof(BaseProbe))]
    [JsonSerializable(typeof(DerivedProbe))]
    [JsonSerializable(typeof(DuplicateProbe))]
    [JsonSerializable(typeof(ThrowConstructorProbe))]
    [JsonSerializable(typeof(ThrowSetterProbe))]
    [JsonSerializable(typeof(ClosedProbe<Unrelated>))]
    [JsonSerializable(typeof(Payload))]
    private partial class Context : JsonSerializerContext;

    private sealed class VirtualProperty : PropertyInfo
    {
        public int Calls;
        public override object[] GetCustomAttributes(Type type, bool inherit) { Calls++; return [new ProbeAttribute("virtual")]; }
        public override object[] GetCustomAttributes(bool inherit) { Calls++; return [new ProbeAttribute("virtual-all")]; }
        public override bool IsDefined(Type type, bool inherit) => false;
        public override PropertyAttributes Attributes => PropertyAttributes.None;
        public override bool CanRead => false;
        public override bool CanWrite => false;
        public override Type PropertyType => typeof(string);
        public override Type DeclaringType => typeof(RequiredProbe);
        public override Type ReflectedType => typeof(RequiredProbe);
        public override string Name => "Virtual";
        public override MethodInfo[] GetAccessors(bool nonPublic) => [];
        public override MethodInfo? GetGetMethod(bool nonPublic) => null;
        public override MethodInfo? GetSetMethod(bool nonPublic) => null;
        public override ParameterInfo[] GetIndexParameters() => [];
        public override object? GetValue(object? obj, BindingFlags flags, Binder? binder, object?[]? index, System.Globalization.CultureInfo? culture) => null;
        public override void SetValue(object? obj, object? value, BindingFlags flags, Binder? binder, object?[]? index, System.Globalization.CultureInfo? culture) => throw new NotSupportedException();
    }
}

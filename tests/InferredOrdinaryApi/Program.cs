// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

public static class Entry
{
    public static string Run(JsonSerializerOptions defaultOptions)
    {
        var lines = new List<string>();
        Check(Parent.Constructions == 0 && Child.Constructions == 0, "registration executes no constructors");
        var value = new Parent { Name = "Ada", Child = new Child { Number = 42 } };
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        string json = JsonSerializer.Serialize(value, options);
        Check(json == "{\"name\":\"Ada\",\"child\":{\"number\":42}}", "root and transitive child");
        Parent copy = JsonSerializer.Deserialize<Parent>(json, options)!;
        Check(copy.Name == "Ada" && copy.Child.Number == 42 && options.IsReadOnly, "deserialization and readonly transition");
        JsonTypeInfo first = options.GetTypeInfo(typeof(Parent));
        Check(ReferenceEquals(first, options.GetTypeInfo(typeof(Parent))) && ReferenceEquals(first.Options, options), "cache and caller options identity");

        var converted = new JsonSerializerOptions();
        converted.Converters.Add(new ParentConverter());
        Check(JsonSerializer.Serialize(value, converted) == "\"converted:Ada\"", "custom converter wins");
        Check(JsonSerializer.Deserialize<Parent>("\"read\"", converted)!.Name == "read", "custom read converter wins");

        IJsonTypeInfoResolver resolver = defaultOptions.TypeInfoResolver!;
        Check(defaultOptions.IsReadOnly, "builtin default readonly");
        string defaults = JsonSerializer.Serialize(value);
        Check(defaults.Contains("\"Name\":\"Ada\"", StringComparison.Ordinal), "implicit builtin default");
        Check(JsonSerializer.Serialize(value, defaultOptions) == defaults && ReferenceEquals(resolver, defaultOptions.TypeInfoResolver), "readonly default resolver unchanged");
        var clonedDefault = new JsonSerializerOptions(defaultOptions);
        Expect<InvalidOperationException>(() => JsonSerializer.Serialize(value, clonedDefault), "copy of builtin Empty has no inferred privilege");
        var explicitEmpty = new JsonSerializerOptions { TypeInfoResolver = resolver };
        Expect<InvalidOperationException>(() => JsonSerializer.Serialize(value, explicitEmpty), "explicit Empty cannot reuse builtin cache");

        var nullResolver = new NullResolver();
        var nullOptions = new JsonSerializerOptions { TypeInfoResolver = nullResolver };
        Expect<NotSupportedException>(() => JsonSerializer.Serialize(value, nullOptions), "custom null has no fallback");
        Check(nullResolver.Calls == 1 && ReferenceEquals(nullResolver.Options, nullOptions), "custom resolver caller options");
        var throwOptions = new JsonSerializerOptions { TypeInfoResolver = new ThrowResolver() };
        Expect<ResolverFailure>(() => JsonSerializer.Serialize(value, throwOptions), "custom throw has no fallback");

        var custom = new RecordingResolver(options.TypeInfoResolver!);
        var replaced = new JsonSerializerOptions(options) { TypeInfoResolver = custom };
        Check(JsonSerializer.Serialize(value, replaced) == json && ReferenceEquals(custom.Options, replaced), "copy and explicit resolver use same options");
        var chain = new JsonSerializerOptions(options);
        chain.TypeInfoResolverChain.Clear();
        chain.TypeInfoResolverChain.Add(new NullResolver());
        Expect<NotSupportedException>(() => JsonSerializer.Serialize(value, chain), "chain replacement has no inferred fallback");
        var readonlyOptions = new JsonSerializerOptions { TypeInfoResolver = options.TypeInfoResolver };
        readonlyOptions.MakeReadOnly();
        Check(JsonSerializer.Serialize(value, readonlyOptions) == defaults && readonlyOptions.IsReadOnly, "supplied readonly resolver");
        var unknown = new Unknown();
        Expect<NotSupportedException>(() => options.GetTypeInfo(unknown.GetType()), "unknown runtime type actionable");
        var untouched = new NullResolver();
        Expect<ArgumentNullException>(() => JsonSerializer.Deserialize<Parent>((string)null!, new JsonSerializerOptions { TypeInfoResolver = untouched }), "null input precedes resolver");
        Check(untouched.Calls == 0, "null input has no resolver effects");
        Check(JsonSerializer.Serialize(17, options) == "17" && JsonSerializer.Deserialize<int>("18", options) == 18, "closed value type calls");
        Parent? absent = null;
        Check(JsonSerializer.Serialize(absent, options) == "null" && JsonSerializer.Deserialize<Parent?>("null", options) is null, "nullable reference call contracts");
        return string.Join('\n', lines);

        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            lines.Add(label);
        }
        void Expect<T>(Action action, string label) where T : Exception
        {
            try { action(); } catch (T) { lines.Add(label); return; }
            throw new InvalidOperationException(label);
        }
    }
}

public sealed class Parent
{
    public static int Constructions;
    public Parent() => Constructions++;
    public string Name { get; set; } = "";
    public Child Child { get; set; } = null!;
}
public sealed class Child
{
    public static int Constructions;
    public Child() => Constructions++;
    public int Number { get; set; }
}
public sealed class Unknown { }
public sealed class ParentConverter : JsonConverter<Parent>
{
    public override Parent? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new() { Name = reader.GetString()! };
    public override void Write(Utf8JsonWriter writer, Parent value, JsonSerializerOptions options) => writer.WriteStringValue("converted:" + value.Name);
}
public sealed class NullResolver : IJsonTypeInfoResolver
{
    public int Calls;
    public JsonSerializerOptions? Options;
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) { Calls++; Options = options; return null; }
}
public sealed class ResolverFailure : Exception { }
public sealed class ThrowResolver : IJsonTypeInfoResolver
{
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => throw new ResolverFailure();
}
public sealed class RecordingResolver(IJsonTypeInfoResolver inner) : IJsonTypeInfoResolver
{
    public JsonSerializerOptions? Options;
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) { Options = options; return inner.GetTypeInfo(type, options); }
}

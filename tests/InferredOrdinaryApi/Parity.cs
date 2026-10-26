// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

public static class Entry
{
    public static string Run()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, NumberHandling = JsonNumberHandling.AllowReadingFromString };
        var payload = new Payload { Name = "Ada", Count = 7, Child = new Child { Value = 23 } };
        string json = JsonSerializer.Serialize(payload, options);
        Payload value = JsonSerializer.Deserialize<Payload>(json, options)!;
        string nested = JsonSerializer.Serialize(new Envelope<Payload> { Value = payload, Items = [payload] }, options);
        Envelope<Payload> roundtrip = JsonSerializer.Deserialize<Envelope<Payload>>(nested, options)!;
        var custom = new JsonSerializerOptions();
        custom.Converters.Add(new PayloadConverter());
        string converted = JsonSerializer.Serialize(payload, custom);
        string convertedName = JsonSerializer.Deserialize<Payload>("\"Grace\"", custom)!.Name;
        string defaults = JsonSerializer.Serialize(payload);
        return string.Join('\n', json, value.Name + ":" + value.Count + ":" + value.Child.Value, nested, roundtrip.Items.Length, converted, convertedName, defaults);
    }
}
public sealed class Payload
{
    public required string Name { get; init; }
    public int Count { get; init; }
    public required Child Child { get; init; }
}
public sealed class Child { public int Value { get; init; } }
public sealed class Envelope<T>
{
    public required T Value { get; init; }
    public required T[] Items { get; init; }
}

public sealed class PayloadConverter : JsonConverter<Payload>
{
    public override Payload Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => new Payload { Name = reader.GetString()!, Count = 1, Child = new Child() };
    public override void Write(Utf8JsonWriter writer, Payload value, JsonSerializerOptions options)
        => writer.WriteStringValue("custom:" + value.Name);
}

// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Text.Json;
using System.Text.Json.Serialization;

var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, NumberHandling = JsonNumberHandling.AllowReadingFromString };
var payload = new Payload { Name = "Ada", Count = 7 };
var json = JsonSerializer.Serialize(payload, options);
Console.WriteLine("ordinary-payload:" + json);
var restored = JsonSerializer.Deserialize<Payload>("{\"name\":\"Ada\",\"count\":\"7\"}", options)!;
Console.WriteLine("ordinary-roundtrip:" + restored.Name + ":" + restored.Count);
var envelope = new Envelope<Payload> { Value = payload, Items = [payload] };
var nested = JsonSerializer.Serialize(envelope, options);
Console.WriteLine("ordinary-nested:" + nested);
Console.WriteLine("ordinary-nested-read:" + JsonSerializer.Deserialize<Envelope<Payload>>(nested, options)!.Items[0].Name);

internal sealed class Payload
{
    public required string Name { get; init; }
    public int Count { get; init; }
}
internal sealed class Envelope<T>
{
    public T? Value { get; init; }
    public List<T> Items { get; init; } = [];
}

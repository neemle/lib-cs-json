// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;

var context = DemoContext.Default;
var payload = new Payload { Name = "Ada", Count = 7 };
var json = JsonSerializer.Serialize(payload, context.Payload);
Console.WriteLine("payload:" + json);
Console.WriteLine("roundtrip:" + JsonSerializer.Deserialize(json, context.Payload)!.Name);
Console.WriteLine("float:" + JsonSerializer.Serialize(1.25f, context.Single));
Console.WriteLine("list:" + JsonSerializer.Serialize(new List<int> { 1, 2 }, context.ListInt32));
Animal animal = new Cat { Name = "Mira" };
var polymorphic = JsonSerializer.Serialize(animal, context.Animal);
Console.WriteLine("polymorphic:" + polymorphic);
Console.WriteLine("derived:" + (JsonSerializer.Deserialize(polymorphic, context.Animal) is Cat));
Console.WriteLine("schema:" + JsonSchemaExporter.GetJsonSchemaAsNode(context.Payload).ToJsonString());
Console.WriteLine("identity:" + typeof(JsonSerializer).Assembly.FullName);
public sealed class Payload { public required string Name { get; init; } public int Count { get; init; } }
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(Cat), "cat")]
public abstract class Animal;
public sealed class Cat : Animal { public required string Name { get; init; } }
[JsonSerializable(typeof(Payload))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(List<int>))]
[JsonSerializable(typeof(Animal))]
internal partial class DemoContext : JsonSerializerContext;

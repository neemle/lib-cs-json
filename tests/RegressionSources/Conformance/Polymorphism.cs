// Copyright (c) Neemle contributors. Licensed under the MIT License.
using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neemle.DBAL.Conformance.Json;

internal static class Polymorphism
{
    internal static void Run()
    {
        var context = PolymorphismContext.Default;
        Check(JsonSerializer.Serialize(1.25f, context.Single) == "1.25", "scalar metadata");
        var memory = new ReadOnlyMemory<float>(new[] { 1.25f, -2.5f });
        var collection = JsonSerializer.Serialize(memory, context.ReadOnlyMemorySingle);
        Check(collection == "[1.25,-2.5]", "collection metadata");
        Check(JsonSerializer.Deserialize(collection, context.ReadOnlyMemorySingle).Span.SequenceEqual(memory.Span), "collection round trip");
        var envelope = new Envelope<Unused> { Value = "no generic argument serialization" };
        var envelopeJson = JsonSerializer.Serialize(envelope, context.EnvelopeUnused);
        Check(envelopeJson == "{\"Value\":\"no generic argument serialization\"}", "unused generic argument");
        Check(JsonSerializer.Deserialize(envelopeJson, context.EnvelopeUnused)!.Value == envelope.Value, "generic envelope round trip");

        Animal cat = new Cat { Name = "Murka", Lives = 9 };
        var json = JsonSerializer.Serialize(cat, context.Animal);
        Check(json == "{\"$kind\":\"cat\",\"Lives\":9,\"Name\":\"Murka\"}", "derived discriminator serialization");
        Check(JsonSerializer.Deserialize(json, context.Animal) is Cat { Name: "Murka", Lives: 9 }, "derived discriminator deserialization");
        var derived = context.Animal.PolymorphismOptions!.DerivedTypes;
        Check(derived.Count == 2 && derived[0].DerivedType == typeof(Cat) && Equals(derived[0].TypeDiscriminator, "cat")
            && derived[1].DerivedType == typeof(Dog) && Equals(derived[1].TypeDiscriminator, "dog"), "derived declaration order");
        Check(context.Animal.PolymorphismOptions.TypeDiscriminatorPropertyName == "$kind"
            && !context.Animal.PolymorphismOptions.IgnoreUnrecognizedTypeDiscriminators
            && context.Animal.PolymorphismOptions.UnknownDerivedTypeHandling == JsonUnknownDerivedTypeHandling.FailSerialization,
            "named polymorphism values");
        var dog = JsonSerializer.Deserialize("{\"$kind\":\"dog\",\"Good\":true,\"Name\":\"Rex\"}", context.Animal);
        Check(dog is Dog { Name: "Rex", Good: true }, "second derived discriminator");
        Check(context.Cat.PolymorphismOptions is null, "noninherited polymorphism attributes");
        Expect<JsonException>(() => JsonSerializer.Deserialize("{\"$kind\":\"unknown\"}", context.Animal), "unknown discriminator");
        Expect<NotSupportedException>(() => JsonSerializer.Serialize<Animal>(new Unregistered { Name = "unregistered" }, context.Animal), "unknown derived type");
        Console.WriteLine("json-polymorphism: scalar,collection,generic-envelope,ordered-derived,named-options,noninheritance,serialize,deserialize,exact-errors PASS");
    }

    private static void Expect<TException>(Action operation, string scenario) where TException : Exception
    {
        try { operation(); }
        catch (TException error)
        {
            Check(error.GetType() == typeof(TException), scenario + " exact exception type");
            Console.WriteLine("error:" + scenario + "|" + error.GetType().FullName + "|" + error.HResult + "|" + error.Message);
            return;
        }
        throw new InvalidOperationException(scenario + " did not throw " + typeof(TException).Name);
    }

    private static void Check(bool condition, string scenario)
    {
        if (!condition) throw new InvalidOperationException(scenario);
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind", IgnoreUnrecognizedTypeDiscriminators = false,
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(Cat), "cat")]
[JsonDerivedType(typeof(Dog), "dog")]
internal abstract class Animal
{
    public string Name { get; set; } = "";
}

internal sealed class Cat : Animal
{
    public int Lives { get; set; }
}

internal sealed class Dog : Animal
{
    public bool Good { get; set; }
}

internal sealed class Unregistered : Animal;

internal sealed class Envelope<T>
{
    public string Value { get; set; } = "";
}

internal sealed class Unused;

[JsonSerializable(typeof(Animal))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(ReadOnlyMemory<float>))]
[JsonSerializable(typeof(Envelope<Unused>))]
internal partial class PolymorphismContext : JsonSerializerContext;

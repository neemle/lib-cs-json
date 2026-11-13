using System.Text.Json;
using System.Text.Json.Serialization;

internal static class DemoPolymorphism
{
    internal static void Run()
    {
        Animal dog = new Dog { Name = "Ada", Barks = true };
        string json = JsonSerializer.Serialize(dog);
        var decoded = JsonSerializer.Deserialize<Animal>(json);
        DemoCheck.Expect("polymorphism.string", json == "{\"kind\":\"dog\",\"Barks\":true,\"Name\":\"Ada\"}" &&
            decoded is Dog { Name: "Ada", Barks: true }, json);
        Animal cat = new Cat { Name = "Lin", Lives = 9 };
        string number = JsonSerializer.Serialize(cat);
        var copy = JsonSerializer.Deserialize<Animal>(number);
        DemoCheck.Expect("polymorphism.integer", number == "{\"kind\":7,\"Lives\":9,\"Name\":\"Lin\"}" &&
            copy is Cat { Name: "Lin", Lives: 9 }, number);
        Animal fish = new Fish { Name = "Fin", Fins = 2 };
        string fishJson = JsonSerializer.Serialize(fish);
        var fishCopy = JsonSerializer.Deserialize<Animal>(fishJson);
        DemoCheck.Expect("polymorphism.many-types", fishCopy is Fish { Name: "Fin", Fins: 2 }, fishJson);
        bool rejected = false;
        try { _ = JsonSerializer.Deserialize<Animal>("{\"kind\":\"unknown\"}"); }
        catch (JsonException) { rejected = true; }
        DemoCheck.Expect("polymorphism.unknown", rejected, rejected ? "rejected" : "accepted");
        var options = new JsonSerializerOptions { AllowOutOfOrderMetadataProperties = true };
        var unordered = JsonSerializer.Deserialize<Animal>("{\"Name\":\"Ada\",\"kind\":\"dog\",\"Barks\":true}",
            options);
        DemoCheck.Expect("polymorphism.metadata-order", unordered is Dog { Name: "Ada", Barks: true }, "dog");
        Fallback();
        IShape shape = new Circle { Radius = 3 };
        string shapeJson = JsonSerializer.Serialize(shape);
        IShape? shapeCopy = JsonSerializer.Deserialize<IShape>(shapeJson);
        DemoCheck.Expect("polymorphism.interface", shapeCopy is Circle { Radius: 3 }, shapeJson);
    }

    private static void Fallback()
    {
        var unknown = JsonSerializer.Deserialize<LooseAnimal>("{\"$type\":\"unknown\",\"Name\":\"Ada\"}");
        DemoCheck.Expect("polymorphism.unknown-fallback", unknown is { Name: "Ada" }, unknown?.Name ?? "null");
        LooseAnimal child = new LooseCat { Name = "Ada", Extra = 1 };
        string json = JsonSerializer.Serialize(child);
        DemoCheck.Expect("polymorphism.runtime-fallback", json == "{\"Name\":\"Ada\"}", json);
        NearAnimal puppy = new Puppy { Name = "Ada", Barks = true, Age = 2 };
        string nearest = JsonSerializer.Serialize(puppy);
        DemoCheck.Expect("polymorphism.nearest-ancestor", nearest ==
            "{\"$type\":\"dog\",\"Barks\":true,\"Name\":\"Ada\"}", nearest);
    }
}

[JsonDerivedType(typeof(Circle), "circle")]
public interface IShape { }

public sealed class Circle : IShape
{
    public int Radius { get; set; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Dog), "dog")]
[JsonDerivedType(typeof(Cat), 7)]
[JsonDerivedType(typeof(Bird), "bird")]
[JsonDerivedType(typeof(Fish), 8)]
public abstract class Animal
{
    public string Name { get; set; } = "";
}

public sealed class Dog : Animal
{
    public bool Barks { get; set; }
}

public sealed class Cat : Animal
{
    public int Lives { get; set; }
}

public sealed class Bird : Animal
{
    public bool Flies { get; set; }
}

public sealed class Fish : Animal
{
    public int Fins { get; set; }
}

[JsonPolymorphic(IgnoreUnrecognizedTypeDiscriminators = true,
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType)]
[JsonDerivedType(typeof(LooseDog), "dog")]
public class LooseAnimal
{
    public string Name { get; set; } = "";
}

public sealed class LooseDog : LooseAnimal
{
    public bool Barks { get; set; }
}

public sealed class LooseCat : LooseAnimal
{
    public int Extra { get; set; }
}

[JsonPolymorphic(UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToNearestAncestor)]
[JsonDerivedType(typeof(NearDog), "dog")]
public class NearAnimal
{
    public string Name { get; set; } = "";
}

public class NearDog : NearAnimal
{
    public bool Barks { get; set; }
}

public sealed class Puppy : NearDog
{
    public int Age { get; set; }
}

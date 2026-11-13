using System.Text.Json;
using System.Text.Json.Serialization;

internal static class DemoReferences
{
    internal static void Run()
    {
        var options = new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.IgnoreCycles };
        var cyclic = new CycleModel();
        cyclic.Next = cyclic;
        string json = JsonSerializer.Serialize(cyclic, options);
        DemoCheck.Expect("references.ignore-cycle", json == "{\"Next\":null}", json);
        var shared = new CycleModel();
        var pair = new ReferencePair { Left = shared, Right = shared };
        string pairJson = JsonSerializer.Serialize(pair, options);
        DemoCheck.Expect("references.shared", pairJson == "{\"Left\":{\"Next\":null},\"Right\":{\"Next\":null}}",
            pairJson);
        var copy = JsonSerializer.Deserialize<ReferencePair>(pairJson, options);
        DemoCheck.Expect("references.decode", copy?.Left is not null && copy.Right is not null, pairJson);
        Preserve();
        Custom();
    }

    private static void Custom()
    {
        var options = new JsonSerializerOptions { ReferenceHandler = new DemoReferenceHandler() };
        var cyclic = new CycleModel();
        cyclic.Next = cyclic;
        string json = JsonSerializer.Serialize(cyclic, options);
        var copy = JsonSerializer.Deserialize<CycleModel>(json, options);
        DemoCheck.Expect("references.custom-handler", copy is not null && ReferenceEquals(copy, copy.Next), json);
    }

    private static void Preserve()
    {
        var options = new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.Preserve };
        var cyclic = new CycleModel();
        cyclic.Next = cyclic;
        string json = JsonSerializer.Serialize(cyclic, options);
        var decoded = JsonSerializer.Deserialize<CycleModel>(json, options);
        DemoCheck.Expect("references.preserve-cycle", json == "{\"$id\":\"1\",\"Next\":{\"$ref\":\"1\"}}" &&
            decoded is not null && ReferenceEquals(decoded, decoded.Next), json);
        var shared = new CycleModel();
        var pair = new ReferencePair { Left = shared, Right = shared };
        string pairJson = JsonSerializer.Serialize(pair, options);
        var pairCopy = JsonSerializer.Deserialize<ReferencePair>(pairJson, options);
        DemoCheck.Expect("references.preserve-shared", pairCopy?.Left is not null &&
            ReferenceEquals(pairCopy.Left, pairCopy.Right), pairJson);
        PreserveCollections(shared);
        Animal dog = new Dog { Name = "Ada", Barks = true };
        string dogJson = JsonSerializer.Serialize(dog, options);
        var dogCopy = JsonSerializer.Deserialize<Animal>(dogJson, options);
        DemoCheck.Expect("references.preserve-polymorphism", dogCopy is Dog { Name: "Ada", Barks: true }, dogJson);
    }

    private static void PreserveCollections(CycleModel shared)
    {
        var options = new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.Preserve };
        var list = new List<CycleModel> { shared, shared };
        string json = JsonSerializer.Serialize(list, options);
        var copy = JsonSerializer.Deserialize<List<CycleModel>>(json, options);
        DemoCheck.Expect("references.preserve-list", copy is { Count: 2 } && ReferenceEquals(copy[0], copy[1]), json);
        var map = new Dictionary<string, CycleModel> { ["a"] = shared, ["b"] = shared };
        string mapJson = JsonSerializer.Serialize(map, options);
        var mapCopy = JsonSerializer.Deserialize<Dictionary<string, CycleModel>>(mapJson, options);
        DemoCheck.Expect("references.preserve-map", mapCopy is not null && ReferenceEquals(mapCopy["a"], mapCopy["b"]),
            mapJson);
        CycleModel[] array = [shared, shared];
        string arrayJson = JsonSerializer.Serialize(array, options);
        var arrayCopy = JsonSerializer.Deserialize<CycleModel[]>(arrayJson, options);
        DemoCheck.Expect("references.preserve-array", arrayCopy is { Length: 2 } &&
            ReferenceEquals(arrayCopy[0], arrayCopy[1]), arrayJson);
        bool rejected = false;
        try { _ = JsonSerializer.Deserialize<CycleModel>("{\"$ref\":\"unknown\"}", options); }
        catch (JsonException) { rejected = true; }
        DemoCheck.Expect("references.missing-id", rejected, rejected ? "rejected" : "accepted");
    }
}

public sealed class ReferencePair
{
    public CycleModel? Left { get; set; }
    public CycleModel? Right { get; set; }
}

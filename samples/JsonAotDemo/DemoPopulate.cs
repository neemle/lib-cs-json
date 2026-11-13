using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;

internal static class DemoPopulate
{
    internal static void Run()
    {
        var options = new JsonSerializerOptions
        {
            PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate
        };
        PopulateCase value = JsonSerializer.Deserialize<PopulateCase>(
            "{\"Items\":[2],\"Value\":{\"Number\":9}}", options)
            ?? throw new InvalidOperationException("Deserialization returned null.");
        DemoCheck.Expect("populate.readonly-list", value.Items.Count == 2
            && value.Items[0] == 1 && value.Items[1] == 2,
            $"count={value.Items.Count}");
        DemoCheck.Expect("populate.readonly-value", value.Value.Number == 3,
            $"number={value.Value.Number}");
        bool rejected = false;
        try { _ = JsonSerializer.Deserialize<PopulateInvalid>("{}"); }
        catch (InvalidOperationException) { rejected = true; }
        DemoCheck.Expect("populate.value-requires-setter", rejected, rejected ? "rejected" : "accepted");
        Writable(options);
    }

    private static void Writable(JsonSerializerOptions options)
    {
        var populated = JsonSerializer.Deserialize<WritablePopulateModel>("{\"Items\":[2]}", options);
        var replaced = JsonSerializer.Deserialize<WritablePopulateModel>("{\"Items\":[2]}");
        DemoCheck.Expect("populate.writable-list", populated?.Items.SequenceEqual([1, 2]) == true &&
            replaced?.Items.SequenceEqual([2]) == true, string.Join(',', populated!.Items));
        var attributed = JsonSerializer.Deserialize<AttributedPopulateModel>("{\"Items\":[2],\"Replace\":[3]}");
        DemoCheck.Expect("populate.type-and-member-attribute", attributed?.Items.SequenceEqual([1, 2]) == true &&
            attributed.Replace.SequenceEqual([3]), string.Join(',', attributed!.Items));
        Collections(options);
        Objects(options);
        SpecialCases(options);
    }

    private static void Collections(JsonSerializerOptions options)
    {
        var map = JsonSerializer.Deserialize<PopulateMapModel>(
            "{\"Writable\":{\"a\":2,\"b\":3},\"Readonly\":{\"a\":4,\"b\":5},\"Interface\":{\"b\":6}}", options);
        DemoCheck.Expect("populate.maps", map?.Writable is { Count: 2 } && map.Writable["a"] == 2 &&
            map.Writable["b"] == 3 && map.Readonly["a"] == 4 && map.Readonly["b"] == 5 &&
            map.Interface["a"] == 1 && map.Interface["b"] == 6, "merged");
        var numbers = JsonSerializer.Deserialize<ClassNumberModeModel>(
            "{\"Values\":[\"4\"],\"Map\":{\"a\":\"5\"}}", options);
        DemoCheck.Expect("populate.number-handling", numbers?.Values.SequenceEqual([4]) == true &&
            numbers.Map["a"] == 5, "numbers");
        var readonlyNumbers = JsonSerializer.Deserialize<PopulateNumberModel>(
            "{\"Items\":[\"4\"],\"Map\":{\"a\":\"5\"}}", options);
        DemoCheck.Expect("populate.readonly-number-handling", readonlyNumbers?.Items.SequenceEqual([4]) == true &&
            readonlyNumbers.Map["a"] == 5, "numbers");
        var sets = JsonSerializer.Deserialize<PopulateSetModel>(
            "{\"Items\":[2],\"Values\":[3],\"Queue\":[4],\"Stack\":[5],\"Custom\":[6]}", options);
        DemoCheck.Expect("populate.collections", sets?.Items.SetEquals([1, 2]) == true &&
            sets.Values.SequenceEqual([1, 3]) && sets.Queue.SequenceEqual([1, 4]) &&
            sets.Stack.SequenceEqual([5, 1]) && sets.Custom.SequenceEqual([1, 6]), "merged");
        var interfaces = JsonSerializer.Deserialize<PopulateReadOnlyInterfaces>(
            "{\"Writable\":[2],\"Readonly\":[3]}", options);
        DemoCheck.Expect("populate.readonly-interfaces", interfaces?.Writable.SequenceEqual([2]) == true &&
            interfaces.Readonly.SequenceEqual([1]), "replaced");
        var unsupported = JsonSerializer.Deserialize<PopulateReadonlyBag>("{\"Values\":[2]}", options);
        DemoCheck.Expect("populate.readonly-unsupported-collection", unsupported?.Values.SequenceEqual([1]) == true,
            "unchanged");
    }

    private static void Objects(JsonSerializerOptions options)
    {
        var objects = JsonSerializer.Deserialize<PopulateObjectModel>(
            "{\"Writable\":{\"Value\":2},\"Readonly\":{\"Value\":3},\"Struct\":{\"Value\":4}}", options);
        DemoCheck.Expect("populate.objects", objects?.Writable.Value == 2 && objects.Writable.Other == 9 &&
            objects.Readonly.Value == 3 && objects.Readonly.Other == 9 && objects.Struct.Value == 4 &&
            objects.Struct.Other == 9, "merged");
        var fields = JsonSerializer.Deserialize<PopulateFieldModel>(
            "{\"Items\":[2],\"Object\":{\"Value\":3},\"Readonly\":[4],\"Map\":{\"b\":5}," +
            "\"ReadonlyObject\":{\"Value\":6}}", options);
        DemoCheck.Expect("populate.fields", fields?.Items.SequenceEqual([1, 2]) == true &&
            fields.Object.Value == 3 && fields.Object.Other == 9 && fields.Readonly.SequenceEqual([1, 4]) &&
            fields.Map["a"] == 1 && fields.Map["b"] == 5 && fields.ReadonlyObject.Value == 6 &&
            fields.ReadonlyObject.Other == 9, "merged");
    }

    private static void SpecialCases(JsonSerializerOptions options)
    {
        var converterOptions = new JsonSerializerOptions
        {
            PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate
        };
        converterOptions.Converters.Add(new PopulateListConverter());
        var converted = JsonSerializer.Deserialize<WritablePopulateModel>("{\"Items\":[2]}", converterOptions);
        DemoCheck.Expect("populate.converter", converted?.Items.SequenceEqual([42]) == true, "converted");
        var extra = JsonSerializer.Deserialize<PopulateExtensionModel>("{\"fresh\":2}", options);
        DemoCheck.Expect("populate.extension", extra?.Extra["existing"].GetInt32() == 1 &&
            extra.Extra["fresh"].GetInt32() == 2, "merged");
    }
}

public sealed class PopulateObjectModel
{
    public PopulateNested Writable { get; set; } = new() { Other = 9 };
    public PopulateNested Readonly { get; } = new() { Other = 9 };
    public PopulateStruct Struct { get; set; } = new() { Other = 9 };
}

public struct PopulateStruct
{
    public int Value { get; set; }
    public int Other { get; set; }
}

public sealed class PopulateNested
{
    public int Value { get; set; }
    public int Other { get; set; } = 7;
}

public sealed class PopulateFieldModel
{
    [JsonInclude] public List<int> Items = [1];
    [JsonInclude] public PopulateNested Object = new() { Other = 9 };
    [JsonInclude] public readonly List<int> Readonly = [1];
    [JsonInclude] public readonly Dictionary<string, int> Map = new() { ["a"] = 1 };
    [JsonInclude] public readonly PopulateNested ReadonlyObject = new() { Other = 9 };
}

public sealed class PopulateListConverter : JsonConverter<List<int>>
{
    public override List<int> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        reader.Skip();
        return [42];
    }

    public override void Write(Utf8JsonWriter writer, List<int> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(42);
        writer.WriteEndArray();
    }
}

public sealed class PopulateExtensionModel
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = new()
    {
        ["existing"] = JsonSerializer.SerializeToElement(1)
    };
}

public sealed class PopulateSetModel
{
    public HashSet<int> Items { get; } = [1];
    public ICollection<int> Values { get; } = new List<int> { 1 };
    public Queue<int> Queue { get; } = new([1]);
    public Stack<int> Stack { get; } = new([1]);
    public CustomNumbers Custom { get; } = [1];
}

public sealed class PopulateReadOnlyInterfaces
{
    public IEnumerable<int> Writable { get; set; } = new List<int> { 1 };
    public IEnumerable<int> Readonly { get; } = new List<int> { 1 };
}

public sealed class PopulateReadonlyBag
{
    public ConcurrentBag<int> Values { get; } = new([1]);
}

[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public sealed class PopulateNumberModel
{
    public List<int> Items { get; } = [];
    public Dictionary<string, int> Map { get; } = [];
}

public sealed class PopulateMapModel
{
    public Dictionary<string, int> Writable { get; set; } = new() { ["a"] = 1 };
    public Dictionary<string, int> Readonly { get; } = new() { ["a"] = 1 };
    public IDictionary<string, int> Interface { get; } = new Dictionary<string, int> { ["a"] = 1 };
}

public sealed class WritablePopulateModel
{
    public List<int> Items { get; set; } = [1];
}

[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public sealed class AttributedPopulateModel
{
    public List<int> Items { get; set; } = [1];

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Replace)]
    public List<int> Replace { get; set; } = [1];
}

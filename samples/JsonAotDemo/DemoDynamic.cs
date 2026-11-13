using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

internal sealed class InternalPayload
{
    public int Value { get; set; }
}

public class IncludedInternalPayload
{
    [JsonInclude]
    internal int Hidden { get; set; }

    [JsonInclude]
    public int Count { get; internal set; }

    [JsonInclude]
    internal int Field;

    [JsonInclude]
    protected internal int Shared { get; set; }
}

public sealed class IncludedPrivatePayload
{
    [JsonInclude]
    private int Secret { get; set; }

    [JsonInclude]
    private int hiddenField;

    [JsonInclude]
    private readonly int fixedField = 12;

    internal static IncludedPrivatePayload Create() => new() { Secret = 7, hiddenField = 8 };
    internal bool Matches() => Secret == 7 && hiddenField == 8 && fixedField == 12;
}

public struct IncludedPrivateStruct
{
    [JsonInclude]
    private int Secret { get; set; }

    [JsonInclude]
    private int hiddenField;

    internal static IncludedPrivateStruct Create() => new() { Secret = 10, hiddenField = 11 };
    internal bool Matches() => Secret == 10 && hiddenField == 11;
}

internal static class DemoDynamic
{
    internal static void Run()
    {
        var internalJson = JsonSerializer.Serialize(new InternalPayload { Value = 42 });
        var internalCopy = JsonSerializer.Deserialize<InternalPayload>(internalJson);
        DemoCheck.Expect("object.internal", internalCopy is { Value: 42 }, internalJson);
        var included = new IncludedInternalPayload { Hidden = 3, Count = 4, Field = 5, Shared = 6 };
        string includedJson = JsonSerializer.Serialize(included);
        var includedCopy = JsonSerializer.Deserialize<IncludedInternalPayload>(includedJson);
        DemoCheck.Expect("object.included-internal", includedCopy is { Hidden: 3, Count: 4, Field: 5, Shared: 6 }, includedJson);
        string privateJson = JsonSerializer.Serialize(IncludedPrivatePayload.Create());
        var privateCopy = JsonSerializer.Deserialize<IncludedPrivatePayload>(privateJson);
        DemoCheck.Expect("object.included-private", privateCopy?.Matches() == true, privateJson);
        string privateStructJson = JsonSerializer.Serialize(IncludedPrivateStruct.Create());
        var privateStructCopy = JsonSerializer.Deserialize<IncludedPrivateStruct>(privateStructJson);
        DemoCheck.Expect("object.included-private-struct", privateStructCopy.Matches(), privateStructJson);
        Type selected = DateTime.UtcNow.Year > 0 ? typeof(InternalPayload) : typeof(string);
        string runtimeJson = JsonSerializer.Serialize((object)new InternalPayload { Value = 42 }, selected);
        object? runtimeCopy = JsonSerializer.Deserialize(runtimeJson, selected);
        DemoCheck.Expect("object.runtime-type", runtimeCopy is InternalPayload { Value: 42 }, runtimeJson);
        byte[] runtimeBytes = JsonSerializer.SerializeToUtf8Bytes((object)new InternalPayload { Value = 42 }, selected);
        object? bytesCopy = JsonSerializer.Deserialize(runtimeBytes, selected);
        DemoCheck.Expect("object.runtime-type-bytes", bytesCopy is InternalPayload { Value: 42 },
            System.Text.Encoding.UTF8.GetString(runtimeBytes));
        object? charsCopy = JsonSerializer.Deserialize(runtimeJson.AsSpan(), selected);
        DemoCheck.Expect("object.runtime-type-chars", charsCopy is InternalPayload { Value: 42 }, runtimeJson);
        JsonElement runtimeElement = JsonSerializer.SerializeToElement((object)new InternalPayload { Value = 42 }, selected);
        object? elementCopy = JsonSerializer.Deserialize(runtimeElement, selected);
        DemoCheck.Expect("object.runtime-type-element", elementCopy is InternalPayload { Value: 42 }, runtimeElement.GetRawText());
        using JsonDocument runtimeDocument = JsonSerializer.SerializeToDocument((object)new InternalPayload { Value = 42 }, selected);
        object? documentCopy = JsonSerializer.Deserialize(runtimeDocument, selected);
        DemoCheck.Expect("object.runtime-type-document", documentCopy is InternalPayload { Value: 42 },
            runtimeDocument.RootElement.GetRawText());
        JsonNode? runtimeNode = JsonSerializer.SerializeToNode((object)new InternalPayload { Value = 42 }, selected);
        object? nodeCopy = JsonSerializer.Deserialize(runtimeNode, selected);
        DemoCheck.Expect("object.runtime-type-node", nodeCopy is InternalPayload { Value: 42 }, runtimeNode!.ToJsonString());
        object number = 42;
        string numeric = JsonSerializer.Serialize(number);
        object? decoded = JsonSerializer.Deserialize<object>(numeric);
        DemoCheck.Expect("object.number", numeric == "42" && decoded is JsonElement element &&
            element.GetInt32() == 42, numeric);
        object produced = Enumerable.Repeat(new Uri("https://example.com/"), 1).ToArray();
        string producedJson = JsonSerializer.Serialize(produced);
        DemoCheck.Expect("object.inferred-array", producedJson == "[\"https://example.com/\"]", producedJson);
        object value = new NamingCaseModel { DisplayName = "Ada" };
        string json = JsonSerializer.Serialize(value);
        object? copy = JsonSerializer.Deserialize<object>(json);
        DemoCheck.Expect("object.model", json == "{\"DisplayName\":\"Ada\"}" && copy is JsonElement model &&
            model.GetProperty("DisplayName").GetString() == "Ada", json);
        var options = new JsonSerializerOptions { UnknownTypeHandling = JsonUnknownTypeHandling.JsonNode };
        object? node = JsonSerializer.Deserialize<object>(json, options);
        DemoCheck.Expect("object.json-node", node is JsonNode jsonNode &&
            jsonNode["DisplayName"]?.GetValue<string>() == "Ada", json);
        var nested = new DynamicModel { Value = new NamingCaseModel { DisplayName = "Ada" } };
        string nestedJson = JsonSerializer.Serialize(nested);
        var nestedCopy = JsonSerializer.Deserialize<DynamicModel>(nestedJson);
        DemoCheck.Expect("object.property", nestedCopy?.Value is JsonElement entry &&
            entry.GetProperty("DisplayName").GetString() == "Ada", nestedJson);
        object inherited = CreateDerived();
        string inheritedJson = JsonSerializer.Serialize(inherited);
        DemoCheck.Expect("object.derived", inheritedJson == "{\"Child\":2,\"Name\":\"Ada\"}", inheritedJson);
        Extension();
    }

    private static DynamicBase CreateDerived() => new DynamicDerived { Name = "Ada", Child = 2 };

    private static void Extension()
    {
        const string json = "{\"Name\":\"Ada\",\"extra\":42,\"empty\":null}";
        var value = JsonSerializer.Deserialize<DynamicExtension>(json);
        string encoded = JsonSerializer.Serialize(value);
        DemoCheck.Expect("object.extension", value?.Extra?["extra"] is JsonElement number &&
            number.GetInt32() == 42 && value.Extra["empty"] is null && encoded == json, encoded);
    }
}

public sealed class DynamicModel
{
    public object? Value { get; set; }
}

public sealed class DynamicExtension
{
    public string Name { get; set; } = "";
    [JsonExtensionData]
    public Dictionary<string, object?>? Extra { get; set; }
}

public class DynamicBase
{
    public string Name { get; set; } = "";
}

public sealed class DynamicDerived : DynamicBase
{
    public int Child { get; set; }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

internal static class DemoExtension
{
    internal static void Run()
    {
        const string expected = "{\"Name\":\"Ada\",\"extra\":42,\"enabled\":true}";
        DemoExtensionModel value = JsonSerializer.Deserialize<DemoExtensionModel>(expected)
            ?? throw new InvalidOperationException("Missing extension model.");
        bool decoded = value.Name == "Ada" && value.Extra?.Count == 2
            && value.Extra["extra"].GetInt32() == 42 && value.Extra["enabled"].GetBoolean();
        DemoCheck.Expect("extension.decode", decoded, $"name={value.Name}, extra={value.Extra?.Count}");
        string json = JsonSerializer.Serialize(value);
        DemoCheck.Expect("extension.encode", json == expected, json);
        NodeExtension();
        const string privateJson = "{\"Name\":\"Ada\",\"extra\":42}";
        var privateExtension = JsonSerializer.Deserialize<PrivateExtensionModel>(privateJson);
        string privateEncoded = JsonSerializer.Serialize(privateExtension);
        DemoCheck.Expect("extension.private", privateExtension?.ExtraValue() == 42 && privateEncoded == privateJson,
            privateEncoded);
        var fieldExtension = JsonSerializer.Deserialize<FieldExtensionModel>(privateJson);
        string fieldEncoded = JsonSerializer.Serialize(fieldExtension);
        DemoCheck.Expect("extension.private-field", fieldExtension?.ExtraValue() == 42 && fieldEncoded == privateJson,
            fieldEncoded);
        var fieldOptions = new JsonSerializerOptions { IncludeFields = true };
        var optionalField = JsonSerializer.Deserialize<OptionalFieldExtensionModel>(privateJson, fieldOptions);
        string optionalJson = JsonSerializer.Serialize(optionalField, fieldOptions);
        var ignoredField = JsonSerializer.Deserialize<OptionalFieldExtensionModel>(privateJson);
        DemoCheck.Expect("extension.field-option", optionalField?.Extra?["extra"].GetInt32() == 42 &&
            optionalJson == privateJson && ignoredField?.Extra is null, optionalJson);
        var nodeField = JsonSerializer.Deserialize<NodeFieldExtensionModel>(privateJson);
        DemoCheck.Expect("extension.node-field", nodeField?.HasExtra() == true, nodeField?.HasExtra() == true ? "42" : "null");
    }

    private static void NodeExtension()
    {
        const string expected = "{\"Name\":\"Ada\",\"extra\":42,\"empty\":null}";
        var value = JsonSerializer.Deserialize<JsonObjectExtension>(expected);
        JsonNode? node = value?.Extra?["extra"];
        int? number = node?.GetValue<int>();
        DemoCheck.Expect("extension.node-decode", number == 42 && value?.Extra?.ContainsKey("empty") == true,
            $"extra={number}");
    }
}

public sealed class NodeFieldExtensionModel
{
    public string Name { get; set; } = "";
    [JsonInclude, JsonExtensionData]
    private JsonObject? extra = null;

    internal bool HasExtra() => extra?["extra"]?.ToJsonString() == "42";
}

public sealed class OptionalFieldExtensionModel
{
    public string Name { get; set; } = "";
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra = null;
}

public sealed class FieldExtensionModel
{
    public string Name { get; set; } = "";
    [JsonInclude, JsonExtensionData]
    private Dictionary<string, JsonElement>? extra = null;

    internal int ExtraValue() => extra?["extra"].GetInt32() ?? 0;
}

public sealed class PrivateExtensionModel
{
    public string Name { get; set; } = "";
    [JsonInclude, JsonExtensionData]
    private Dictionary<string, JsonElement>? Extra { get; set; }

    internal int ExtraValue() => Extra?["extra"].GetInt32() ?? 0;
}

public sealed class JsonObjectExtension
{
    public string Name { get; set; } = "";
    [JsonExtensionData]
    public JsonObject? Extra { get; set; }
}

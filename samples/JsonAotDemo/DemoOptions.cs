using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;

internal static class DemoOptions
{
    internal static void Run(Message message)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        string json = JsonSerializer.Serialize(message, options);
        using JsonDocument document = JsonDocument.Parse(json);
        DemoCheck.Expect("options.encode", json.Contains('\n')
            && document.RootElement.GetProperty("number").GetInt32() == 7, json);
        Message copy = JsonSerializer.Deserialize<Message>(json, options)
            ?? throw new InvalidOperationException("Deserialization returned null.");
        DemoCheck.Expect("options.decode", copy.Name == "Ada" && copy.Number == 7,
            $"name={copy.Name}, number={copy.Number}");
        var converted = new JsonSerializerOptions();
        converted.Converters.Add(new DemoDetailConverter());
        string convertedJson = JsonSerializer.Serialize(message, converted);
        using JsonDocument convertedDocument = JsonDocument.Parse(convertedJson);
        DemoCheck.Expect("converter.encode", convertedDocument.RootElement.GetProperty("Details").GetBoolean(),
            convertedJson);
        Message result = JsonSerializer.Deserialize<Message>(convertedJson, converted)
            ?? throw new InvalidOperationException("Deserialization returned null.");
        DemoCheck.Expect("converter.decode", result.Details is { Enabled: true }, "enabled=true");
        ConverterOptions();
        CaseInsensitive();
        SnakeCase();
        ReaderOptions();
        IgnoreDefaults();
        DictionaryKeys();
        WriteDepth();
        EncoderAndBuffer();
        CustomNaming();
        NameCollisions();
        PassedOptions();
    }

    private static void PassedOptions()
    {
        var options = CreateOptions();
        string json = EncodeWithOptions(new NamingCaseModel { DisplayName = "Ada" }, options);
        var value = DecodeWithOptions(json, options);
        DemoCheck.Expect("options.passed-between-methods", value?.DisplayName == "Ada", json);
    }

    private static JsonSerializerOptions CreateOptions() => new() { PropertyNameCaseInsensitive = true };

    private static string EncodeWithOptions(NamingCaseModel value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(value, options);

    private static NamingCaseModel? DecodeWithOptions(string json, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<NamingCaseModel>(json, options);

    private static void NameCollisions()
    {
        string json = JsonSerializer.Serialize(new CaseCollisionModel { Name = 1, name = 2 });
        DemoCheck.Expect("options.case-sensitive-names", json == "{\"Name\":1,\"name\":2}", json);
        var insensitive = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        bool rejected = false;
        try { _ = JsonSerializer.Serialize(new CaseCollisionModel(), insensitive); }
        catch (InvalidOperationException) { rejected = true; }
        DemoCheck.Expect("options.case-name-collision", rejected, rejected ? "rejected" : "accepted");
        CountingNamingPolicy.Calls = 0;
        var options = new JsonSerializerOptions { PropertyNamingPolicy = new CountingNamingPolicy() };
        string named = JsonSerializer.Serialize(new NamingCaseModel { DisplayName = "Ada" }, options);
        var copy = JsonSerializer.Deserialize<NamingCaseModel>(named, options);
        DemoCheck.Expect("options.naming-cache", copy?.DisplayName == "Ada" && CountingNamingPolicy.Calls == 1,
            $"{named} calls={CountingNamingPolicy.Calls}");
    }

    private static void CustomNaming()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = new DemoPrefixPolicy(),
            DictionaryKeyPolicy = new DemoPrefixPolicy() };
        string json = JsonSerializer.Serialize(new NamingCaseModel { DisplayName = "Ada" }, options);
        var copy = JsonSerializer.Deserialize<NamingCaseModel>(json, options);
        var map = new Dictionary<string, int> { ["Name"] = 1 };
        string mapJson = JsonSerializer.Serialize(map, options);
        var mapCopy = JsonSerializer.Deserialize<Dictionary<string, int>>(mapJson, options);
        DemoCheck.Expect("options.custom-naming", json == "{\"x_DisplayName\":\"Ada\"}" &&
            copy?.DisplayName == "Ada" && mapCopy?["x_Name"] == 1, json + "|" + mapJson);
    }

    private static void EncoderAndBuffer()
    {
        var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultBufferSize = 1 };
        var value = new NamingCaseModel { DisplayName = "<Ганна>" };
        string json = JsonSerializer.Serialize(value, options);
        var copy = JsonSerializer.Deserialize<NamingCaseModel>(json, options);
        DemoCheck.Expect("options.encoder-buffer", json == "{\"DisplayName\":\"<Ганна>\"}" &&
            copy?.DisplayName == "<Ганна>", json);
    }

    private static void ConverterOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new DemoDetailConverter());
        string result = JsonSerializer.Serialize(new Detail { Enabled = false }, options);
        DemoCheck.Expect("converter.options", result == "true", result);
        string defaultJson = JsonSerializer.Serialize(7, JsonSerializerOptions.Default);
        DemoCheck.Expect("options.default", defaultJson == "7", defaultJson);
    }

    private static void CaseInsensitive()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var value = JsonSerializer.Deserialize<DemoExtensionModel>("{\"NAME\":\"Ada\",\"extra\":42}", options);
        DemoCheck.Expect("options.case-insensitive", value?.Name == "Ada" && value.Extra?.Count == 1 &&
            value.Extra["extra"].GetInt32() == 42, value?.Name ?? "null");
    }

    private static void SnakeCase()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        string json = JsonSerializer.Serialize(new NamingCaseModel { DisplayName = "Ada" }, options);
        DemoCheck.Expect("options.snake-case.encode", json == "{\"display_name\":\"Ada\"}", json);
        var value = JsonSerializer.Deserialize<NamingCaseModel>(json, options);
        DemoCheck.Expect("options.snake-case.decode", value?.DisplayName == "Ada", value?.DisplayName ?? "null");
    }

    private static void ReaderOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 8
        };
        var value = JsonSerializer.Deserialize<NamingCaseModel>("{/* comment */\"DisplayName\":\"Ada\",}", options);
        DemoCheck.Expect("options.comments-trailing-comma", value?.DisplayName == "Ada", value?.DisplayName ?? "null");
        var shallow = new JsonSerializerOptions { MaxDepth = 1 };
        try
        {
            JsonSerializer.Deserialize<Message>("{\"display_name\":\"Ada\",\"Details\":{\"Enabled\":true}}", shallow);
            DemoCheck.Expect("options.max-depth", false, "accepted nested object");
        }
        catch (JsonException)
        {
            DemoCheck.Expect("options.max-depth", true, "rejected");
        }
    }

    private static void IgnoreDefaults()
    {
        var options = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };
        string json = JsonSerializer.Serialize(new IgnoreDefaultModel(), options);
        DemoCheck.Expect("options.ignore-default", json == "{\"Always\":null}", json);
    }

    private static void DictionaryKeys()
    {
        var options = new JsonSerializerOptions { DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower };
        var value = new Dictionary<string, int> { ["MyKey"] = 7 };
        string json = JsonSerializer.Serialize(value, options);
        DemoCheck.Expect("options.dictionary-key-policy", json == "{\"my_key\":7}", json);
        var result = JsonSerializer.Deserialize<Dictionary<string, int>>(json, options);
        DemoCheck.Expect("options.dictionary-key-policy.decode", result is not null && result["my_key"] == 7, json);
    }

    private static void WriteDepth()
    {
        var options = new JsonSerializerOptions { MaxDepth = 1 };
        var value = new Message { Name = "Ada", Details = new Detail { Enabled = true } };
        try
        {
            JsonSerializer.Serialize(value, options);
            DemoCheck.Expect("options.write-max-depth", false, "accepted nested object");
        }
        catch (JsonException)
        {
            DemoCheck.Expect("options.write-max-depth", true, "rejected");
        }
    }
}

public sealed class DemoPrefixPolicy : JsonNamingPolicy
{
    public override string ConvertName(string name) => "x_" + name;
}

public sealed class CountingNamingPolicy : JsonNamingPolicy
{
    public static int Calls;
    public override string ConvertName(string name) { Calls++; return name.ToLowerInvariant(); }
}

public sealed class CaseCollisionModel
{
    public int Name { get; set; }
    public int name { get; set; }
}

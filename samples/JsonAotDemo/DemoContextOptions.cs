using System.Text.Json;
using System.Text.Json.Serialization;

internal static class DemoContextOptions
{
    internal static void Run()
    {
        const string input = "/*comment*/{\"FIRST-NAME\":\"Ada\",\"VALUE\":\"7\",\"tags\":[2],}";
        var value = JsonSerializer.Deserialize(input, MatrixJsonContext.Default.ContextOptionsModel)
            ?? throw new InvalidOperationException("Missing JSON value.");
        bool valid = value is { FirstName: "Ada", Value: 7 } && value.Tags.SequenceEqual([1, 2]);
        string json = JsonSerializer.Serialize(value, MatrixJsonContext.Default.ContextOptionsModel);
        DemoCheck.Expect("context.options-matrix", valid && json.Contains("\"first-name\":\"Ada\"") &&
            json.Contains("\"first_key\":3") && !json.Contains("\"optional\""), json);
        bool duplicate = false;
        try { _ = JsonSerializer.Deserialize("{\"value\":1,\"value\":2}", MatrixJsonContext.Default.ContextOptionsModel); }
        catch (JsonException) { duplicate = true; }
        bool unknown = false;
        try { _ = JsonSerializer.Deserialize("{\"unknown\":1}", MatrixJsonContext.Default.ContextOptionsModel); }
        catch (JsonException) { unknown = true; }
        DemoCheck.Expect("context.options-errors", duplicate && unknown, duplicate && unknown ? "rejected" : "accepted");
        string indented = JsonSerializer.Serialize(new FastPayload { Name = "Ada" },
            IndentedJsonContext.Default.FastPayload);
        DemoCheck.Expect("context.indentation-options", indented == "{\n\t\"Name\": \"Ada\"\n}",
            indented.Replace("\n", "\\n"));
        var cycle = new CycleModel();
        cycle.Next = cycle;
        string cycleJson = JsonSerializer.Serialize(cycle, IgnoreCyclesJsonContext.Default.CycleModel);
        DemoCheck.Expect("context.reference-handler", cycleJson == "{\"Next\":null}", cycleJson);
    }
}

public sealed class ContextOptionsModel
{
    public string FirstName { get; set; } = "";
    public int Value { get; set; }
    public string? Optional { get; set; }
    public List<int> Tags { get; set; } = [1];
    public Dictionary<string, int> Map { get; set; } = new() { ["FirstKey"] = 3 };
}

[JsonSourceGenerationOptions(
    AllowDuplicateProperties = false,
    AllowOutOfOrderMetadataProperties = true,
    AllowTrailingCommas = true,
    DefaultBufferSize = 128,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    DictionaryKeyPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    IgnoreReadOnlyFields = true,
    IgnoreReadOnlyProperties = true,
    IncludeFields = true,
    MaxDepth = 32,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.KebabCaseLower,
    ReadCommentHandling = JsonCommentHandling.Skip,
    ReferenceHandler = JsonKnownReferenceHandler.Unspecified,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    UnknownTypeHandling = JsonUnknownTypeHandling.JsonElement,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ContextOptionsModel))]
internal partial class MatrixJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(WriteIndented = true, IndentCharacter = '\t', IndentSize = 1, NewLine = "\n")]
[JsonSerializable(typeof(FastPayload))]
internal partial class IndentedJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(ReferenceHandler = JsonKnownReferenceHandler.IgnoreCycles)]
[JsonSerializable(typeof(CycleModel))]
internal partial class IgnoreCyclesJsonContext : JsonSerializerContext;

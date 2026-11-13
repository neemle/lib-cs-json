using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class DemoReaderWriterCases
{
    internal static void Run()
    {
        ReaderOptions();
        ReaderSequence();
        ReaderIncremental();
        WriterRawValue();
        MutableDom();
        UnicodeStrings();
    }

    private static void ReaderOptions()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("/*note*/{\"items\":[1,2,],}");
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        int sum = document.RootElement.GetProperty("items").EnumerateArray().Sum(item => item.GetInt32());
        DemoCheck.Expect("reader.comments-trailing-commas", sum == 3, $"sum={sum}");
    }

    private static void ReaderSequence()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("[1,2,3]");
        var sequence = new ReadOnlySequence<byte>(bytes);
        var reader = new Utf8JsonReader(sequence);
        int total = 0;
        while (reader.Read()) if (reader.TokenType == JsonTokenType.Number) total += reader.GetInt32();
        DemoCheck.Expect("reader.read-only-sequence", total == 6 && reader.BytesConsumed == bytes.Length,
            $"sum={total}");
    }

    private static void WriterRawValue()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("value");
            writer.WriteRawValue("[1,2]");
            writer.WriteEndObject();
        }
        string json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        DemoCheck.Expect("writer.buffer-raw-value", json == "{\"value\":[1,2]}", json);
    }

    private static void ReaderIncremental()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("[1,2]");
        var first = new Utf8JsonReader(bytes.AsSpan(0, 3), false, default);
        int sum = 0;
        while (first.Read()) if (first.TokenType == JsonTokenType.Number) sum += first.GetInt32();
        int consumed = checked((int)first.BytesConsumed);
        var second = new Utf8JsonReader(bytes.AsSpan(consumed), true, first.CurrentState);
        while (second.Read()) if (second.TokenType == JsonTokenType.Number) sum += second.GetInt32();
        DemoCheck.Expect("reader.incremental-state", sum == 3, $"sum={sum}");
    }

    private static void MutableDom()
    {
        var node = new JsonObject
        {
            ["name"] = "Ada",
            ["items"] = new JsonArray(1, 2),
            ["empty"] = null
        };
        node["items"]!.AsArray().Add(3);
        JsonNode copy = node.DeepClone();
        string json = node.ToJsonString();
        bool valid = JsonNode.DeepEquals(node, copy) && copy["items"]!.AsArray().Count == 3 &&
            json == "{\"name\":\"Ada\",\"items\":[1,2,3],\"empty\":null}";
        DemoCheck.Expect("dom.object-array-clone", valid, json);
        JsonNode? insensitive = JsonNode.Parse("{\"Name\":\"Ada\"}",
            new JsonNodeOptions { PropertyNameCaseInsensitive = true });
        DemoCheck.Expect("dom.case-insensitive", insensitive?["name"]?.GetValue<string>() == "Ada", "Ada");
    }

    private static void UnicodeStrings()
    {
        const string value = "Ганна🙂\n\t\\\"";
        string json = JsonSerializer.Serialize(value);
        string? copy = JsonSerializer.Deserialize<string>(json);
        DemoCheck.Expect("encoding.unicode-control", copy == value && json.Contains("\\n") &&
            json.Contains("\\t") && !json.Contains('\n'), json);
    }
}

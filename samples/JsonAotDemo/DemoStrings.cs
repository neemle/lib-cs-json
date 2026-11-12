using System.Text.Json;

internal static class DemoStrings
{
    internal static string Run(Message message)
    {
        const string expected = "{\"display_name\":\"Ada\",\"Number\":7,"
            + "\"Tags\":[\"one\",\"two\"],\"Details\":{\"Enabled\":true}}";
        string json = JsonSerializer.Serialize(message);
        DemoCheck.Expect("encode.string", json == expected, json);
        Message value = JsonSerializer.Deserialize<Message>(json)
            ?? throw new InvalidOperationException("Deserialization returned null.");
        DemoCheck.Expect("decode.string", value.Name == "Ada" && value.Number == 7,
            $"name={value.Name}, number={value.Number}");
        DemoCheck.Expect("decode.nested", value.Tags.Count == 2 && value.Tags[1] == "two"
            && value.Details is { Enabled: true }, $"tags={value.Tags.Count}");
        DemoCheck.Expect("attribute.custom-name", json.Contains("\"display_name\"", StringComparison.Ordinal),
            "display_name");
        DemoCheck.Expect("attribute.ignore-default", !json.Contains("\"Optional\"", StringComparison.Ordinal),
            "default omitted");
        DemoCheck.Expect("attribute.ignore", !json.Contains("\"Ignored\"", StringComparison.Ordinal),
            "ignored omitted");
        Message parsed = JsonSerializer.Deserialize<Message>(
            ("{\"display_name\":\"Ганна\",\"Number\":\"8\",\"Tags\":[],\"Details\":" +
            "null}"))
            ?? throw new InvalidOperationException("Deserialization returned null.");
        DemoCheck.Expect("decode.attributes", parsed.Name == "Ганна" && parsed.Number == 8
            && parsed.Details is null, $"name={parsed.Name}, number={parsed.Number}, details=null");
        CheckTypeOperations(message, json, expected);
        return json;
    }

    private static void CheckTypeOperations(Message message, string json, string expected)
    {
        string typeJson = JsonSerializer.Serialize((object)message, typeof(Message));
        DemoCheck.Expect("encode.type", typeJson == expected, typeJson);
        object? typeValue = JsonSerializer.Deserialize(json, typeof(Message));
        DemoCheck.Expect("decode.type", typeValue is Message { Name: "Ada", Number: 7 },
            typeValue is Message typed ? $"name={typed.Name}, number={typed.Number}" : "unexpected type");
    }
}

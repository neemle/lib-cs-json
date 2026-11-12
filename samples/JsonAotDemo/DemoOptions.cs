using System.Text.Json;

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
    }
}

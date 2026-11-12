using System.Text.Json;
using System.Text.Json.Serialization;

internal static class DemoNamedEnum
{
    internal static void Run()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter<JsonValueKind>());
        string json = JsonSerializer.Serialize(JsonValueKind.True, options);
        DemoCheck.Expect("enum.encode-name", json == "\"True\"", json);
        JsonValueKind value = JsonSerializer.Deserialize<JsonValueKind>(json, options);
        DemoCheck.Expect("enum.decode-name", value == JsonValueKind.True, $"value={(int)value}");
    }
}

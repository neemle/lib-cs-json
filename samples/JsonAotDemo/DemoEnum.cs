using System.Text.Json;
using System.Text.Json.Serialization;

internal static class DemoEnum
{
    internal static void Run()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter<JsonValueKind>());
        JsonValueKind value = JsonSerializer.Deserialize<JsonValueKind>("\"255\"", options);
        DemoCheck.Expect("enum.decode-numeric-string", (int)value == byte.MaxValue, $"value={(int)value}");
        string encoded = JsonSerializer.Serialize(value, options);
        DemoCheck.Expect("enum.encode-unknown-value", encoded == "255", encoded);
        bool rejected = false;
        try { _ = JsonSerializer.Deserialize<JsonValueKind>("\"256\"", options); }
        catch (JsonException) { rejected = true; }
        DemoCheck.Expect("enum.decode-overflow", rejected, rejected ? "rejected" : "accepted");
    }
}

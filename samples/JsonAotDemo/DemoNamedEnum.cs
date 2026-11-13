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
        var conflicting = new JsonSerializerOptions();
        conflicting.Converters.Add(new JsonStringEnumConverter<DemoCaseEnum>());
        var caseValue = JsonSerializer.Deserialize<DemoCaseEnum>("\"first\"", conflicting);
        DemoCheck.Expect("enum.case-conflict", caseValue == DemoCaseEnum.first, $"value={(int)caseValue}");
    }
}

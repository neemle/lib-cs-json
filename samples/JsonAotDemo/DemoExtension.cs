using System.Text.Json;

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
    }
}

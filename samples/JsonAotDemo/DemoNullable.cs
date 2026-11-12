using System.Text.Json;

internal static class DemoNullable
{
    internal static void Run()
    {
        int? value = 42;
        string json = JsonSerializer.Serialize(value);
        DemoCheck.Expect("nullable.encode-value", json == "42", json);
        int? decoded = JsonSerializer.Deserialize<int?>(json);
        DemoCheck.Expect("nullable.decode-value", decoded == 42, $"value={decoded}");
        string nullJson = JsonSerializer.Serialize<int?>(null);
        DemoCheck.Expect("nullable.encode-null", nullJson == "null", nullJson);
        int? nullValue = JsonSerializer.Deserialize<int?>(nullJson);
        DemoCheck.Expect("nullable.decode-null", nullValue is null, nullValue is null ? "null" : "non-null");
    }
}

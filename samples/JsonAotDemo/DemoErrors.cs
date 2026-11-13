using System.Text.Json;

internal static class DemoErrors
{
    internal static void Run()
    {
        bool missing = false;
        try { _ = JsonSerializer.Deserialize<Message>("{\"Number\":1}"); }
        catch (JsonException) { missing = true; }
        DemoCheck.Expect("decode.required", missing, missing ? "rejected" : "accepted");
        bool invalid = false;
        try { _ = JsonSerializer.Deserialize<Message>("{"); }
        catch (JsonException) { invalid = true; }
        DemoCheck.Expect("decode.invalid", invalid, invalid ? "rejected" : "accepted");
        Reject("decode.invalid-string", () => JsonSerializer.Deserialize<string>("42"));
        Reject("decode.invalid-boolean", () => JsonSerializer.Deserialize<bool>("42"));
        Reject("decode.invalid-base64", () => JsonSerializer.Deserialize<byte[]>("\"!!!\""));
        Reject("decode.invalid-array", () => JsonSerializer.Deserialize<int[]>("true"));
        Reject("decode.invalid-list", () => JsonSerializer.Deserialize<List<int>>("true"));
        Reject("decode.invalid-map", () => JsonSerializer.Deserialize<Dictionary<string, int>>("[]"));
        Cycle();
    }

    private static void Reject(string name, Action action)
    {
        try
        {
            action();
            DemoCheck.Expect(name, false, "accepted invalid value");
        }
        catch (JsonException error)
        {
            DemoCheck.Expect(name, error.Path == "$", error.Path ?? "null");
        }
    }

    private static void Cycle()
    {
        var value = new CycleModel();
        value.Next = value;
        try
        {
            JsonSerializer.Serialize(value);
            DemoCheck.Expect("encode.cycle", false, "accepted cyclic value");
        }
        catch (JsonException error)
        {
            DemoCheck.Expect("encode.cycle", error.Path?.StartsWith('$') == true, "rejected");
        }
    }
}

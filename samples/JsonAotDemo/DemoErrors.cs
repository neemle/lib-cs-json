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
    }
}

using System.Text.Json;

internal static class DemoDuplicateOptions
{
    internal static void Run()
    {
        var options = new JsonSerializerOptions
            { AllowDuplicateProperties = false, PropertyNameCaseInsensitive = true };
        bool rejected = false;
        try { _ = JsonSerializer.Deserialize<StrictModel>("{\"Known\":1,\"KNOWN\":2}", options); }
        catch (JsonException) { rejected = true; }
        DemoCheck.Expect("options.duplicate-case", rejected, rejected ? "rejected" : "accepted");
        var unknown = JsonSerializer.Deserialize<SkipUnmappedModel>("{\"Other\":1,\"Other\":2}", options);
        DemoCheck.Expect("options.duplicate-unknown", unknown is not null, unknown is null ? "null" : "accepted");
        try { _ = JsonSerializer.Deserialize<Dictionary<string, int>>("{\"x\":1,\"x\":2}", options); rejected = false; }
        catch (JsonException) { rejected = true; }
        DemoCheck.Expect("options.duplicate-map", rejected, rejected ? "rejected" : "accepted");
        try { _ = JsonSerializer.Deserialize<JsonElement>("{\"x\":1,\"x\":2}", options); rejected = false; }
        catch (JsonException) { rejected = true; }
        DemoCheck.Expect("options.duplicate-element", rejected, rejected ? "rejected" : "accepted");
        try { _ = JsonSerializer.Deserialize<DemoExtensionModel>("{\"x\":1,\"x\":2}", options); rejected = false; }
        catch (JsonException) { rejected = true; }
        DemoCheck.Expect("options.duplicate-extension", rejected, rejected ? "rejected" : "accepted");
        var distinct = JsonSerializer.Deserialize<DemoExtensionModel>("{\"x\":1,\"X\":2}", options);
        DemoCheck.Expect("options.extension-key-case", distinct?.Extra?.Count == 2, $"count={distinct?.Extra?.Count}");
    }
}

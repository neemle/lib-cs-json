using System.Text.Json;

internal static class DemoNullableOptions
{
    internal static void Run()
    {
        var options = new JsonSerializerOptions { RespectNullableAnnotations = true };
        var value = JsonSerializer.Deserialize<NullableContractModel>("{\"Note\":null,\"AllowedNull\":null}", options);
        DemoCheck.Expect("options.nullable-allowed", value is { Note: null, AllowedNull: null }, "accepted");
        bool rejected = false;
        try { _ = JsonSerializer.Deserialize<NullableContractModel>("{\"Name\":null}", options); }
        catch (JsonException) { rejected = true; }
        DemoCheck.Expect("options.nullable-read", rejected, rejected ? "rejected" : "accepted");
        try { _ = JsonSerializer.Deserialize<NullableContractModel>("{\"DeniedNull\":null}", options); rejected = false; }
        catch (JsonException) { rejected = true; }
        DemoCheck.Expect("options.nullable-disallow", rejected, rejected ? "rejected" : "accepted");
        try { _ = JsonSerializer.Serialize(new NullableContractModel { Name = null! }, options); rejected = false; }
        catch (JsonException) { rejected = true; }
        DemoCheck.Expect("options.nullable-write", rejected, rejected ? "rejected" : "accepted");
        var constructed = JsonSerializer.Deserialize<NullableConstructorModel>(
            "{\"Maybe\":null,\"Required\":\"ok\"}", options);
        DemoCheck.Expect("options.nullable-constructor-allowed", constructed?.Maybe == "fallback", "accepted");
        try
        {
            _ = JsonSerializer.Deserialize<NullableConstructorModel>(
                "{\"Maybe\":\"ok\",\"Required\":null}", options);
            rejected = false;
        }
        catch (JsonException) { rejected = true; }
        DemoCheck.Expect("options.nullable-constructor-required", rejected, rejected ? "rejected" : "accepted");
        CollectionElements(options);
    }

    private static void CollectionElements(JsonSerializerOptions options)
    {
        var values = JsonSerializer.Deserialize<List<string>>("[null]", options);
        string json = JsonSerializer.Serialize(new List<string> { null! }, options);
        DemoCheck.Expect("options.nullable-collection-elements", values is { Count: 1 } && values[0] is null &&
            json == "[null]", json);
    }
}

using System.Text.Json;

internal static class DemoWebOptions
{
    internal static void Run()
    {
        string json = JsonSerializer.Serialize(new NamingCaseModel { DisplayName = "Ada" }, JsonSerializerOptions.Web);
        var value = JsonSerializer.Deserialize<NamingCaseModel>("{\"DISPLAYNAME\":\"Ada\"}", JsonSerializerOptions.Web);
        DemoCheck.Expect("options.web-default", json == "{\"displayName\":\"Ada\"}" && value?.DisplayName == "Ada",
            json);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        int number = JsonSerializer.Deserialize<int>("\"7\"", options);
        DemoCheck.Expect("options.web-constructor", number == 7, $"value={number}");
        bool rejected = false;
        try
        {
            _ = JsonSerializer.Deserialize<ContractOrderModel>(
                "{\"First\":1,\"Extra\":2}", JsonSerializerOptions.Strict);
        }
        catch (JsonException) { rejected = true; }
        DemoCheck.Expect("options.strict-default", rejected, rejected ? "rejected" : "accepted");
        var strict = new JsonSerializerOptions(JsonSerializerDefaults.Strict);
        var good = JsonSerializer.Deserialize<StrictModel>("{\"Known\":1}", strict);
        DemoCheck.Expect("options.strict-constructor", good?.Known == 1, $"known={good?.Known}");
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;

internal static class DemoDomValues
{
    internal static void Run()
    {
        const string expected = "{\"number\":7}";
        JsonElement element = JsonSerializer.Deserialize<JsonElement>(expected);
        DemoCheck.Expect("dom.element", JsonSerializer.Serialize(element) == expected, element.GetRawText());
        using JsonDocument document = JsonSerializer.Deserialize<JsonDocument>(expected)!;
        DemoCheck.Expect("dom.document", JsonSerializer.Serialize(document) == expected,
            document.RootElement.GetRawText());
        JsonNode node = JsonSerializer.Deserialize<JsonNode>(expected)!;
        DemoCheck.Expect("dom.node", JsonSerializer.Serialize(node) == expected, node.ToJsonString());
    }
}

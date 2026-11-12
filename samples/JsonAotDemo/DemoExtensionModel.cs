using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class DemoExtensionModel
{
    public string Name { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

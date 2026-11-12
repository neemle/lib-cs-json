using System.Text.Json.Serialization;

public sealed class Message
{
    [JsonPropertyName("display_name")]
    [JsonRequired]
    public string Name { get; set; } = string.Empty;

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int Number { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Optional { get; set; }

    [JsonIgnore]
    public string Ignored { get; set; } = "unused";

    public List<string> Tags { get; set; } = [];

    public Detail? Details { get; set; }
}

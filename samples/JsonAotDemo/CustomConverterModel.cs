using System.Text.Json.Serialization;

public sealed class CustomConverterModel
{
    [JsonConverter(typeof(AttributeStringConverter))]
    public string Name { get; set; } = "";

    public string Plain { get; set; } = "";

    [JsonInclude]
    [JsonConverter(typeof(AttributeStringConverter))]
    public string Field = "";
}

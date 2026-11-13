using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<AttributedMode>))]
public enum AttributedMode
{
    First,
    Second
}

using System.Text.Json.Serialization;

[JsonConverter(typeof(BoxAttributeConverter))]
public sealed class ConvertedBox(int value)
{
    public int Value { get; } = value;
}

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(LabelKeyConverter))]
public sealed class LabelKey
{
    public string Name { get; set; } = "";
}

internal sealed class LabelKeyConverter : JsonConverter<LabelKey>
{
    public override LabelKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new() { Name = reader.GetString()! };

    public override void Write(Utf8JsonWriter writer, LabelKey value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Name);

    public override LabelKey ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) => new() { Name = reader.GetString()![1..] };

    public override void WriteAsPropertyName(Utf8JsonWriter writer, LabelKey value, JsonSerializerOptions options) =>
        writer.WritePropertyName("#" + value.Name);
}

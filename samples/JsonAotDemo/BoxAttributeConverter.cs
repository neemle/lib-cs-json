using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class BoxAttributeConverter : JsonConverter<ConvertedBox>
{
    public override ConvertedBox Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetInt32());

    public override void Write(Utf8JsonWriter writer, ConvertedBox value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.Value);
}

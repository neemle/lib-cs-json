using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class AttributeStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString()!.ToLowerInvariant();

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUpperInvariant());
}

using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class AddressConverter : JsonConverter<Uri>
{
    public override Uri Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString()!, UriKind.Absolute);

    public override void Write(Utf8JsonWriter writer, Uri value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.AbsoluteUri);
}

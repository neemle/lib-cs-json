using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class DemoUnsupportedConverter : JsonConverter<Detail>
{
    public override Detail Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException((string?)null);

    public override void Write(Utf8JsonWriter writer, Detail value, JsonSerializerOptions options)
        => throw new NotSupportedException("custom converter refused");
}

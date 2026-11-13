using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class DemoDetailConverter : JsonConverter<Detail>
{
    public override Detail Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) => new() { Enabled = reader.GetBoolean() };

    public override void Write(Utf8JsonWriter writer, Detail value, JsonSerializerOptions options)
        => writer.WriteBooleanValue(value.Enabled || options.WriteIndented);
}

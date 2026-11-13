using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class CountingStringConverter : JsonConverter<string>
{
    private int count;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString()!;

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value + ++count);
}

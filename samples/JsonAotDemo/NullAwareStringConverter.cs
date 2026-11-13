using System.Text.Json;
using System.Text.Json.Serialization;

public class NullAwareStringConverter : JsonConverter<string>
{
    public override bool HandleNull => true;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? "decoded-null" : reader.GetString()!;

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value is null ? "encoded-null" : value);
}

public sealed class NullAwareModel
{
    [JsonConverter(typeof(NullAwareStringConverter))]
    public string? Value { get; set; }
}

internal sealed class NullAwareIntConverter : JsonConverter<int?>
{
    public override bool HandleNull => true;

    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? 17 : reader.GetInt32();

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value ?? 19);
}

using System.Text.Json;
using System.Text.Json.Serialization;

public class RegisteredStringConverter : JsonConverter<string>
{
    private readonly string prefix = "registered:";

    public override bool CanConvert(Type typeToConvert) => base.CanConvert(typeToConvert);

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        this.prefix + reader.GetString();

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(this.prefix + value);
}

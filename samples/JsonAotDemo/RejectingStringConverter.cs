using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class RejectingStringConverter : JsonConverter<string>
{
    public override bool CanConvert(Type typeToConvert) => false;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new InvalidOperationException();

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        throw new InvalidOperationException();
}

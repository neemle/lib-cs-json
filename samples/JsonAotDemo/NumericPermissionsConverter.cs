using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class NumericPermissionsConverter : JsonConverter<DemoPermissions>
{
    public override DemoPermissions Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => (DemoPermissions)reader.GetInt32();

    public override void Write(Utf8JsonWriter writer, DemoPermissions value, JsonSerializerOptions options)
        => writer.WriteNumberValue((int)value);
}

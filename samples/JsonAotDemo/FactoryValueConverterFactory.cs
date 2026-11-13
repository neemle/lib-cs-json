using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class FactoryValue
{
    public int Number { get; set; }
}

public sealed class InvalidFactoryValue;

public sealed class InvalidConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(InvalidFactoryValue);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) => null!;
}

public sealed class MismatchedConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(InvalidFactoryValue);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        new FactoryValueConverter();
}

[JsonConverter(typeof(FactoryValueConverterFactory))]
public sealed class FactoryAttributeValue
{
    public int Number { get; set; }
}

public sealed class FactoryMemberModel
{
    [JsonConverter(typeof(FactoryValueConverterFactory))]
    public FactoryValue Value { get; set; } = new();

    [JsonInclude, JsonConverter(typeof(FactoryValueConverterFactory))]
    public FactoryValue Field = new();
}

public sealed class CustomFactoryAttribute : JsonConverterAttribute
{
    public override JsonConverter CreateConverter(Type typeToConvert) => typeToConvert == typeof(FactoryValue) ?
        new FactoryValueConverter() : throw new NotSupportedException();
}

public sealed class CustomAttributeModel
{
    [CustomFactory]
    public FactoryValue Value { get; set; } = new();
}

public sealed class FactoryValueConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(FactoryValue) ||
        typeToConvert == typeof(FactoryAttributeValue) || typeToConvert == typeof(FactoryKey) ||
        typeToConvert == typeof(FactoryNullValue);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) => typeToConvert switch
    {
        var type when type == typeof(FactoryValue) => new FactoryValueConverter(),
        var type when type == typeof(FactoryAttributeValue) => new FactoryAttributeValueConverter(),
        var type when type == typeof(FactoryKey) => new FactoryKeyConverter(),
        var type when type == typeof(FactoryNullValue) => new FactoryNullValueConverter(),
        _ => throw new NotSupportedException()
    };
}

public readonly record struct FactoryKey(string Value);

public sealed class FactoryNullValue
{
    public string Text { get; set; } = "";
}

public sealed class FactoryNullValueConverter : JsonConverter<FactoryNullValue>
{
    public override bool HandleNull => true;

    public override FactoryNullValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new() { Text = reader.TokenType == JsonTokenType.Null ? "null" : reader.GetString()! };

    public override void Write(Utf8JsonWriter writer, FactoryNullValue value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value is null ? "null" : value.Text);
}

public sealed class FactoryKeyConverter : JsonConverter<FactoryKey>
{
    public override FactoryKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, FactoryKey value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);

    public override FactoryKey ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) => new(reader.GetString()![4..]);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, FactoryKey value, JsonSerializerOptions options) =>
        writer.WritePropertyName("key:" + value.Value);
}

public sealed class FactoryValueConverter : JsonConverter<FactoryValue>
{
    public override FactoryValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new() { Number = int.Parse(reader.GetString()![6..], System.Globalization.CultureInfo.InvariantCulture) };

    public override void Write(Utf8JsonWriter writer, FactoryValue value, JsonSerializerOptions options) =>
        writer.WriteStringValue("value:" + value.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

public sealed class AlternateFactoryValueConverter : JsonConverter<FactoryValue>
{
    public override FactoryValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new() { Number = int.Parse(reader.GetString()![4..], System.Globalization.CultureInfo.InvariantCulture) };

    public override void Write(Utf8JsonWriter writer, FactoryValue value, JsonSerializerOptions options) =>
        writer.WriteStringValue("alt:" + value.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

public sealed class WrappedPayload
{
    public AdvancedPayload Value { get; set; } = new();
}

public sealed class WrappedPayloadConverter : JsonConverter<WrappedPayload>
{
    public override WrappedPayload Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new() { Value = JsonSerializer.Deserialize<AdvancedPayload>(ref reader, options) ?? new AdvancedPayload() };

    public override void Write(Utf8JsonWriter writer, WrappedPayload value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.Value, options);
}

public sealed class ConverterCountPayload
{
    public int Count { get; set; }
}

public sealed class ConverterCountConverter : JsonConverter<ConverterCountPayload>
{
    public override ConverterCountPayload Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new() { Count = reader.GetInt32() + options.Converters.Count };

    public override void Write(Utf8JsonWriter writer, ConverterCountPayload value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.Count + options.Converters.Count);
}

public sealed class FactoryAttributeValueConverter : JsonConverter<FactoryAttributeValue>
{
    public override FactoryAttributeValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new() { Number = int.Parse(reader.GetString()![10..], System.Globalization.CultureInfo.InvariantCulture) };

    public override void Write(Utf8JsonWriter writer, FactoryAttributeValue value, JsonSerializerOptions options) =>
        writer.WriteStringValue("attribute:" + value.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

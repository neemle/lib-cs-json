using System.Text.Json;
using System.Text.Json.Serialization;

internal static class DemoContext
{
    internal static void Run()
    {
        var value = new ContextPayload { Name = "Ada", Number = 7 };
        string json = JsonSerializer.Serialize(value, DemoJsonContext.Default.ContextPayload);
        var copy = JsonSerializer.Deserialize(json, DemoJsonContext.Default.ContextPayload);
        DemoCheck.Expect("context.typed", copy is { Name: "Ada", Number: 7 }, json);
        RunAlias(value);
        string nestedType = JsonSerializer.Serialize("Ada", DemoJsonContext.Default.String);
        string? nestedCopy = JsonSerializer.Deserialize(nestedType, DemoJsonContext.Default.String);
        DemoCheck.Expect("context.inferred-member-type", nestedCopy == "Ada", nestedType);
        string declaredNested = JsonSerializer.Serialize((object)"Ada", typeof(string), DemoJsonContext.Default);
        DemoCheck.Expect("context.declared-member-type", declaredNested == "\"Ada\"", declaredNested);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, DemoJsonContext.Default.ContextPayload);
        var fromBytes = JsonSerializer.Deserialize(bytes, DemoJsonContext.Default.ContextPayload);
        DemoCheck.Expect("context.bytes", fromBytes is { Name: "Ada", Number: 7 }, System.Text.Encoding.UTF8.GetString(bytes));
        JsonElement element = JsonSerializer.SerializeToElement(value, DemoJsonContext.Default.ContextPayload);
        var fromElement = JsonSerializer.Deserialize(element, DemoJsonContext.Default.ContextPayload);
        DemoCheck.Expect("context.element", fromElement is { Name: "Ada", Number: 7 }, element.GetRawText());
        Type selected = value.Number > 0 ? typeof(ContextPayload) : typeof(string);
        string declared = JsonSerializer.Serialize(value, selected, DemoJsonContext.Default);
        object? fromDeclared = JsonSerializer.Deserialize(declared, selected, DemoJsonContext.Default);
        DemoCheck.Expect("context.runtime-type", fromDeclared is ContextPayload { Name: "Ada", Number: 7 }, declared);
        string collection = JsonSerializer.Serialize(new List<int> { 1, 2 }, CollectionJsonContext.Default.ListInt32);
        var collectionCopy = JsonSerializer.Deserialize(collection, CollectionJsonContext.Default.ListInt32);
        DemoCheck.Expect("context.generic-type", collectionCopy is not null && collectionCopy.SequenceEqual([1, 2]), collection);
        string mapJson = JsonSerializer.Serialize(new Dictionary<string, int> { ["a"] = 1 }, NamedJsonContext.Default.CustomMap);
        var mapCopy = JsonSerializer.Deserialize(mapJson, NamedJsonContext.Default.CustomMap);
        DemoCheck.Expect("context.custom-type-name", mapCopy?["a"] == 1, mapJson);
        var configured = new ConfiguredContextPayload { DisplayName = "Ada", Count = 7, Rank = 3 };
        string configuredJson = JsonSerializer.Serialize(configured, ConfiguredJsonContext.Default.ConfiguredContextPayload);
        var configuredCopy = JsonSerializer.Deserialize("{\"DISPLAY_NAME\":\"Ada\",\"count\":\"7\",\"rank\":3}",
            ConfiguredJsonContext.Default.ConfiguredContextPayload);
        DemoCheck.Expect("context.options", configuredJson == "{\"display_name\":\"Ada\",\"count\":7,\"rank\":3}" &&
            configuredCopy is { DisplayName: "Ada", Count: 7, Rank: 3 }, configuredJson);
        string enumJson = JsonSerializer.Serialize(DemoPermissions.Read, EnumJsonContext.Default.DemoPermissions);
        var enumCopy = JsonSerializer.Deserialize(enumJson, EnumJsonContext.Default.DemoPermissions);
        DemoCheck.Expect("context.string-enums", enumJson == "\"Read\"" && enumCopy == DemoPermissions.Read, enumJson);
        string convertedJson = JsonSerializer.Serialize(value, ConverterJsonContext.Default.ContextPayload);
        var convertedCopy = JsonSerializer.Deserialize("{\"Name\":\"Ada\",\"Number\":7}",
            ConverterJsonContext.Default.ContextPayload);
        DemoCheck.Expect("context.converters", convertedJson == "{\"Name\":\"registered:Ada\",\"Number\":7}" &&
            convertedCopy?.Name == "registered:Ada", convertedJson);
        string ordered = JsonSerializer.Serialize(DemoPermissions.Read, OrderedEnumContext.Default.DemoPermissions);
        var orderedCopy = JsonSerializer.Deserialize(ordered, OrderedEnumContext.Default.DemoPermissions);
        DemoCheck.Expect("context.converter-order", ordered == "1" && orderedCopy == DemoPermissions.Read, ordered);
        string generic = JsonSerializer.Serialize(DemoPermissions.Read, GenericEnumContext.Default.DemoPermissions);
        DemoCheck.Expect("context.generic-enum-converter", generic == "\"Read\"", generic);
        string multi = JsonSerializer.Serialize((object)new List<int> { 1, 2 }, typeof(List<int>), MultiJsonContext.Default);
        object? multiCopy = JsonSerializer.Deserialize(multi, typeof(List<int>), MultiJsonContext.Default);
        DemoCheck.Expect("context.multiple-types", multiCopy is List<int> list && list.SequenceEqual([1, 2]), multi);
        RunMultiRuntime();
        string first = JsonSerializer.Serialize(value, StatefulJsonContext.Default.ContextPayload);
        string second = JsonSerializer.Serialize(value, StatefulJsonContext.Default.ContextPayload);
        DemoCheck.Expect("context.converter-state", first == "{\"Name\":\"Ada1\",\"Number\":7}" &&
            second == "{\"Name\":\"Ada2\",\"Number\":7}", first + "/" + second);
        string metadataJson = JsonSerializer.Serialize(value, MetadataJsonContext.Default.ContextPayload);
        var metadataCopy = JsonSerializer.Deserialize(metadataJson, MetadataJsonContext.Default.ContextPayload);
        DemoCheck.Expect("context.metadata-mode", metadataCopy is { Name: "Ada", Number: 7 }, metadataJson);
        string arrayJson = JsonSerializer.Serialize(new[] { "Ada", "Lin" }, ArrayJsonContext.Default.StringArray);
        var arrayCopy = JsonSerializer.Deserialize(arrayJson, ArrayJsonContext.Default.StringArray);
        DemoCheck.Expect("context.array-type", arrayCopy is not null && arrayCopy.SequenceEqual(["Ada", "Lin"]), arrayJson);
    }

    private static void RunAlias(ContextPayload value)
    {
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<ContextPayload> info = DemoJsonContext.Default.ContextPayload;
        string json = JsonSerializer.Serialize(value, info);
        var copy = JsonSerializer.Deserialize(json, info);
        DemoCheck.Expect("context.local-type-info", copy is { Name: "Ada", Number: 7 }, json);
        var configured = ConfiguredJsonContext.Default.ConfiguredContextPayload;
        string named = JsonSerializer.Serialize(new ConfiguredContextPayload { DisplayName = "Ada" }, configured);
        DemoCheck.Expect("context.local-type-info-options", named.Contains("\"display_name\""), named);
        JsonSerializerContext context = DemoJsonContext.Default;
        string declared = JsonSerializer.Serialize(value, typeof(ContextPayload), context);
        var restored = JsonSerializer.Deserialize(declared, typeof(ContextPayload), context);
        DemoCheck.Expect("context.local-context", restored is ContextPayload { Name: "Ada", Number: 7 }, declared);
    }

    private static void RunMultiRuntime()
    {
        object[] values = [new ContextPayload { Name = "Ada", Number = 7 }, new List<int> { 1, 2 }];
        Type[] types = [typeof(ContextPayload), typeof(List<int>)];
        for (int i = 0; i < values.Length; i++)
        {
            string json = JsonSerializer.Serialize(values[i], types[i], MultiJsonContext.Default);
            object? copy = JsonSerializer.Deserialize(json, types[i], MultiJsonContext.Default);
            bool valid = i == 0 ? copy is ContextPayload { Name: "Ada", Number: 7 } :
                copy is List<int> list && list.SequenceEqual([1, 2]);
            DemoCheck.Expect("context.multiple-runtime-types." + i, valid, json);
        }
    }
}

public sealed class ContextPayload
{
    public string Name { get; set; } = "";
    public int Number { get; set; }
}

[JsonSerializable(typeof(ContextPayload))]
internal partial class DemoJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(List<int>))]
internal partial class CollectionJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(Dictionary<string, int>), TypeInfoPropertyName = "CustomMap")]
internal partial class NamedJsonContext : JsonSerializerContext;

public sealed class ConfiguredContextPayload
{
    public string DisplayName { get; set; } = "";
    public int Count { get; set; }
    public string? Optional { get; set; }
    public int Rank;
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, IncludeFields = true)]
[JsonSerializable(typeof(ConfiguredContextPayload))]
internal partial class ConfiguredJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(DemoPermissions))]
internal partial class EnumJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(Converters = new[] { typeof(RegisteredStringConverter) })]
[JsonSerializable(typeof(ContextPayload))]
internal partial class ConverterJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(UseStringEnumConverter = true, Converters = new[] { typeof(NumericPermissionsConverter) })]
[JsonSerializable(typeof(DemoPermissions))]
internal partial class OrderedEnumContext : JsonSerializerContext;

[JsonSourceGenerationOptions(Converters = new[] { typeof(JsonStringEnumConverter<DemoPermissions>) })]
[JsonSerializable(typeof(DemoPermissions))]
internal partial class GenericEnumContext : JsonSerializerContext;

[JsonSerializable(typeof(ContextPayload))]
[JsonSerializable(typeof(List<int>))]
internal partial class MultiJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(Converters = new[] { typeof(CountingStringConverter) })]
[JsonSerializable(typeof(ContextPayload))]
internal partial class StatefulJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ContextPayload), GenerationMode = JsonSourceGenerationMode.Metadata)]
internal partial class MetadataJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(string[]))]
internal partial class ArrayJsonContext : JsonSerializerContext;

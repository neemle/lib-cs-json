using System.Text.Json;

internal static class DemoConverterAttributes
{
    internal static void Run()
    {
        NullHandling();
        TypeAttribute();
        OpaqueProperty();
        var value = new CustomConverterModel { Name = "Ada", Plain = "Lin", Field = "Grace" };
        string json = JsonSerializer.Serialize(value);
        var copy = JsonSerializer.Deserialize<CustomConverterModel>(json);
        DemoCheck.Expect("attribute.custom-converter", json == "{\"Name\":\"ADA\",\"Plain\":\"Lin\",\"Field\":\"GRACE\"}" &&
            copy is { Name: "ada", Plain: "Lin", Field: "grace" }, json);
        var options = new JsonSerializerOptions();
        options.Converters.Add(new RegisteredStringConverter());
        string priority = JsonSerializer.Serialize(value, options);
        var decoded = JsonSerializer.Deserialize<CustomConverterModel>(priority, options);
        DemoCheck.Expect("attribute.converter-priority", priority ==
            "{\"Name\":\"ADA\",\"Plain\":\"registered:Lin\",\"Field\":\"GRACE\"}" &&
            decoded is { Name: "ada", Plain: "registered:registered:Lin", Field: "grace" }, priority);
    }

    private static void NullHandling()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new NullAwareStringConverter());
        string? value = null;
        string encoded = JsonSerializer.Serialize(value, options);
        string? decoded = JsonSerializer.Deserialize<string?>("null", options);
        DemoCheck.Expect("converter.handle-null", encoded == "\"encoded-null\"" && decoded == "decoded-null", encoded);
        var model = new NullAwareModel();
        string json = JsonSerializer.Serialize(model);
        var copy = JsonSerializer.Deserialize<NullAwareModel>("{\"Value\":null}");
        DemoCheck.Expect("converter.attribute-handle-null", json == "{\"Value\":\"encoded-null\"}" &&
            copy?.Value == "decoded-null", json);
        var numberOptions = new JsonSerializerOptions();
        var numberConverter = new NullAwareIntConverter();
        numberOptions.Converters.Add(numberConverter);
        int? number = null;
        string numberJson = JsonSerializer.Serialize(number, numberOptions);
        int? numberCopy = JsonSerializer.Deserialize<int?>("null", numberOptions);
        DemoCheck.Expect("converter.nullable-handle-null", numberJson == "19" && numberCopy == 17, numberJson);
        var ordered = new JsonSerializerOptions();
        ordered.Converters.Add(new RejectingStringConverter());
        ordered.Converters.Add(new RegisteredStringConverter());
        string orderedJson = JsonSerializer.Serialize("Ada", ordered);
        DemoCheck.Expect("converter.can-convert-order", orderedJson == "\"registered:Ada\"", orderedJson);
    }
    private static void OpaqueProperty()
    {
        string json = JsonSerializer.Serialize(new AddressModel());
        var copy = JsonSerializer.Deserialize<AddressModel>(json);
        DemoCheck.Expect("attribute.opaque-converter", json == "{\"Address\":\"https://example.com/\"}" &&
            copy?.Address.AbsoluteUri == "https://example.com/", json);
    }

    private static void TypeAttribute()
    {
        var value = new ConvertedBox(7);
        string direct = JsonSerializer.Serialize(value);
        var decoded = JsonSerializer.Deserialize<ConvertedBox>(direct);
        DemoCheck.Expect("attribute.type-converter", direct == "7" && decoded?.Value == 7, direct);
        var options = new JsonSerializerOptions();
        options.Converters.Add(new BoxRegisteredConverter());
        string priority = JsonSerializer.Serialize(value, options);
        var copy = JsonSerializer.Deserialize<ConvertedBox>(priority, options);
        DemoCheck.Expect("attribute.type-converter-priority", priority == "70" && copy?.Value == 7, priority);
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

internal static class DemoContractOptions
{
    internal static void Run()
    {
        string ordered = JsonSerializer.Serialize(new ContractOrderModel());
        DemoCheck.Expect("attribute.property-order", ordered == "{\"First\":1,\"Middle\":2,\"Last\":3}", ordered);
        bool rejected = false;
        try { _ = JsonSerializer.Deserialize<StrictModel>("{\"Known\":1,\"Unknown\":2}"); }
        catch (JsonException) { rejected = true; }
        DemoCheck.Expect("attribute.unmapped-disallow", rejected, rejected ? "rejected" : "accepted");
        var options = new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        var value = JsonSerializer.Deserialize<SkipUnmappedModel>("{\"Known\":1,\"Unknown\":2}", options);
        DemoCheck.Expect("attribute.unmapped-override", value?.Known == 1, $"known={value?.Known}");
        try { _ = JsonSerializer.Deserialize<ContractOrderModel>("{\"Unknown\":2}", options); rejected = false; }
        catch (JsonException) { rejected = true; }
        DemoCheck.Expect("options.unmapped-disallow", rejected, rejected ? "rejected" : "accepted");
        var format = new JsonSerializerOptions
        {
            WriteIndented = true,
            IndentCharacter = '\t',
            IndentSize = 1,
            NewLine = "\r\n"
        };
        string formatted = JsonSerializer.Serialize(new StrictModel { Known = 1 }, format);
        DemoCheck.Expect("options.indentation", formatted == "{\r\n\t\"Known\": 1\r\n}", formatted);
        var direction = JsonSerializer.Deserialize<DirectionalIgnoreModel>(
            "{\"ReadOnlyInJson\":3,\"WriteOnlyInJson\":4}");
        string directionJson = JsonSerializer.Serialize(direction);
        DemoCheck.Expect("attribute.directional-ignore", direction?.ReadOnlyInJson == 3 &&
            direction.WriteOnlyInJson == 7 && directionJson == "{\"WriteOnlyInJson\":7}", directionJson);
        var readonlyOptions = new JsonSerializerOptions { IgnoreReadOnlyProperties = true };
        string readonlyJson = JsonSerializer.Serialize(new ReadOnlyOptionModel(), readonlyOptions);
        DemoCheck.Expect("options.ignore-readonly", readonlyJson == "{\"Kept\":2,\"Items\":[3]}", readonlyJson);
        var numbers = JsonSerializer.Deserialize<NumberAttributeModel>(
            "{\"Long\":\"1234567890123\",\"Double\":\"1.5\",\"Decimal\":\"2.25\",\"Small\":\"-7\"}");
        string numbersJson = JsonSerializer.Serialize(numbers);
        DemoCheck.Expect("attribute.number-handling", numbers is { Long: 1234567890123, Double: 1.5,
            Decimal: 2.25m, Small: -7 }, numbersJson);
        try { _ = JsonSerializer.Deserialize<NumberAttributeModel>("{\"Double\":\" 1.5\"}"); rejected = false; }
        catch (JsonException) { rejected = true; }
        DemoCheck.Expect("attribute.invalid-number-string", rejected, rejected ? "rejected" : "accepted");
        Fields();
    }

    private static void Fields()
    {
        var value = new FieldOptionModel();
        string defaults = JsonSerializer.Serialize(value);
        DemoCheck.Expect("fields.default", defaults == "{\"Explicit\":7}", defaults);
        var options = new JsonSerializerOptions
        {
            IncludeFields = true,
            IgnoreReadOnlyFields = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        string json = JsonSerializer.Serialize(value, options);
        var copy = JsonSerializer.Deserialize<FieldOptionModel>("{\"Value\":8,\"Explicit\":9}", options);
        DemoCheck.Expect("fields.options", json == "{\"Value\":3,\"Kept\":5,\"Explicit\":7}" &&
            copy is { Value: 8, Explicit: 9 }, json);
        string skipped = JsonSerializer.Serialize(new OptionalUnsupportedFieldModel());
        DemoCheck.Expect("fields.unused-unsupported", skipped == "{\"Value\":4}", skipped);
    }
}

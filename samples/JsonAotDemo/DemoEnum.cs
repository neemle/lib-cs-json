using System.Text.Json;
using System.Text.Json.Serialization;

internal static class DemoEnum
{
    internal static void Run()
    {
        Flags();
        ConverterAttributes();
        DisallowIntegers();
        NamingPolicy();
        Factory();
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter<JsonValueKind>());
        JsonValueKind value = JsonSerializer.Deserialize<JsonValueKind>("\"255\"", options);
        DemoCheck.Expect("enum.decode-numeric-string", (int)value == byte.MaxValue, $"value={(int)value}");
        string encoded = JsonSerializer.Serialize(value, options);
        DemoCheck.Expect("enum.encode-unknown-value", encoded == "255", encoded);
        bool rejected = false;
        try { _ = JsonSerializer.Deserialize<JsonValueKind>("\"256\"", options); }
        catch (JsonException) { rejected = true; }
        DemoCheck.Expect("enum.decode-overflow", rejected, rejected ? "rejected" : "accepted");
    }

    private static void Factory()
    {
        var options = new JsonSerializerOptions();
        var factory = new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false);
        options.Converters.Add(factory);
        string json = JsonSerializer.Serialize(DemoPermissions.Read | DemoPermissions.Write, options);
        var copy = JsonSerializer.Deserialize<DemoPermissions>(json, options);
        string kind = JsonSerializer.Serialize(JsonValueKind.True, options);
        bool rejected = false;
        try { _ = JsonSerializer.Deserialize<DemoPermissions>("8", options); }
        catch (JsonException) { rejected = true; }
        DemoCheck.Expect("enum.factory", json == "\"read, write\"" &&
            copy == (DemoPermissions.Read | DemoPermissions.Write) && kind == "\"true\"" && rejected, json);
        var numericFirst = new JsonSerializerOptions();
        numericFirst.Converters.Add(new NumericPermissionsConverter());
        numericFirst.Converters.Add(new JsonStringEnumConverter());
        string numeric = JsonSerializer.Serialize(DemoPermissions.Read, numericFirst);
        var stringFirst = new JsonSerializerOptions();
        stringFirst.Converters.Add(new JsonStringEnumConverter());
        stringFirst.Converters.Add(new NumericPermissionsConverter());
        string text = JsonSerializer.Serialize(DemoPermissions.Read, stringFirst);
        DemoCheck.Expect("enum.factory-order", numeric == "1" && text == "\"Read\"", numeric + "/" + text);
        string attributed = JsonSerializer.Serialize(FactoryAttributedMode.Second);
        var attributedCopy = JsonSerializer.Deserialize<FactoryAttributedMode>(attributed);
        DemoCheck.Expect("enum.factory-attribute", attributed == "\"Second\"" &&
            attributedCopy == FactoryAttributedMode.Second, attributed);
    }

    private static void NamingPolicy()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter<DemoPermissions>(JsonNamingPolicy.CamelCase, false));
        string json = JsonSerializer.Serialize(DemoPermissions.Read | DemoPermissions.Write, options);
        var copy = JsonSerializer.Deserialize<DemoPermissions>(json, options);
        var original = JsonSerializer.Deserialize<DemoPermissions>("\"Read\"", options);
        DemoCheck.Expect("enum.naming-policy", json == "\"read, write\"" &&
            copy == (DemoPermissions.Read | DemoPermissions.Write) && original == DemoPermissions.Read, json);
        var map = new Dictionary<DemoPermissions, int> { [DemoPermissions.Read] = 1 };
        string mapJson = JsonSerializer.Serialize(map, options);
        var mapCopy = JsonSerializer.Deserialize<Dictionary<DemoPermissions, int>>(mapJson, options);
        DemoCheck.Expect("enum.naming-policy-key", mapCopy?.ContainsKey(DemoPermissions.Read) == true, mapJson);
        var dictionaryPolicy = new JsonSerializerOptions { DictionaryKeyPolicy = JsonNamingPolicy.CamelCase };
        string policyJson = JsonSerializer.Serialize(map, dictionaryPolicy);
        DemoCheck.Expect("enum.dictionary-key-policy", policyJson == "{\"read\":1}", policyJson);
        var combined = new JsonSerializerOptions { DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseUpper };
        combined.Converters.Add(new JsonStringEnumConverter<DemoPermissions>(JsonNamingPolicy.CamelCase));
        string combinedJson = JsonSerializer.Serialize(map, combined);
        DemoCheck.Expect("enum.combined-key-policy", combinedJson == "{\"READ\":1}", combinedJson);
        string customNameKey = JsonSerializer.Serialize(new Dictionary<DemoPermissions, int>
            { [DemoPermissions.Execute] = 1 }, combined);
        DemoCheck.Expect("enum.custom-key-policy", customNameKey == "{\"run\":1}", customNameKey);
        string flagsKey = JsonSerializer.Serialize(new Dictionary<DemoPermissions, int>
            { [DemoPermissions.Read | DemoPermissions.Execute] = 1 }, combined);
        DemoCheck.Expect("enum.flags-key-policy", flagsKey == "{\"READ, run\":1}", flagsKey);
        string unknownKey = JsonSerializer.Serialize(new Dictionary<DemoPermissions, int> { [(DemoPermissions)8] = 1 }, options);
        bool unknownRejected = false;
        try { _ = JsonSerializer.Deserialize<Dictionary<DemoPermissions, int>>(unknownKey, options); }
        catch (JsonException) { unknownRejected = true; }
        DemoCheck.Expect("enum.unknown-key", unknownRejected && unknownKey == "{\"8\":1}", unknownKey);
    }

    private static void DisallowIntegers()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter<DemoPermissions>(null, false));
        bool readRejected = false;
        bool writeRejected = false;
        try { _ = JsonSerializer.Deserialize<DemoPermissions>("8", options); }
        catch (JsonException) { readRejected = true; }
        try { _ = JsonSerializer.Serialize((DemoPermissions)8, options); }
        catch (JsonException) { writeRejected = true; }
        string valid = JsonSerializer.Serialize(DemoPermissions.Read, options);
        DemoCheck.Expect("enum.disallow-integers", readRejected && writeRejected && valid == "\"Read\"", valid);
    }

    private static void ConverterAttributes()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new NumericPermissionsConverter());
        var value = new EnumAttributeModel { Permissions = DemoPermissions.Read, Mode = AttributedMode.Second };
        string json = JsonSerializer.Serialize(value, options);
        var copy = JsonSerializer.Deserialize<EnumAttributeModel>(json, options);
        DemoCheck.Expect("attribute.enum-converter", json == "{\"Permissions\":\"Read\",\"Mode\":\"Second\"}" &&
            copy is { Permissions: DemoPermissions.Read, Mode: AttributedMode.Second }, json);
    }

    private static void Flags()
    {
        var value = DemoPermissions.Read | DemoPermissions.Write;
        string json = JsonSerializer.Serialize(value);
        DemoCheck.Expect("enum.flags.encode", json == "3", json);
        DemoCheck.Expect("enum.flags.decode", JsonSerializer.Deserialize<DemoPermissions>(json) == value, json);
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter<DemoPermissions>());
        string names = JsonSerializer.Serialize(value, options);
        DemoCheck.Expect("enum.flags.string-encode", names == "\"Read, Write\"", names);
        DemoPermissions decoded = JsonSerializer.Deserialize<DemoPermissions>(names, options);
        DemoCheck.Expect("enum.flags.string-decode", decoded == value, names);
        string all = JsonSerializer.Serialize(DemoPermissions.All, options);
        DemoCheck.Expect("enum.flags.composite", all == "\"All\"", all);
        string unknown = JsonSerializer.Serialize((DemoPermissions)8, options);
        DemoCheck.Expect("enum.flags.unknown", unknown == "8", unknown);
        var noZero = new JsonSerializerOptions();
        noZero.Converters.Add(new JsonStringEnumConverter<DemoNoZero>());
        string zero = JsonSerializer.Serialize((DemoNoZero)0, noZero);
        DemoCheck.Expect("enum.flags.unnamed-zero", zero == "0", zero);
        string custom = JsonSerializer.Serialize(DemoPermissions.Execute, options);
        DemoCheck.Expect("enum.flags.custom-name", custom == "\"run\"", custom);
        bool rejected = false;
        try { _ = JsonSerializer.Deserialize<DemoPermissions>("\"RUN\"", options); }
        catch (JsonException) { rejected = true; }
        DemoCheck.Expect("enum.flags.custom-name-case", rejected, rejected ? "rejected" : "accepted");
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FactoryAttributedMode
{
    First,
    Second
}

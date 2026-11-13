using System.Text.Json;
using System.Text.Json.Serialization;

internal static class DemoNumberOptions
{
    internal static void Run()
    {
        var options = new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString
        };
        string number = JsonSerializer.Serialize(123, options);
        int parsed = JsonSerializer.Deserialize<int>(number, options);
        DemoCheck.Expect("options.number-handling", number == "\"123\"" && parsed == 123, number);
        string arrayJson = JsonSerializer.Serialize(new[] { 1, 2 }, options);
        var array = JsonSerializer.Deserialize<int[]>(arrayJson, options);
        DemoCheck.Expect("options.number-array", arrayJson == "[\"1\",\"2\"]" &&
            array is not null && array.SequenceEqual([1, 2]), arrayJson);
        var model = new NumberModeModel { Strict = 3, Annotated = 2.5m };
        string json = JsonSerializer.Serialize(model, options);
        var copy = JsonSerializer.Deserialize<NumberModeModel>(json, options);
        DemoCheck.Expect("attribute.number-override", json == "{\"Strict\":3,\"Annotated\":\"2.5\"}" &&
            copy is { Strict: 3, Annotated: 2.5m }, json);
        bool rejected = false;
        try { _ = JsonSerializer.Deserialize<NumberModeModel>("{\"Strict\":\"3\"}", options); }
        catch (JsonException) { rejected = true; }
        DemoCheck.Expect("attribute.number-strict", rejected, rejected ? "rejected" : "accepted");
        NamedNumbers();
        ClassNumbers();
    }

    private static void NamedNumbers()
    {
        var named = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        string special = JsonSerializer.Serialize(double.NaN, named);
        double decoded = JsonSerializer.Deserialize<double>(special, named);
        DemoCheck.Expect("options.number-nan", special == "\"NaN\"" && double.IsNaN(decoded), special);
        string infinity = JsonSerializer.Serialize(double.NegativeInfinity, named);
        double negative = JsonSerializer.Deserialize<double>(infinity, named);
        DemoCheck.Expect("options.number-infinity", infinity == "\"-Infinity\"" &&
            double.IsNegativeInfinity(negative), infinity);
        string positiveJson = JsonSerializer.Serialize(float.PositiveInfinity, named);
        float positive = JsonSerializer.Deserialize<float>(positiveJson, named);
        DemoCheck.Expect("options.number-positive-infinity", positiveJson == "\"Infinity\"" &&
            float.IsPositiveInfinity(positive), positiveJson);
    }

    private static void ClassNumbers()
    {
        var classValue = new ClassNumberModeModel
        {
            Number = 4, Strict = 5, Values = [1, null, 2], Map = { ["a"] = 7 }, Maybe = 8, StrictValues = [9]
        };
        string classJson = JsonSerializer.Serialize(classValue);
        var classCopy = JsonSerializer.Deserialize<ClassNumberModeModel>(classJson);
        DemoCheck.Expect("attribute.class-number-handling", classJson ==
            "{\"Number\":\"4\",\"Strict\":5,\"Values\":[\"1\",null,\"2\"],\"Map\":{\"a\":\"7\"}," +
            "\"Maybe\":\"8\",\"StrictValues\":[9]}" &&
            classCopy is { Number: 4, Strict: 5 } && classCopy.Values.SequenceEqual([1, null, 2]) &&
            classCopy.Map["a"] == 7 && classCopy.Maybe == 8 && classCopy.StrictValues.SequenceEqual([9]), classJson);
    }
}

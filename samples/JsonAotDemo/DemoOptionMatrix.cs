using System.Text.Json;
using System.Text.Json.Serialization;

internal static class DemoOptionMatrix
{
    internal static void Run()
    {
        (string Name, Action Run)[] scenarios =
        [
            ("naming-policies", NamingPolicies), ("number-handling", NumberHandling),
            ("ignore-conditions", IgnoreConditions), ("copied-options", ClonedOptions),
            ("enum-policies", EnumPolicies), ("indentation", Indentation),
            ("ignore-null-attribute", IgnoreNullAttribute), ("wide-number-handling", WideNumberHandling)
        ];
        foreach (var (name, run) in scenarios) DemoCheck.Run(name, run);
    }

    private static void NamingPolicies()
    {
        (JsonNamingPolicy? Policy, string Name)[] policies =
        [
            (null, "DisplayName"), (JsonNamingPolicy.CamelCase, "displayName"),
            (JsonNamingPolicy.SnakeCaseLower, "display_name"), (JsonNamingPolicy.SnakeCaseUpper, "DISPLAY_NAME"),
            (JsonNamingPolicy.KebabCaseLower, "display-name"), (JsonNamingPolicy.KebabCaseUpper, "DISPLAY-NAME")
        ];
        foreach (var (policy, name) in policies)
            DemoCheck.Run("naming-policy." + name, () =>
            {
                var options = new JsonSerializerOptions { PropertyNamingPolicy = policy, DictionaryKeyPolicy = policy };
                string expected = "{\"" + name + "\":\"Ada\"}";
                string json = JsonSerializer.Serialize(new NamingCaseModel { DisplayName = "Ada" }, options);
                var copy = JsonSerializer.Deserialize<NamingCaseModel>(json, options);
                string dictionary = JsonSerializer.Serialize(new Dictionary<string, string> { ["DisplayName"] = "Ada" }, options);
                DemoCheck.Expect("options.naming-matrix." + name, json == expected && copy?.DisplayName == "Ada" &&
                    dictionary == expected, json);
            });
    }

    private static void NumberHandling()
    {
        for (int flags = 0; flags < 8; flags++)
            DemoCheck.Run("number-handling." + flags, () =>
            {
                var options = new JsonSerializerOptions { NumberHandling = (JsonNumberHandling)flags };
                string json = JsonSerializer.Serialize(42, options);
                string expected = (flags & (int)JsonNumberHandling.WriteAsString) != 0 ? "\"42\"" : "42";
                if ((flags & (int)JsonNumberHandling.WriteAsString) != 0 &&
                    (flags & (int)JsonNumberHandling.AllowReadingFromString) == 0)
            {
                    bool rejected = false;
                    try { _ = JsonSerializer.Deserialize<int>(json, options); }
                    catch (JsonException) { rejected = true; }
                    DemoCheck.Expect("options.number-matrix." + flags, json == expected && rejected, json);
                }
                else
            {
                    int copy = JsonSerializer.Deserialize<int>(json, options);
                    DemoCheck.Expect("options.number-matrix." + flags, json == expected && copy == 42, json);
                }
            });
    }

    private static void IgnoreConditions()
    {
        (JsonIgnoreCondition Condition, string Expected)[] cases =
        [
            (JsonIgnoreCondition.Never, "{\"Text\":null,\"Number\":0}"),
            (JsonIgnoreCondition.WhenWritingNull, "{\"Number\":0}"),
            (JsonIgnoreCondition.WhenWritingDefault, "{}")
        ];
        foreach (var (condition, expected) in cases)
            DemoCheck.Run("ignore-condition." + condition, () =>
            {
                var options = new JsonSerializerOptions { DefaultIgnoreCondition = condition };
                string json = JsonSerializer.Serialize(new IgnoreMatrix(), options);
                DemoCheck.Expect("options.ignore-matrix." + condition, json == expected, json);
            });
    }

    private static void ClonedOptions()
    {
        var source = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        source.Converters.Add(new FactoryValueConverterFactory());
        var clone = new JsonSerializerOptions(source) { WriteIndented = true };
        string json = JsonSerializer.Serialize(new NamingCaseModel { DisplayName = "Ada" }, clone);
        var copy = JsonSerializer.Deserialize<NamingCaseModel>(json, clone);
        string converted = JsonSerializer.Serialize(new FactoryValue { Number = 3 }, clone);
        var convertedCopy = JsonSerializer.Deserialize<FactoryValue>(converted, clone);
        bool cloneLocked = clone.IsReadOnly;
        bool sourceMutable = !source.IsReadOnly;
        DemoCheck.Expect("options.copy-constructor", json.Contains("\"displayName\"") &&
            copy?.DisplayName == "Ada" && converted == "\"value:3\"" && convertedCopy?.Number == 3 &&
            cloneLocked && sourceMutable, json.Replace("\n", " "));
    }

    private static void EnumPolicies()
    {
        (JsonNamingPolicy? Policy, string Name)[] policies =
        [
            (null, "FirstValue"), (JsonNamingPolicy.CamelCase, "firstValue"),
            (JsonNamingPolicy.SnakeCaseLower, "first_value"), (JsonNamingPolicy.SnakeCaseUpper, "FIRST_VALUE"),
            (JsonNamingPolicy.KebabCaseLower, "first-value"), (JsonNamingPolicy.KebabCaseUpper, "FIRST-VALUE")
        ];
        foreach (var (policy, name) in policies)
            DemoCheck.Run("enum-policy." + name, () =>
            {
                var options = new JsonSerializerOptions();
                options.Converters.Add(new JsonStringEnumConverter<OptionEnum>(policy, false));
                string json = JsonSerializer.Serialize(OptionEnum.FirstValue, options);
                OptionEnum copy = JsonSerializer.Deserialize<OptionEnum>(json, options);
                bool numberRejected = false;
                try { _ = JsonSerializer.Deserialize<OptionEnum>("1", options); }
                catch (JsonException) { numberRejected = true; }
                DemoCheck.Expect("options.enum-policy." + name, json == "\"" + name + "\"" &&
                    copy == OptionEnum.FirstValue && numberRejected, json);
            });
    }

    private static void Indentation()
    {
        foreach (char character in new[] { ' ', '\t' })
        foreach (int size in new[] { 0, 1, 2, 4 })
        foreach (string newline in new[] { "\n", "\r\n" })
            DemoCheck.Run("indentation." + (int)character + "." + size + "." + newline.Length, () =>
            {
                var options = new JsonSerializerOptions
            {
                    WriteIndented = true,
                    IndentCharacter = character,
                    IndentSize = size,
                    NewLine = newline
                };
                string expected = "{" + newline + new string(character, size) + "\"Number\": 1" + newline + "}";
                string json = JsonSerializer.Serialize(new IndentValue { Number = 1 }, options);
                DemoCheck.Expect("options.indentation." + (int)character + "." + size + "." + newline.Length,
                    json == expected, json.Replace(newline, " "));
            });
    }

    private static void IgnoreNullAttribute()
    {
        string empty = JsonSerializer.Serialize(new AttributeIgnoreNull());
        string present = JsonSerializer.Serialize(new AttributeIgnoreNull { Text = "Ada" });
        var decoded = JsonSerializer.Deserialize<AttributeIgnoreNull>("{\"Text\":null}");
        DemoCheck.Expect("attribute.ignore-when-writing-null", empty == "{}" &&
            present == "{\"Text\":\"Ada\"}" && decoded?.Text is null, present);
    }

    private static void WideNumberHandling()
    {
        var options = new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString |
                JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        string wide = JsonSerializer.Serialize(Int128.MaxValue, options);
        Int128 wideCopy = JsonSerializer.Deserialize<Int128>(wide, options);
        string half = JsonSerializer.Serialize(Half.NaN, options);
        Half halfCopy = JsonSerializer.Deserialize<Half>(half, options);
        DemoCheck.Expect("options.wide-number-handling", wide.StartsWith('"') && wideCopy == Int128.MaxValue &&
            half == "\"NaN\"" && Half.IsNaN(halfCopy), wide + "/" + half);
    }
}

public sealed class IgnoreMatrix
{
    public string? Text { get; set; }
    public int Number { get; set; }
}

public enum OptionEnum
{
    FirstValue = 1,
    SecondValue = 2
}

public sealed class IndentValue
{
    public int Number { get; set; }
}

public sealed class AttributeIgnoreNull
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }
}

using System.Text.Json;

internal static class DemoConstructor
{
    internal static void Run()
    {
        const string expected = "{\"Name\":\"Ada\",\"Count\":7,\"Included\":9}";
        var value = new DemoConstructorModel("Ada", 7) { Included = 9 };
        string json = JsonSerializer.Serialize(value);
        DemoCheck.Expect("constructor.encode", json == expected, json);
        DemoConstructorModel decoded = JsonSerializer.Deserialize<DemoConstructorModel>(json)
            ?? throw new InvalidOperationException("Missing constructor model.");
        DemoCheck.Expect("constructor.decode", decoded.Name == "Ada" && decoded.Count == 7,
            $"name={decoded.Name}, count={decoded.Count}");
        DemoCheck.Expect("attribute.include-field", decoded.Included == 9, $"included={decoded.Included}");
    }
}

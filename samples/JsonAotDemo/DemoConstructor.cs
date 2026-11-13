using System.Text.Json;

internal static class DemoConstructor
{
    internal static void Run()
    {
        var internalConstructor = JsonSerializer.Deserialize<InternalConstructorModel>("{\"Name\":\"Ada\"}");
        DemoCheck.Expect("constructor.internal", internalConstructor?.Name == "Ada", internalConstructor?.Name ?? "null");
        var emptyConstructor = JsonSerializer.Deserialize<InternalEmptyConstructorModel>("{\"Number\":7}");
        DemoCheck.Expect("constructor.internal-empty", emptyConstructor?.Number == 7,
            emptyConstructor?.Number.ToString() ?? "null");
        var sharedConstructor = JsonSerializer.Deserialize<SharedConstructorModel>("{\"Number\":8}");
        DemoCheck.Expect("constructor.protected-internal", sharedConstructor?.Number == 8,
            sharedConstructor?.Number.ToString() ?? "null");
        var privateConstructor = JsonSerializer.Deserialize<PrivateConstructorModel>("{\"Number\":9}");
        DemoCheck.Expect("constructor.private", privateConstructor?.Number == 9,
            privateConstructor?.Number.ToString() ?? "null");
        var privateEmpty = JsonSerializer.Deserialize<PrivateEmptyConstructorModel>("{\"Number\":10}");
        DemoCheck.Expect("constructor.private-empty", privateEmpty?.Number == 10,
            privateEmpty?.Number.ToString() ?? "null");
        RequiredInit();
        const string expected = "{\"Name\":\"Ada\",\"Count\":7,\"Included\":9}";
        var value = new DemoConstructorModel("Ada", 7) { Included = 9 };
        string json = JsonSerializer.Serialize(value);
        DemoCheck.Expect("constructor.encode", json == expected, json);
        DemoConstructorModel decoded = JsonSerializer.Deserialize<DemoConstructorModel>(json)
            ?? throw new InvalidOperationException("Missing constructor model.");
        DemoCheck.Expect("constructor.decode", decoded.Name == "Ada" && decoded.Count == 7,
            $"name={decoded.Name}, count={decoded.Count}");
        DemoCheck.Expect("attribute.include-field", decoded.Included == 9, $"included={decoded.Included}");
        Subset();
        RequiredParameters();
    }

    private static void RequiredInit()
    {
        var value = new RequiredInitModel { Name = "Ada", Count = 3, Code = 7 };
        string json = JsonSerializer.Serialize(value);
        var copy = JsonSerializer.Deserialize<RequiredInitModel>(json);
        bool rejected = false;
        try { _ = JsonSerializer.Deserialize<RequiredInitModel>("{\"Name\":\"Ada\"}"); }
        catch (JsonException) { rejected = true; }
        DemoCheck.Expect("constructor.required-init", copy is { Name: "Ada", Count: 3, Code: 7 } && rejected, json);
        var structure = new RequiredInitStruct { Number = 4, Code = 5 };
        string structJson = JsonSerializer.Serialize(structure);
        var structCopy = JsonSerializer.Deserialize<RequiredInitStruct>(structJson);
        DemoCheck.Expect("constructor.required-struct", structCopy is { Number: 4, Code: 5 }, structJson);
        var fieldOptions = new JsonSerializerOptions { IncludeFields = true };
        var fieldCopy = JsonSerializer.Deserialize<RequiredFieldModel>("{\"Number\":6}", fieldOptions);
        bool fieldRejected = false;
        try { _ = JsonSerializer.Deserialize<RequiredFieldModel>("{}", fieldOptions); }
        catch (JsonException) { fieldRejected = true; }
        DemoCheck.Expect("constructor.required-field-option", fieldCopy?.Number == 6 && fieldRejected, "6");
        var defaulted = JsonSerializer.Deserialize<ConstructorSetsRequiredModel>("{\"Explicit\":1}");
        bool explicitRejected = false;
        try { _ = JsonSerializer.Deserialize<ConstructorSetsRequiredModel>("{}"); }
        catch (JsonException) { explicitRejected = true; }
        DemoCheck.Expect("constructor.sets-required-members", defaulted?.Name == "default" && explicitRejected,
            defaulted?.Name ?? "null");
    }

    private static void RequiredParameters()
    {
        var options = new JsonSerializerOptions { RespectRequiredConstructorParameters = true };
        bool rejected = false;
        try { _ = JsonSerializer.Deserialize<ConstructorSubsetModel>("{}", options); }
        catch (JsonException) { rejected = true; }
        DemoCheck.Expect("options.required-constructor", rejected, rejected ? "rejected" : "accepted");
        var value = JsonSerializer.Deserialize<ConstructorSubsetModel>("{\"Count\":7}", options);
        DemoCheck.Expect("options.optional-constructor", value is { Count: 7, Name: "fallback", Scale: 5 },
            value?.Name ?? "null");
    }

    private static void Subset()
    {
        var value = JsonSerializer.Deserialize<ConstructorSubsetModel>("{\"Name\":\"Ada\",\"Count\":7,\"Note\":\"ok\"}");
        DemoCheck.Expect("constructor.subset", value is { Name: "Ada", Count: 7, Note: "ok" }, value?.Note ?? "null");
        var missing = JsonSerializer.Deserialize<ConstructorSubsetModel>("{\"Name\":\"Ada\",\"Count\":7}");
        DemoCheck.Expect("constructor.initializer", missing?.Note == "initial", missing?.Note ?? "null");
        var optional = JsonSerializer.Deserialize<ConstructorSubsetModel>("{\"Count\":7}");
        DemoCheck.Expect("constructor.optional-defaults", optional is { Name: "fallback", Scale: 5 },
            optional?.Name ?? "null");
    }
}

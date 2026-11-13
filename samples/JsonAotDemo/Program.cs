internal static class Program
{
    private static async Task Main()
    {
        DemoCheck.Expect("runtime.reflection-disabled", !System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault,
            System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault ? "enabled" : "disabled");
        var message = new Message
        {
            Name = "Ada",
            Number = 7,
            Tags = ["one", "two"],
            Details = new Detail { Enabled = true }
        };

        string json = DemoStrings.Expected;
        DemoCheck.Run("strings", () => json = DemoStrings.Run(message));
        DemoCheck.Run("bytes", () => DemoBytes.Run(message, json));
        DemoCheck.Run("streams", () => DemoStreams.Run(message, json));
        await DemoCheck.RunAsync("async", () => DemoAsync.Run(message, json));
        await DemoCheck.RunAsync("async-sequences", DemoAsyncSequences.Run);
        await DemoCheck.RunAsync("pipelines", () => DemoPipelines.Run(message));
        await DemoCheck.RunAsync("advanced", DemoAdvanced.Run);
        await DemoCheck.RunAsync("metadata-overloads", DemoMetadataOverloads.Run);
        DemoCheck.Run("options", () => DemoOptions.Run(message));
        DemoCheck.Run("unsupported-exceptions", () => DemoUnsupportedExceptions.Run(message));
        DemoCheck.Run("low-level", () => DemoLowLevel.Run(json));
        RunScenarios();
        DemoCheck.Complete();
    }

    private static void RunScenarios()
    {
        (string Name, Action Run)[] scenarios =
        [
            ("collections", DemoCollections.Run), ("immutable", DemoImmutable.Run),
            ("concurrent", DemoConcurrent.Run), ("non-generic", DemoNonGeneric.Run),
            ("more-types", DemoMoreTypes.Run), ("generics", DemoGenerics.Run),
            ("more-collections", DemoMoreCollections.Run),
            ("special-collections", DemoSpecialCollections.Run),
            ("option-matrix", DemoOptionMatrix.Run), ("contract-options", DemoContractOptions.Run),
            ("duplicate-options", DemoDuplicateOptions.Run), ("number-options", DemoNumberOptions.Run),
            ("web-options", DemoWebOptions.Run), ("nullable-options", DemoNullableOptions.Run),
            ("populate", DemoPopulate.Run), ("enum", DemoEnum.Run),
            ("converter-attributes", DemoConverterAttributes.Run), ("constructors", DemoConstructor.Run),
            ("callbacks", DemoCallbacks.Run), ("dynamic", DemoDynamic.Run),
            ("context", DemoContext.Run), ("context-options", DemoContextOptions.Run),
            ("polymorphism", DemoPolymorphism.Run), ("references", DemoReferences.Run),
            ("combined", DemoCombinedCases.Run), ("extension", DemoExtension.Run),
            ("nullable", DemoNullable.Run), ("scalars", DemoStandardScalars.Run),
            ("named-enum", DemoNamedEnum.Run), ("reader-writer", DemoReaderWriterCases.Run),
            ("dom-values", DemoDomValues.Run), ("errors", DemoErrors.Run),
            ("exception-cases", DemoExceptionCases.Run)
        ];
        foreach (var (name, run) in scenarios) DemoCheck.Run(name, run);
    }
}

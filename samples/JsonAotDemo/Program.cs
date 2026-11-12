internal static class Program
{
    private static async Task Main()
    {
        var message = new Message
        {
            Name = "Ada",
            Number = 7,
            Tags = ["one", "two"],
            Details = new Detail { Enabled = true }
        };

        string json = DemoStrings.Run(message);
        DemoBytes.Run(message, json);
        DemoStreams.Run(message, json);
        await DemoAsync.Run(message, json);
        DemoCollections.Run();
        DemoOptions.Run(message);
        DemoUnsupportedExceptions.Run(message);
        DemoPopulate.Run();
        DemoEnum.Run();
        DemoConstructor.Run();
        DemoExtension.Run();
        DemoNullable.Run();
        DemoNamedEnum.Run();
        DemoLowLevel.Run(json);
        DemoErrors.Run();
        Console.WriteLine("JSON_DEMO_PASS");
    }
}

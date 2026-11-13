using System.Text.Json;

internal static class DemoUnsupportedExceptions
{
    internal static void Run(Message value)
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new DemoUnsupportedConverter());
        bool readRejected = false;
        try { _ = JsonSerializer.Deserialize<Message>("{\"display_name\":\"Ada\",\"Details\":true}", options); }
        catch (NotSupportedException error)
        {
            readRejected = error.Message.Contains("$.Details", StringComparison.Ordinal);
        }
        DemoCheck.Expect("error.not-supported-default", readRejected, readRejected ? "rejected" : "accepted");

        bool writeRejected = false;
        try { _ = JsonSerializer.Serialize(value, options); }
        catch (NotSupportedException error)
        {
            writeRejected = error.Message.Contains("custom converter refused", StringComparison.Ordinal) &&
                error.Message.Contains("$.Details", StringComparison.Ordinal);
        }
        DemoCheck.Expect("error.not-supported-custom", writeRejected, writeRejected ? "rejected" : "accepted");
    }
}

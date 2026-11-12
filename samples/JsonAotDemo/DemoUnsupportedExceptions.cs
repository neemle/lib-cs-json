using System.Text.Json;

internal static class DemoUnsupportedExceptions
{
    internal static void Run(Message value)
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new DemoUnsupportedConverter());
        string readMessage = "";
        try { _ = JsonSerializer.Deserialize<Message>("{\"display_name\":\"Ada\",\"Details\":true}", options); }
        catch (NotSupportedException error) { readMessage = error.Message; }
        DemoCheck.Expect("error.not-supported-default", readMessage.Contains(
            "Specified method is not supported.", StringComparison.Ordinal)
            && readMessage.Contains("Path: $.Details", StringComparison.Ordinal), readMessage);

        string writeMessage = "";
        try { _ = JsonSerializer.Serialize(value, options); }
        catch (NotSupportedException error) { writeMessage = error.Message; }
        DemoCheck.Expect("error.not-supported-custom", writeMessage.Contains("custom converter refused",
            StringComparison.Ordinal)
            && writeMessage.Contains("Path: $.Details", StringComparison.Ordinal), writeMessage);
    }
}

using System.Text;
using System.Text.Json;

internal static class DemoStreams
{
    internal static void Run(Message message, string expected)
    {
        using var stream = new MemoryStream();
        JsonSerializer.Serialize(stream, message);
        string actual = Encoding.UTF8.GetString(stream.ToArray());
        DemoCheck.Expect("encode.stream", actual == expected, actual);
        stream.Position = 0;
        Message value = JsonSerializer.Deserialize<Message>(stream)
            ?? throw new InvalidOperationException("Deserialization returned null.");
        DemoCheck.Expect("decode.stream", value.Name == "Ada" && value.Number == 7,
            $"name={value.Name}, number={value.Number}");
    }
}

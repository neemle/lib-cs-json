using System.Text;
using System.Text.Json;

internal static class DemoStreams
{
    internal static void Run(Message message, string expected)
    {
        Type runtimeType = message.Number > 0 ? typeof(Message) : typeof(string);
        using var runtimeStream = new MemoryStream();
        JsonSerializer.Serialize(runtimeStream, message, runtimeType);
        runtimeStream.Position = 0;
        object? runtimeCopy = JsonSerializer.Deserialize(runtimeStream, runtimeType);
        DemoCheck.Expect("stream.runtime-type", runtimeCopy is Message { Name: "Ada", Number: 7 },
            System.Text.Encoding.UTF8.GetString(runtimeStream.ToArray()));
        using var stream = new MemoryStream();
        JsonSerializer.Serialize(stream, message);
        string actual = Encoding.UTF8.GetString(stream.ToArray());
        DemoCheck.Expect("encode.stream", actual == expected, actual);
        stream.Position = 0;
        Message value = JsonSerializer.Deserialize<Message>(stream)
            ?? throw new InvalidOperationException("Deserialization returned null.");
        DemoCheck.Expect("decode.stream", value.Name == "Ada" && value.Number == 7,
            $"name={value.Name}, number={value.Number}");
        using var declaredStream = new MemoryStream();
        JsonSerializer.Serialize(declaredStream, (object)message, typeof(Message));
        declaredStream.Position = 0;
        object? declared = JsonSerializer.Deserialize(declaredStream, typeof(Message));
        DemoCheck.Expect("stream.declared-type", declared is Message { Name: "Ada", Number: 7 },
            Encoding.UTF8.GetString(declaredStream.ToArray()));
    }
}

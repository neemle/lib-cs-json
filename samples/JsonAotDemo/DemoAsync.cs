using System.Text;
using System.Text.Json;

internal static class DemoAsync
{
    internal static async Task Run(Message message, string expected)
    {
        using var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(stream, message);
        string actual = Encoding.UTF8.GetString(stream.ToArray());
        DemoCheck.Expect("encode.stream-async", actual == expected, actual);
        stream.Position = 0;
        Message value = await JsonSerializer.DeserializeAsync<Message>(stream)
            ?? throw new InvalidOperationException("Deserialization returned null.");
        DemoCheck.Expect("decode.stream-async", value.Name == "Ada" && value.Number == 7,
            $"name={value.Name}, number={value.Number}");
        using var declaredStream = new MemoryStream();
        await JsonSerializer.SerializeAsync(declaredStream, (object)message, typeof(Message));
        declaredStream.Position = 0;
        object? declared = await JsonSerializer.DeserializeAsync(declaredStream, typeof(Message));
        DemoCheck.Expect("stream-async.declared-type", declared is Message { Name: "Ada", Number: 7 },
            Encoding.UTF8.GetString(declaredStream.ToArray()));
        Type runtimeType = message.Number > 0 ? typeof(Message) : typeof(string);
        using var runtimeStream = new MemoryStream();
        await JsonSerializer.SerializeAsync(runtimeStream, message, runtimeType);
        runtimeStream.Position = 0;
        object? runtimeCopy = await JsonSerializer.DeserializeAsync(runtimeStream, runtimeType);
        DemoCheck.Expect("stream-async.runtime-type", runtimeCopy is Message { Name: "Ada", Number: 7 },
            Encoding.UTF8.GetString(runtimeStream.ToArray()));
    }
}

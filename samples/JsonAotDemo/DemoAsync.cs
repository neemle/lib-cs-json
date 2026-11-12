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
    }
}

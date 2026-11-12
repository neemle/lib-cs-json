using System.Text;
using System.Text.Json;

internal static class DemoBytes
{
    internal static void Run(Message message, string expected)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        string actual = Encoding.UTF8.GetString(bytes);
        DemoCheck.Expect("encode.utf8", actual == expected, actual);
        Message value = JsonSerializer.Deserialize<Message>(bytes)
            ?? throw new InvalidOperationException("Deserialization returned null.");
        DemoCheck.Expect("decode.utf8", value.Name == "Ada" && value.Number == 7,
            $"name={value.Name}, number={value.Number}");
    }
}

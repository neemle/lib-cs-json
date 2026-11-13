using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

internal static class DemoPipelines
{
    internal static async Task Run(Message message)
    {
        var pipe = new Pipe();
        await JsonSerializer.SerializeAsync(pipe.Writer, message);
        await pipe.Writer.CompleteAsync();
        Message? value = await JsonSerializer.DeserializeAsync<Message>(utf8Json: pipe.Reader);
        await pipe.Reader.CompleteAsync();
        DemoCheck.Expect("pipe.roundtrip", value is { Name: "Ada", Number: 7 }, value?.Name ?? "null");
        Type selected = message.Number > 0 ? typeof(Message) : typeof(string);
        var typedPipe = new Pipe();
        await JsonSerializer.SerializeAsync(typedPipe.Writer, message, selected);
        await typedPipe.Writer.CompleteAsync();
        object? typedValue = await JsonSerializer.DeserializeAsync(typedPipe.Reader, selected);
        await typedPipe.Reader.CompleteAsync();
        DemoCheck.Expect("pipe.runtime-type", typedValue is Message { Name: "Ada", Number: 7 }, "Ada");

        var sequence = new Pipe();
        await sequence.Writer.WriteAsync(Encoding.UTF8.GetBytes("[1,2,3]"));
        await sequence.Writer.CompleteAsync();
        var values = new List<int>();
        await foreach (int item in JsonSerializer.DeserializeAsyncEnumerable<int>(sequence.Reader)) values.Add(item);
        await sequence.Reader.CompleteAsync();
        DemoCheck.Expect("pipe.sequence", values.SequenceEqual([1, 2, 3]), string.Join(',', values));
        var output = new Pipe();
        await JsonSerializer.SerializeAsync(output.Writer, Values());
        await output.Writer.CompleteAsync();
        using var outputStream = output.Reader.AsStream();
        using var buffer = new MemoryStream();
        await outputStream.CopyToAsync(buffer);
        DemoCheck.Expect("pipe.encode-sequence", Encoding.UTF8.GetString(buffer.ToArray()) == "[4,5]",
            Encoding.UTF8.GetString(buffer.ToArray()));
        await TopLevelSequence();
    }

    private static async IAsyncEnumerable<int> Values()
    {
        yield return 4;
        await Task.Yield();
        yield return 5;
    }

    private static async Task TopLevelSequence()
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("1 2 3"));
        await pipe.Writer.CompleteAsync();
        var values = new List<int>();
        await foreach (int value in JsonSerializer.DeserializeAsyncEnumerable<int>(pipe.Reader, topLevelValues: true))
            values.Add(value);
        await pipe.Reader.CompleteAsync();
        DemoCheck.Expect("pipe.top-level-sequence", values.SequenceEqual([1, 2, 3]), string.Join(',', values));
    }
}

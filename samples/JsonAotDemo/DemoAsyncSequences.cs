using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

internal static class DemoAsyncSequences
{
    internal static async Task Run()
    {
        var options = new JsonSerializerOptions { DefaultBufferSize = 1 };
        using var stream = Input("[1,20,-3]");
        var values = new List<int>();
        await foreach (int value in JsonSerializer.DeserializeAsyncEnumerable<int>(stream, options)) values.Add(value);
        DemoCheck.Expect("async-sequence.array", values.SequenceEqual([1, 20, -3]), string.Join(',', values));
        using var top = Input("1 20 -3");
        var topValues = new List<int>();
        await foreach (int value in JsonSerializer.DeserializeAsyncEnumerable<int>(top, true, options)) topValues.Add(value);
        DemoCheck.Expect("async-sequence.top-level", topValues.SequenceEqual([1, 20, -3]), string.Join(',', topValues));
        using var text = Input("[\"Ганна\",\"A\\nB\",null]");
        var strings = new List<string?>();
        await foreach (string? value in JsonSerializer.DeserializeAsyncEnumerable<string>(text, options)) strings.Add(value);
        DemoCheck.Expect("async-sequence.utf8", strings.Count == 3 && strings[0] == "Ганна" &&
            strings[1] == "A\nB" && strings[2] is null, $"count={strings.Count}");
        await Incremental(options);
        await Bom(options);
        await Cancellation(options);
        await Preserved();
        await CustomReferences();
        await SerializeSequence();
    }

    private static async Task SerializeSequence()
    {
        using var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(stream, Values());
        string json = Encoding.UTF8.GetString(stream.ToArray());
        DemoCheck.Expect("async-sequence.encode", json == "[1,2,3]", json);
        using var declaredStream = new MemoryStream();
        await JsonSerializer.SerializeAsync(declaredStream, (object)Values(), typeof(IAsyncEnumerable<int>));
        string declaredJson = Encoding.UTF8.GetString(declaredStream.ToArray());
        DemoCheck.Expect("async-sequence.encode-declared", declaredJson == "[1,2,3]", declaredJson);
        var shared = new CycleModel();
        using var references = new MemoryStream();
        var options = new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.Preserve };
        await JsonSerializer.SerializeAsync(references, Shared(shared), options);
        string referenceJson = Encoding.UTF8.GetString(references.ToArray());
        DemoCheck.Expect("async-sequence.encode-preserve", referenceJson ==
            "[{\"$id\":\"1\",\"Next\":null},{\"$ref\":\"1\"}]", referenceJson);
        using var empty = new MemoryStream();
        IAsyncEnumerable<int>? absent = null;
        await JsonSerializer.SerializeAsync(empty, absent);
        string nullJson = Encoding.UTF8.GetString(empty.ToArray());
        DemoCheck.Expect("async-sequence.encode-null", nullJson == "null", nullJson);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var canceledStream = new MemoryStream();
        bool canceled = false;
        try { await JsonSerializer.SerializeAsync(canceledStream, Values(), cancellationToken: cancellation.Token); }
        catch (OperationCanceledException) { canceled = true; }
        DemoCheck.Expect("async-sequence.encode-cancel", canceled, canceled ? "canceled" : "completed");
        var converterOptions = new JsonSerializerOptions();
        converterOptions.Converters.Add(new NullAwareStringConverter());
        using var strings = new MemoryStream();
        await JsonSerializer.SerializeAsync(strings, NullableValues(), converterOptions);
        string stringJson = Encoding.UTF8.GetString(strings.ToArray());
        DemoCheck.Expect("async-sequence.encode-converter", stringJson == "[\"encoded-null\",\"Ada\"]", stringJson);
    }

    private static async IAsyncEnumerable<int> Values()
    {
        yield return 1;
        await Task.Yield();
        yield return 2;
        yield return 3;
    }

    private static async IAsyncEnumerable<CycleModel> Shared(CycleModel value)
    {
        yield return value;
        await Task.Yield();
        yield return value;
    }

    private static async IAsyncEnumerable<string?> NullableValues()
    {
        yield return null;
        await Task.Yield();
        yield return "Ada";
    }

    private static async Task CustomReferences()
    {
        var options = new JsonSerializerOptions { DefaultBufferSize = 1, ReferenceHandler = new DemoReferenceHandler() };
        using var stream = Input("[{\"$id\":\"x\",\"Next\":null},{\"$ref\":\"x\"}]");
        var values = new List<CycleModel?>();
        await foreach (CycleModel? value in JsonSerializer.DeserializeAsyncEnumerable<CycleModel>(stream, options))
            values.Add(value);
        bool shared = values.Count == 2 && values[0] is not null && ReferenceEquals(values[0], values[1]);
        DemoCheck.Expect("async-sequence.custom-handler", shared, shared ? "shared" : "different");
    }

    private static async Task Preserved()
    {
        var options = new JsonSerializerOptions { DefaultBufferSize = 1, ReferenceHandler = ReferenceHandler.Preserve };
        using var stream = Input("[{\"$id\":\"1\",\"Next\":null},{\"$ref\":\"1\"}]");
        var values = new List<CycleModel?>();
        await foreach (CycleModel? value in JsonSerializer.DeserializeAsyncEnumerable<CycleModel>(stream, options))
            values.Add(value);
        bool shared = values.Count == 2 && values[0] is not null && ReferenceEquals(values[0], values[1]);
        DemoCheck.Expect("async-sequence.preserve", shared, shared ? "shared" : "different");
    }

    private static async Task Bom(JsonSerializerOptions options)
    {
        byte[] bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("[1]")).ToArray();
        using var stream = new MemoryStream(bytes);
        var values = new List<int>();
        await foreach (int value in JsonSerializer.DeserializeAsyncEnumerable<int>(stream, options)) values.Add(value);
        DemoCheck.Expect("async-sequence.bom", values.SequenceEqual([1]), string.Join(',', values));
    }

    private static async Task Cancellation(JsonSerializerOptions options)
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        using var stream = Input("[1,2]");
        bool canceled = false;
        try
        {
            await foreach (int value in JsonSerializer.DeserializeAsyncEnumerable<int>(stream, options, source.Token))
                _ = value;
        }
        catch (OperationCanceledException) { canceled = true; }
        DemoCheck.Expect("async-sequence.cancel", canceled, canceled ? "canceled" : "completed");
    }

    private static async Task Incremental(JsonSerializerOptions options)
    {
        using var stream = Input("[1,2,3,4,5,6,7,8,9]");
        await using var enumerator = JsonSerializer.DeserializeAsyncEnumerable<int>(stream, options).GetAsyncEnumerator();
        bool first = await enumerator.MoveNextAsync();
        bool partial = stream.Position < stream.Length;
        DemoCheck.Expect("async-sequence.incremental", first && enumerator.Current == 1 && partial,
            first && partial ? "partial" : "buffered");
    }

    private static MemoryStream Input(string json) => new(Encoding.UTF8.GetBytes(json));
}

using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class DemoMetadataOverloads
{
    private const string Json = "{\"Name\":\"Ada\"}";

    internal static async Task Run()
    {
        (string Name, Action Run)[] scenarios =
        [
            ("metadata-sync", Sync), ("metadata-reader-writer", ReaderWriter),
            ("metadata-untyped-reader-writer", UntypedReaderWriter), ("metadata-untyped", Untyped),
            ("metadata-untyped-dom", UntypedDom), ("metadata-dom-extensions", DomExtensions),
            ("metadata-declared-context", DeclaredContext),
            ("metadata-declared-context-reader-writer", DeclaredContextReaderWriter)
        ];
        foreach (var (name, run) in scenarios) DemoCheck.Run(name, run);
        await DemoCheck.RunAsync("metadata-streams", Streams);
        await DemoCheck.RunAsync("metadata-declared-context-pipes", DeclaredContextPipes);
        await DemoCheck.RunAsync("metadata-untyped-async", UntypedAsync);
        await DemoCheck.RunAsync("metadata-array-stream", ArraySequence);
        await DemoCheck.RunAsync("metadata-top-level-pipe", TopLevelPipe);
        await DemoCheck.RunAsync("metadata-top-level-stream", TopLevelStream);
        await DemoCheck.RunAsync("metadata-array-pipe", ArrayPipe);
    }

    private static void Sync()
    {
        var info = AdvancedJsonContext.Default.AdvancedPayload;
        byte[] bytes = Encoding.UTF8.GetBytes(Json);
        var fromBytes = JsonSerializer.Deserialize(bytes.AsSpan(), info);
        var fromChars = JsonSerializer.Deserialize(Json.AsSpan(), info);
        using var document = JsonDocument.Parse(Json);
        var fromDocument = JsonSerializer.Deserialize(document, info);
        JsonNode? node = JsonNode.Parse(Json);
        var fromNode = JsonSerializer.Deserialize(node, info);
        AdvancedPayload value = fromNode ?? throw new InvalidOperationException("Missing JSON value.");
        string encoded = JsonSerializer.Serialize(value, info);
        byte[] encodedBytes = JsonSerializer.SerializeToUtf8Bytes(value, info);
        JsonElement element = JsonSerializer.SerializeToElement(value, info);
        using JsonDocument output = JsonSerializer.SerializeToDocument(value, info);
        JsonNode? outputNode = JsonSerializer.SerializeToNode(value, info);
        using var stream = new MemoryStream();
        JsonSerializer.Serialize(stream, value, info);
        stream.Position = 0;
        var fromStream = JsonSerializer.Deserialize(stream, info);
        DemoCheck.Expect("metadata.sync-overloads", fromBytes?.Name == "Ada" && fromChars?.Name == "Ada" &&
            fromDocument?.Name == "Ada" && fromNode?.Name == "Ada" && fromStream?.Name == "Ada" && encoded == Json &&
            Encoding.UTF8.GetString(encodedBytes) == Json &&
            element.GetProperty("Name").GetString() == "Ada" &&
            output.RootElement.GetProperty("Name").GetString() == "Ada" &&
            outputNode?["Name"]?.GetValue<string>() == "Ada", encoded);
    }

    private static void Untyped()
    {
        System.Text.Json.Serialization.Metadata.JsonTypeInfo info = AdvancedJsonContext.Default.AdvancedPayload;
        object value = new AdvancedPayload { Name = "Ada" };
        string json = JsonSerializer.Serialize(value, info);
        object? copy = JsonSerializer.Deserialize(json, info);
        using var stream = new MemoryStream();
        JsonSerializer.Serialize(stream, value, info);
        stream.Position = 0;
        object? fromStream = JsonSerializer.Deserialize(stream, info);
        DemoCheck.Expect("metadata.untyped-overloads", json == Json &&
            copy is AdvancedPayload { Name: "Ada" } && fromStream is AdvancedPayload { Name: "Ada" }, json);
    }

    private static void UntypedDom()
    {
        System.Text.Json.Serialization.Metadata.JsonTypeInfo info = AdvancedJsonContext.Default.AdvancedPayload;
        object value = new AdvancedPayload { Name = "Ada" };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, info);
        JsonElement element = JsonSerializer.SerializeToElement(value, info);
        using JsonDocument document = JsonSerializer.SerializeToDocument(value, info);
        JsonNode? node = JsonSerializer.SerializeToNode(value, info);
        object? fromBytes = JsonSerializer.Deserialize(bytes.AsSpan(), info);
        object? fromChars = JsonSerializer.Deserialize(Json.AsSpan(), info);
        object? fromElement = JsonSerializer.Deserialize(element, info);
        object? fromDocument = JsonSerializer.Deserialize(document, info);
        object? fromNode = JsonSerializer.Deserialize(node, info);
        DemoCheck.Expect("metadata.untyped-dom", Encoding.UTF8.GetString(bytes) == Json &&
            fromBytes is AdvancedPayload { Name: "Ada" } && fromChars is AdvancedPayload { Name: "Ada" } &&
            fromElement is AdvancedPayload { Name: "Ada" } && fromDocument is AdvancedPayload { Name: "Ada" } &&
            fromNode is AdvancedPayload { Name: "Ada" }, element.GetRawText());
    }

    private static void DomExtensions()
    {
        JsonNode node = JsonNode.Parse(Json)!;
        var fromNode = node.Deserialize(AdvancedJsonContext.Default.AdvancedPayload);
        using JsonDocument document = JsonDocument.Parse(Json);
        var fromElement = document.RootElement.Deserialize(AdvancedJsonContext.Default.AdvancedPayload);
        var fromDocument = document.Deserialize(AdvancedJsonContext.Default.AdvancedPayload);
        DemoCheck.Expect("metadata.dom-extensions", fromNode?.Name == "Ada" && fromElement?.Name == "Ada" &&
            fromDocument?.Name == "Ada", node.ToJsonString());
    }

    private static void DeclaredContext()
    {
        object value = new AdvancedPayload { Name = "Ada" };
        string json = JsonSerializer.Serialize(value, typeof(AdvancedPayload), AdvancedJsonContext.Default);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonNode? node = JsonNode.Parse(json);
        object? fromDocument = JsonSerializer.Deserialize(document, typeof(AdvancedPayload), AdvancedJsonContext.Default);
        object? fromNode = JsonSerializer.Deserialize(node, typeof(AdvancedPayload), AdvancedJsonContext.Default);
        JsonElement element = JsonSerializer.SerializeToElement(value, typeof(AdvancedPayload), AdvancedJsonContext.Default);
        using JsonDocument output = JsonSerializer.SerializeToDocument(value, typeof(AdvancedPayload), AdvancedJsonContext.Default);
        JsonNode? outputNode = JsonSerializer.SerializeToNode(value, typeof(AdvancedPayload), AdvancedJsonContext.Default);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeof(AdvancedPayload), AdvancedJsonContext.Default);
        using var stream = new MemoryStream();
        JsonSerializer.Serialize(stream, value, typeof(AdvancedPayload), AdvancedJsonContext.Default);
        stream.Position = 0;
        object? fromStream = JsonSerializer.Deserialize(stream, typeof(AdvancedPayload), AdvancedJsonContext.Default);
        object? fromBytes = JsonSerializer.Deserialize(bytes.AsSpan(), typeof(AdvancedPayload), AdvancedJsonContext.Default);
        object? fromChars = JsonSerializer.Deserialize(Json.AsSpan(), typeof(AdvancedPayload), AdvancedJsonContext.Default);
        object? fromElement = JsonSerializer.Deserialize(element, typeof(AdvancedPayload), AdvancedJsonContext.Default);
        DemoCheck.Expect("metadata.declared-context-dom", json == Json &&
            fromDocument is AdvancedPayload { Name: "Ada" } && fromNode is AdvancedPayload { Name: "Ada" } &&
            fromStream is AdvancedPayload { Name: "Ada" } && fromBytes is AdvancedPayload { Name: "Ada" } &&
            fromChars is AdvancedPayload { Name: "Ada" } && fromElement is AdvancedPayload { Name: "Ada" } &&
            element.GetRawText() == Json && output.RootElement.GetRawText() == Json && outputNode?.ToJsonString() == Json &&
            Encoding.UTF8.GetString(bytes) == Json, json);
    }

    private static void DeclaredContextReaderWriter()
    {
        object value = new AdvancedPayload { Name = "Ada" };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            JsonSerializer.Serialize(writer, value, typeof(AdvancedPayload), AdvancedJsonContext.Default);
        var reader = new Utf8JsonReader(stream.ToArray());
        object? copy = JsonSerializer.Deserialize(ref reader, typeof(AdvancedPayload), AdvancedJsonContext.Default);
        string json = Encoding.UTF8.GetString(stream.ToArray());
        DemoCheck.Expect("metadata.declared-context-reader-writer", json == Json &&
            copy is AdvancedPayload { Name: "Ada" }, json);
    }

    private static async Task DeclaredContextPipes()
    {
        object value = new AdvancedPayload { Name = "Ada" };
        using var streamOnly = new MemoryStream();
        await JsonSerializer.SerializeAsync(streamOnly, value, typeof(AdvancedPayload), AdvancedJsonContext.Default);
        streamOnly.Position = 0;
        object? streamCopy = await JsonSerializer.DeserializeAsync(streamOnly, typeof(AdvancedPayload), AdvancedJsonContext.Default);
        using var stream = new MemoryStream();
        var writer = PipeWriter.Create(stream);
        await JsonSerializer.SerializeAsync(writer, value, typeof(AdvancedPayload), AdvancedJsonContext.Default);
        await writer.CompleteAsync();
        string json = Encoding.UTF8.GetString(stream.ToArray());
        using var input = new MemoryStream(stream.ToArray());
        var reader = PipeReader.Create(input);
        object? copy = await JsonSerializer.DeserializeAsync(reader, typeof(AdvancedPayload), AdvancedJsonContext.Default);
        await reader.CompleteAsync();
        DemoCheck.Expect("metadata.declared-context-pipes", json == Json && copy is AdvancedPayload { Name: "Ada" } &&
            streamCopy is AdvancedPayload { Name: "Ada" }, json);
    }

    private static async Task UntypedAsync()
    {
        System.Text.Json.Serialization.Metadata.JsonTypeInfo info = AdvancedJsonContext.Default.AdvancedPayload;
        object value = new AdvancedPayload { Name = "Ada" };
        using var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(stream, value, info);
        stream.Position = 0;
        object? copy = await JsonSerializer.DeserializeAsync(stream, info);
        using var output = new MemoryStream();
        var writer = PipeWriter.Create(output);
        await JsonSerializer.SerializeAsync(writer, value, info);
        await writer.CompleteAsync();
        using var input = new MemoryStream(output.ToArray());
        var reader = PipeReader.Create(input);
        object? pipeCopy = await JsonSerializer.DeserializeAsync(reader, info);
        await reader.CompleteAsync();
        string json = Encoding.UTF8.GetString(output.ToArray());
        DemoCheck.Expect("metadata.untyped-async", json == Json && copy is AdvancedPayload { Name: "Ada" } &&
            pipeCopy is AdvancedPayload { Name: "Ada" }, json);
    }

    private static void ReaderWriter()
    {
        var info = AdvancedJsonContext.Default.AdvancedPayload;
        byte[] bytes = Encoding.UTF8.GetBytes(Json);
        var reader = new Utf8JsonReader(bytes);
        var value = JsonSerializer.Deserialize(ref reader, info);
        AdvancedPayload result = value ?? throw new InvalidOperationException("Missing JSON value.");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) JsonSerializer.Serialize(writer, result, info);
        string json = Encoding.UTF8.GetString(stream.ToArray());
        DemoCheck.Expect("metadata.reader-writer", value?.Name == "Ada" && json == Json, json);
    }

    private static void UntypedReaderWriter()
    {
        System.Text.Json.Serialization.Metadata.JsonTypeInfo info = AdvancedJsonContext.Default.AdvancedPayload;
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(Json));
        object? value = JsonSerializer.Deserialize(ref reader, info);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) JsonSerializer.Serialize(writer, value, info);
        string json = Encoding.UTF8.GetString(stream.ToArray());
        DemoCheck.Expect("metadata.untyped-reader-writer", value is AdvancedPayload { Name: "Ada" } && json == Json, json);
    }

    private static async Task Streams()
    {
        var info = AdvancedJsonContext.Default.AdvancedPayload;
        using var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(stream, new AdvancedPayload { Name = "Ada" }, info);
        stream.Position = 0;
        var copy = await JsonSerializer.DeserializeAsync(stream, info);
        string json = Encoding.UTF8.GetString(stream.ToArray());
        DemoCheck.Expect("metadata.stream-async", copy?.Name == "Ada" && json == Json, json);
        using var pipeStream = new MemoryStream(Encoding.UTF8.GetBytes(Json));
        var reader = PipeReader.Create(pipeStream);
        var pipeCopy = await JsonSerializer.DeserializeAsync(reader, info);
        await reader.CompleteAsync();
        DemoCheck.Expect("metadata.pipe-async", pipeCopy?.Name == "Ada", pipeCopy?.Name ?? "null");
        using var outputStream = new MemoryStream();
        var writer = PipeWriter.Create(outputStream);
        await JsonSerializer.SerializeAsync(writer, new AdvancedPayload { Name = "Ada" }, info);
        await writer.CompleteAsync();
        string pipeJson = Encoding.UTF8.GetString(outputStream.ToArray());
        DemoCheck.Expect("metadata.pipe-write-async", pipeJson == Json, pipeJson);
    }

    private static async Task ArraySequence()
    {
        var info = AdvancedJsonContext.Default.AdvancedPayload;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("[" + Json + "," + Json + "]"));
        var names = new List<string>();
        await foreach (AdvancedPayload? item in JsonSerializer.DeserializeAsyncEnumerable(stream, info))
            names.Add(item?.Name ?? "null");
        DemoCheck.Expect("metadata.async-sequence", names.SequenceEqual(["Ada", "Ada"]), string.Join(',', names));
    }

    private static async Task TopLevelPipe()
    {
        var info = AdvancedJsonContext.Default.AdvancedPayload;
        using var topStream = new MemoryStream(Encoding.UTF8.GetBytes(Json + " " + Json));
        var reader = PipeReader.Create(topStream);
        var topNames = new List<string>();
        await foreach (AdvancedPayload? item in JsonSerializer.DeserializeAsyncEnumerable(reader, info, true))
            topNames.Add(item?.Name ?? "null");
        await reader.CompleteAsync();
        DemoCheck.Expect("metadata.pipe-top-level-sequence", topNames.SequenceEqual(["Ada", "Ada"]),
            string.Join(',', topNames));
    }

    private static async Task TopLevelStream()
    {
        var info = AdvancedJsonContext.Default.AdvancedPayload;
        using var topOnly = new MemoryStream(Encoding.UTF8.GetBytes(Json + " " + Json));
        int count = 0;
        await foreach (AdvancedPayload? item in JsonSerializer.DeserializeAsyncEnumerable(topOnly, info, true))
            if (item?.Name == "Ada") count++;
        DemoCheck.Expect("metadata.stream-top-level-sequence", count == 2, $"count={count}");
    }

    private static async Task ArrayPipe()
    {
        var info = AdvancedJsonContext.Default.AdvancedPayload;
        using var arrayStream = new MemoryStream(Encoding.UTF8.GetBytes("[" + Json + "]"));
        var arrayReader = PipeReader.Create(arrayStream);
        int arrayCount = 0;
        await foreach (AdvancedPayload? item in JsonSerializer.DeserializeAsyncEnumerable(arrayReader, info))
            if (item?.Name == "Ada") arrayCount++;
        await arrayReader.CompleteAsync();
        DemoCheck.Expect("metadata.pipe-array-sequence", arrayCount == 1, $"count={arrayCount}");
    }
}

using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

internal static class Program
{
    private const string Json = "{\"Name\":\"Ada\"}";
    private const string ArrayJson = "[" + Json + "]";
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes(Json);
    private static int failures;

    private static async Task Main()
    {
        Payload payload = new() { Name = "Ada" };
        object value = payload;
        JsonSerializerOptions options = new();
        JsonSerializerContext context = DemoContext.Default;
        JsonTypeInfo<Payload> typedInfo = DemoContext.Default.Payload;
        JsonTypeInfo info = typedInfo;
        using JsonDocument document = JsonDocument.Parse(Json);
        JsonElement element = document.RootElement;
        JsonNode node = JsonNode.Parse(Json)!;
        CheckJson("001", Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(value, info)));
        CheckJson("002", Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(value, typeof(Payload), options)));
        CheckJson("003", Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(value, typeof(Payload), context)));
        CheckJson("004", Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes<Payload>(payload, options)));
        CheckJson("005", Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes<Payload>(payload, typedInfo)));
        CheckPayload("006", JsonSerializer.Deserialize(Input(Json), info));
        CheckPayload("007", JsonSerializer.Deserialize(Input(Json), typeof(Payload), options));
        CheckPayload("008", JsonSerializer.Deserialize(Input(Json), typeof(Payload), context));
        CheckPayload("009", JsonSerializer.Deserialize(Bytes.AsSpan(), info));
        CheckPayload("010", JsonSerializer.Deserialize(Bytes.AsSpan(), typeof(Payload), options));
        CheckPayload("011", JsonSerializer.Deserialize(Bytes.AsSpan(), typeof(Payload), context));
        CheckPayload("012", JsonSerializer.Deserialize(Json.AsSpan(), info));
        CheckPayload("013", JsonSerializer.Deserialize(Json.AsSpan(), typeof(Payload), options));
        CheckPayload("014", JsonSerializer.Deserialize(Json.AsSpan(), typeof(Payload), context));
        CheckPayload("015", JsonSerializer.Deserialize(Json, info));
        CheckPayload("016", JsonSerializer.Deserialize(Json, typeof(Payload), options));
        CheckPayload("017", JsonSerializer.Deserialize(Json, typeof(Payload), context));
        CheckPayload("018", JsonSerializer.Deserialize(document, info));
        CheckPayload("019", JsonSerializer.Deserialize(document, typeof(Payload), options));
        CheckPayload("020", JsonSerializer.Deserialize(document, typeof(Payload), context));
        CheckPayload("021", JsonSerializer.Deserialize(element, info));
        CheckPayload("022", JsonSerializer.Deserialize(element, typeof(Payload), options));
        CheckPayload("023", JsonSerializer.Deserialize(element, typeof(Payload), context));
        CheckPayload("024", JsonSerializer.Deserialize(node, info));
        CheckPayload("025", JsonSerializer.Deserialize(node, typeof(Payload), options));
        CheckPayload("026", JsonSerializer.Deserialize(node, typeof(Payload), context));
        var reader26 = new Utf8JsonReader(Bytes);
        CheckPayload("027", JsonSerializer.Deserialize(ref reader26, info));
        var reader27 = new Utf8JsonReader(Bytes);
        CheckPayload("028", JsonSerializer.Deserialize(ref reader27, typeof(Payload), options));
        var reader28 = new Utf8JsonReader(Bytes);
        CheckPayload("029", JsonSerializer.Deserialize(ref reader28, typeof(Payload), context));
        CheckPayload("030", await JsonSerializer.DeserializeAsync(InputPipe(Json), info, CancellationToken.None));
        CheckPayload("031", await JsonSerializer.DeserializeAsync(InputPipe(Json), typeof(Payload), options, CancellationToken.None));
        CheckPayload("032", await JsonSerializer.DeserializeAsync(InputPipe(Json), typeof(Payload), context, CancellationToken.None));
        CheckPayload("033", await JsonSerializer.DeserializeAsync(Input(Json), info, CancellationToken.None));
        CheckPayload("034", await JsonSerializer.DeserializeAsync(Input(Json), typeof(Payload), options, CancellationToken.None));
        CheckPayload("035", await JsonSerializer.DeserializeAsync(Input(Json), typeof(Payload), context, CancellationToken.None));
        await CheckSequence("036", JsonSerializer.DeserializeAsyncEnumerable<Payload>(InputPipe(ArrayJson), false, options, CancellationToken.None));
        await CheckSequence("037", JsonSerializer.DeserializeAsyncEnumerable<Payload>(InputPipe(ArrayJson), options, CancellationToken.None));
        await CheckSequence("038", JsonSerializer.DeserializeAsyncEnumerable<Payload>(InputPipe(ArrayJson), typedInfo, false, CancellationToken.None));
        await CheckSequence("039", JsonSerializer.DeserializeAsyncEnumerable<Payload>(InputPipe(ArrayJson), typedInfo, CancellationToken.None));
        await CheckSequence("040", JsonSerializer.DeserializeAsyncEnumerable<Payload>(Input(ArrayJson), false, options, CancellationToken.None));
        await CheckSequence("041", JsonSerializer.DeserializeAsyncEnumerable<Payload>(Input(ArrayJson), options, CancellationToken.None));
        await CheckSequence("042", JsonSerializer.DeserializeAsyncEnumerable<Payload>(Input(ArrayJson), typedInfo, false, CancellationToken.None));
        await CheckSequence("043", JsonSerializer.DeserializeAsyncEnumerable<Payload>(Input(ArrayJson), typedInfo, CancellationToken.None));
        CheckPayload("044", await JsonSerializer.DeserializeAsync<Payload>(InputPipe(Json), options, CancellationToken.None));
        CheckPayload("045", await JsonSerializer.DeserializeAsync<Payload>(InputPipe(Json), typedInfo, CancellationToken.None));
        CheckPayload("046", await JsonSerializer.DeserializeAsync<Payload>(Input(Json), options, CancellationToken.None));
        CheckPayload("047", await JsonSerializer.DeserializeAsync<Payload>(Input(Json), typedInfo, CancellationToken.None));
        CheckPayload("048", JsonSerializer.Deserialize<Payload>(Input(Json), options));
        CheckPayload("049", JsonSerializer.Deserialize<Payload>(Input(Json), typedInfo));
        CheckPayload("050", JsonSerializer.Deserialize<Payload>(Bytes.AsSpan(), options));
        CheckPayload("051", JsonSerializer.Deserialize<Payload>(Bytes.AsSpan(), typedInfo));
        CheckPayload("052", JsonSerializer.Deserialize<Payload>(Json.AsSpan(), options));
        CheckPayload("053", JsonSerializer.Deserialize<Payload>(Json.AsSpan(), typedInfo));
        CheckPayload("054", JsonSerializer.Deserialize<Payload>(Json, options));
        CheckPayload("055", JsonSerializer.Deserialize<Payload>(Json, typedInfo));
        CheckPayload("056", JsonSerializer.Deserialize<Payload>(document, options));
        CheckPayload("057", JsonSerializer.Deserialize<Payload>(document, typedInfo));
        CheckPayload("058", JsonSerializer.Deserialize<Payload>(element, options));
        CheckPayload("059", JsonSerializer.Deserialize<Payload>(element, typedInfo));
        CheckPayload("060", JsonSerializer.Deserialize<Payload>(node, options));
        CheckPayload("061", JsonSerializer.Deserialize<Payload>(node, typedInfo));
        var reader61 = new Utf8JsonReader(Bytes);
        CheckPayload("062", JsonSerializer.Deserialize<Payload>(ref reader61, options));
        var reader62 = new Utf8JsonReader(Bytes);
        CheckPayload("063", JsonSerializer.Deserialize<Payload>(ref reader62, typedInfo));
        CheckStream("064", output => JsonSerializer.Serialize(output, value, info));
        CheckStream("065", output => JsonSerializer.Serialize(output, value, typeof(Payload), options));
        CheckStream("066", output => JsonSerializer.Serialize(output, value, typeof(Payload), context));
        CheckJson("067", JsonSerializer.Serialize(value, info));
        CheckJson("068", JsonSerializer.Serialize(value, typeof(Payload), options));
        CheckJson("069", JsonSerializer.Serialize(value, typeof(Payload), context));
        CheckWriter("070", output => JsonSerializer.Serialize(output, value, info));
        CheckWriter("071", output => JsonSerializer.Serialize(output, value, typeof(Payload), options));
        CheckWriter("072", output => JsonSerializer.Serialize(output, value, typeof(Payload), context));
        await CheckPipeAsync("073", async output => await JsonSerializer.SerializeAsync(output, value, info, CancellationToken.None));
        await CheckPipeAsync("074", async output => await JsonSerializer.SerializeAsync(output, value, typeof(Payload), options, CancellationToken.None));
        await CheckPipeAsync("075", async output => await JsonSerializer.SerializeAsync(output, value, typeof(Payload), context, CancellationToken.None));
        await CheckStreamAsync("076", async output => await JsonSerializer.SerializeAsync(output, value, info, CancellationToken.None));
        await CheckStreamAsync("077", async output => await JsonSerializer.SerializeAsync(output, value, typeof(Payload), options, CancellationToken.None));
        await CheckStreamAsync("078", async output => await JsonSerializer.SerializeAsync(output, value, typeof(Payload), context, CancellationToken.None));
        await CheckPipeAsync("079", async output => await JsonSerializer.SerializeAsync<Payload>(output, payload, options, CancellationToken.None));
        await CheckPipeAsync("080", async output => await JsonSerializer.SerializeAsync<Payload>(output, payload, typedInfo, CancellationToken.None));
        await CheckStreamAsync("081", async output => await JsonSerializer.SerializeAsync<Payload>(output, payload, options, CancellationToken.None));
        await CheckStreamAsync("082", async output => await JsonSerializer.SerializeAsync<Payload>(output, payload, typedInfo, CancellationToken.None));
        CheckDocument("083", JsonSerializer.SerializeToDocument(value, info));
        CheckDocument("084", JsonSerializer.SerializeToDocument(value, typeof(Payload), options));
        CheckDocument("085", JsonSerializer.SerializeToDocument(value, typeof(Payload), context));
        CheckDocument("086", JsonSerializer.SerializeToDocument<Payload>(payload, options));
        CheckDocument("087", JsonSerializer.SerializeToDocument<Payload>(payload, typedInfo));
        CheckJson("088", JsonSerializer.SerializeToElement(value, info).GetRawText());
        CheckJson("089", JsonSerializer.SerializeToElement(value, typeof(Payload), options).GetRawText());
        CheckJson("090", JsonSerializer.SerializeToElement(value, typeof(Payload), context).GetRawText());
        CheckJson("091", JsonSerializer.SerializeToElement<Payload>(payload, options).GetRawText());
        CheckJson("092", JsonSerializer.SerializeToElement<Payload>(payload, typedInfo).GetRawText());
        CheckJson("093", JsonSerializer.SerializeToNode(value, info)?.ToJsonString());
        CheckJson("094", JsonSerializer.SerializeToNode(value, typeof(Payload), options)?.ToJsonString());
        CheckJson("095", JsonSerializer.SerializeToNode(value, typeof(Payload), context)?.ToJsonString());
        CheckJson("096", JsonSerializer.SerializeToNode<Payload>(payload, options)?.ToJsonString());
        CheckJson("097", JsonSerializer.SerializeToNode<Payload>(payload, typedInfo)?.ToJsonString());
        CheckStream("098", output => JsonSerializer.Serialize<Payload>(output, payload, options));
        CheckStream("099", output => JsonSerializer.Serialize<Payload>(output, payload, typedInfo));
        CheckWriter("100", output => JsonSerializer.Serialize<Payload>(output, payload, options));
        CheckWriter("101", output => JsonSerializer.Serialize<Payload>(output, payload, typedInfo));
        CheckJson("102", JsonSerializer.Serialize<Payload>(payload, options));
        CheckJson("103", JsonSerializer.Serialize<Payload>(payload, typedInfo));

        var entry = new DemoEntry(DemoAction.Created);
        Report("104", $"Action: {entry.Action}" == "Action: Created" && $"{entry.Action,10}" == "   Created");
        Console.WriteLine(failures == 0 ? "JSON_DEMO_PASS" : $"JSON_DEMO_FAIL {failures}");
        if (failures != 0) Environment.ExitCode = 1;
    }

    private static MemoryStream Input(string json) => new(Encoding.UTF8.GetBytes(json));
    private static PipeReader InputPipe(string json) => PipeReader.Create(Input(json));

    private static void CheckPayload(string id, object? result) =>
        Report(id, result is Payload { Name: "Ada" });

    private static void CheckJson(string id, string? json)
    {
        using JsonDocument result = JsonDocument.Parse(json ?? "null");
        Report(id, result.RootElement.ValueKind == JsonValueKind.Object &&
            result.RootElement.TryGetProperty("Name", out var name) && name.GetString() == "Ada");
    }

    private static void CheckDocument(string id, JsonDocument result)
    {
        using (result) CheckJson(id, result.RootElement.GetRawText());
    }

    private static void CheckStream(string id, Action<Stream> write)
    {
        using var stream = new MemoryStream();
        write(stream);
        CheckJson(id, Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static async Task CheckStreamAsync(string id, Func<Stream, Task> write)
    {
        using var stream = new MemoryStream();
        await write(stream);
        CheckJson(id, Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static void CheckWriter(string id, Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) write(writer);
        CheckJson(id, Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static async Task CheckPipeAsync(string id, Func<PipeWriter, Task> write)
    {
        using var stream = new MemoryStream();
        PipeWriter writer = PipeWriter.Create(stream);
        await write(writer);
        await writer.CompleteAsync();
        CheckJson(id, Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static async Task CheckSequence(string id, IAsyncEnumerable<Payload?> sequence)
    {
        int count = 0;
        bool valid = true;
        await foreach (Payload? item in sequence)
        {
            count++;
            valid &= item is { Name: "Ada" };
        }
        Report(id, valid && count == 1);
    }

    private static void Report(string id, bool passed)
    {
        Console.WriteLine($"{id}: {(passed ? "PASS" : "FAIL")}");
        if (!passed) failures++;
    }
}

public sealed class Payload
{
    public string Name { get; set; } = "";
}

public enum DemoAction { Created }
public sealed record DemoEntry(DemoAction Action);

[JsonSerializable(typeof(Payload))]
internal partial class DemoContext : JsonSerializerContext;

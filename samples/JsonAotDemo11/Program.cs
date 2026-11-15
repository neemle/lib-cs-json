using System.IO.Pipelines;
using System.Numerics;
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

        await CheckNet11();
        var entry = new DemoEntry(DemoAction.Created);
        Report("137", JsonSerializer.Deserialize<DemoEntry>(JsonSerializer.Serialize(entry))?.Action == DemoAction.Created);
        object boxed = payload;
        Type runtimeType = typeof(Payload);
        string dynamicJson = JsonSerializer.Serialize(boxed, runtimeType);
        Report("138", JsonSerializer.Deserialize(dynamicJson, runtimeType) is Payload { Name: "Ada" } &&
            JsonSerializer.Serialize(boxed) == dynamicJson);
        var converterOptions = new JsonSerializerOptions();
        converterOptions.Converters.Add(new UpperConverter());
        string converted = JsonSerializer.Serialize("hello", converterOptions);
        Report("139", converted == "\"HELLO\"" && JsonSerializer.Deserialize<string>(converted, converterOptions) == "hello");
        var enumOptions = new JsonSerializerOptions();
        JsonStringEnumConverter enumConverter = new(JsonNamingPolicy.CamelCase, false);
        enumOptions.Converters.Add(enumConverter);
        string enumJson = JsonSerializer.Serialize(DemoAction.Created, enumOptions);
        Report("140", enumJson == "\"created\"" &&
            JsonSerializer.Deserialize<DemoAction>(enumJson, enumOptions) == DemoAction.Created);
        Console.WriteLine(failures == 0 ? "JSON_DEMO_PASS" : $"JSON_DEMO_FAIL {failures}");
        if (failures != 0) Environment.ExitCode = 1;
    }

    private static async Task CheckNet11()
    {
        Report("104", JsonSerializer.Deserialize<BFloat16>(JsonSerializer.Serialize(BFloat16.One)) == BFloat16.One);
        Report("105", JsonSerializer.Deserialize<Decimal32>(JsonSerializer.Serialize(Decimal32.One)) == Decimal32.One);
        Report("106", JsonSerializer.Deserialize<Decimal64>(JsonSerializer.Serialize(Decimal64.One)) == Decimal64.One);
        Report("107", JsonSerializer.Deserialize<Decimal128>(JsonSerializer.Serialize(Decimal128.One)) == Decimal128.One);
        Reading value = new("hello");
        var copy = JsonSerializer.Deserialize<Reading>(JsonSerializer.Serialize(value));
        Report("108", copy.Value is string text && text == "hello");
        using var stream = new MemoryStream();
        await JsonSerializer.SerializeAsyncEnumerable(stream, Numbers(), topLevelValues: true);
        Report("109", Encoding.UTF8.GetString(stream.ToArray()) == "1\n2\n");
        var set = JsonSerializer.Deserialize<IReadOnlySet<int>>("[1,2]");
        Report("110", set is not null && set.SetEquals([1, 2]));
        var dog = JsonSerializer.Deserialize<Pet>("{\"Name\":\"Rex\",\"Breed\":\"Collie\"}");
        Report("111", dog.Value is Dog { Name: "Rex", Breed: "Collie" });
        Reading number = new(42);
        Report("112", JsonSerializer.Deserialize<Reading>(JsonSerializer.Serialize(number)).Value is 42);
        var keys = new Dictionary<Decimal32, string> { [Decimal32.One] = "one" };
        Report("113", JsonSerializer.Deserialize<Dictionary<Decimal32, string>>(JsonSerializer.Serialize(keys))?[Decimal32.One] == "one");
        var named = new NamedEvent { EventName = "Launch", ReleaseVersion = "11" };
        string namedJson = JsonSerializer.Serialize(named);
        Report("114", namedJson == "{\"eventName\":\"Launch\",\"release_version\":\"11\"}");
        using var pipeStream = new MemoryStream();
        PipeWriter pipe = PipeWriter.Create(pipeStream);
        await JsonSerializer.SerializeAsyncEnumerable(pipe, Numbers(), topLevelValues: true, options: new JsonSerializerOptions());
        await pipe.CompleteAsync();
        Report("115", Encoding.UTF8.GetString(pipeStream.ToArray()) == "1\n2\n");
        using var typedStream = new MemoryStream();
        await JsonSerializer.SerializeAsyncEnumerable(typedStream, Numbers(), topLevelValues: true, jsonTypeInfo: DemoContext.Default.Int32);
        Report("116", Encoding.UTF8.GetString(typedStream.ToArray()) == "1\n2\n");
        using var typedPipeStream = new MemoryStream();
        PipeWriter typedPipe = PipeWriter.Create(typedPipeStream);
        await JsonSerializer.SerializeAsyncEnumerable(typedPipe, Numbers(), topLevelValues: false, jsonTypeInfo: DemoContext.Default.Int32);
        await typedPipe.CompleteAsync();
        Report("117", Encoding.UTF8.GetString(typedPipeStream.ToArray()) == "[1,2]");
        var extension = JsonSerializer.Deserialize<ExtraData>("{\"Name\":\"Ada\",\"age\":42}");
        Report("118", extension?.Extra is not null && extension.Extra["age"].GetInt32() == 42);
        Shape shape = new Circle(5);
        var shapeCopy = JsonSerializer.Deserialize<Shape>(JsonSerializer.Serialize(shape));
        Report("119", shapeCopy is Circle { Radius: 5 });
        GenericBase<int> generic = new GenericDerived<int> { Value = 7 };
        var genericCopy = JsonSerializer.Deserialize<GenericBase<int>>(JsonSerializer.Serialize(generic));
        Report("120", genericCopy is GenericDerived<int> { Value: 7 });
        var metadataOptions = new JsonSerializerOptions { TypeInfoResolver = DemoContext.Default };
        metadataOptions.MakeReadOnly();
        Report("121", metadataOptions.GetTypeInfo<Payload>() is not null &&
            metadataOptions.TryGetTypeInfo<Payload>(out var metadata) && metadata is not null);
        var optional = JsonSerializer.Deserialize<Option<int>>(JsonSerializer.Serialize(new Option<int>(7)));
        Report("122", optional.Value == 7);
        var hidden = JsonSerializer.Deserialize<HiddenData>("{\"Name\":\"Ada\"}");
        Report("123", hidden?.ReadName() == "Ada");
        var defaults = JsonSerializer.Deserialize<Defaults>("{}");
        Report("124", defaults?.Name == "default");
        var objectExtra = JsonSerializer.Deserialize<ObjectExtra>("{\"flag\":true}");
        Report("125", objectExtra?.Extra?["flag"]?.GetValue<bool>() == true);
        var numberOptions = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        var infinity = JsonSerializer.Deserialize<Decimal64>(JsonSerializer.Serialize(Decimal64.PositiveInfinity, numberOptions), numberOptions);
        Report("126", Decimal64.IsPositiveInfinity(infinity));
        var bfloatKeys = new Dictionary<BFloat16, string> { [BFloat16.One] = "one" };
        var decimal64Keys = new Dictionary<Decimal64, string> { [Decimal64.One] = "one" };
        var decimal128Keys = new Dictionary<Decimal128, string> { [Decimal128.One] = "one" };
        Report("127", JsonSerializer.Deserialize<Dictionary<BFloat16, string>>(JsonSerializer.Serialize(bfloatKeys))?[BFloat16.One] == "one");
        Report("128", JsonSerializer.Deserialize<Dictionary<Decimal64, string>>(JsonSerializer.Serialize(decimal64Keys))?[Decimal64.One] == "one");
        Report("129", JsonSerializer.Deserialize<Dictionary<Decimal128, string>>(JsonSerializer.Serialize(decimal128Keys))?[Decimal128.One] == "one");
        var bfloatNan = JsonSerializer.Deserialize<BFloat16>(JsonSerializer.Serialize(BFloat16.NaN, numberOptions), numberOptions);
        var decimal32Nan = JsonSerializer.Deserialize<Decimal32>(JsonSerializer.Serialize(Decimal32.NaN, numberOptions), numberOptions);
        var decimal128Nan = JsonSerializer.Deserialize<Decimal128>(JsonSerializer.Serialize(Decimal128.NaN, numberOptions), numberOptions);
        Report("130", BFloat16.IsNaN(bfloatNan) && Decimal32.IsNaN(decimal32Nan) && Decimal128.IsNaN(decimal128Nan));
        var inValue = JsonSerializer.Deserialize<InConstructor>("{\"Value\":7}");
        var refValue = JsonSerializer.Deserialize<RefConstructor>("{\"Value\":8}");
        var outValue = JsonSerializer.Deserialize<OutConstructor>("{\"Value\":9}");
        Report("131", inValue?.Value == 7 && refValue?.Value == 8 && outValue?.Value == 9);
        var objectExtraJson = JsonSerializer.Serialize(objectExtra);
        Report("132", JsonNode.Parse(objectExtraJson)?["flag"]?.GetValue<bool>() == true);
        var extensionJson = JsonSerializer.Serialize(extension);
        Report("133", JsonNode.Parse(extensionJson)?["age"]?.GetValue<int>() == 42);
        var readOnlyRef = JsonSerializer.Deserialize<RefReadonlyConstructor>("{\"Value\":10}");
        var privateReadOnlyRef = JsonSerializer.Deserialize<PrivateRefReadonlyConstructor>("{\"Value\":11}");
        Report("134", readOnlyRef?.Value == 10 && privateReadOnlyRef?.Value == 11);
        var classifierOptions = new JsonSerializerOptions();
        classifierOptions.TypeClassifiers.Add(new JsonUnionTypeStructuralClassifier());
        var classified = JsonSerializer.Deserialize<GlobalPet>("{\"Name\":\"Misty\",\"Lives\":9}", classifierOptions);
        string classifiedJson = JsonSerializer.Serialize(classified, classifierOptions);
        Report("135", classified.Value is Cat { Name: "Misty", Lives: 9 } &&
            JsonSerializer.Deserialize<GlobalPet>(classifiedJson, classifierOptions).Value is Cat { Lives: 9 });
        bool ambiguousRejected = false;
        try { _ = JsonSerializer.Deserialize<GlobalPet>("{\"Name\":\"Misty\",\"Lives\":9}"); }
        catch (InvalidOperationException) { ambiguousRejected = true; }
        catch (NotSupportedException) { ambiguousRejected = true; }
        catch (JsonException) { ambiguousRejected = true; }
        Report("136", ambiguousRejected);
    }

    private static async IAsyncEnumerable<int> Numbers()
    {
        yield return 1;
        await Task.Yield();
        yield return 2;
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
public sealed class UpperConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString()?.ToLowerInvariant();
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUpperInvariant());
}

[JsonSerializable(typeof(Payload))]
[JsonSerializable(typeof(int))]
internal partial class DemoContext : JsonSerializerContext;

public union Reading(int, string);

[JsonUnion(TypeClassifier = typeof(JsonUnionTypeStructuralClassifier))]
public union Pet(Dog, Cat);
public union GlobalPet(Dog, Cat);
public sealed record Dog(string Name, string Breed);
public sealed record Cat(string Name, int Lives);

[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
[JsonNamingPolicy(JsonKnownNamingPolicy.SnakeCaseLower)]
public sealed class NamedEvent
{
    [JsonNamingPolicy(JsonKnownNamingPolicy.CamelCase)]
    public string EventName { get; set; } = "";
    public string ReleaseVersion { get; set; } = "";
    public string? Notes { get; set; }
}

public sealed class ExtraData
{
    public string Name { get; set; } = "";
    [JsonExtensionData]
    public IReadOnlyDictionary<string, JsonElement>? Extra { get; set; }
}

[JsonPolymorphic(InferClosedTypePolymorphism = true)]
public closed record Shape;
public sealed record Circle(int Radius) : Shape;
public sealed record Square(int Length) : Shape;

[JsonDerivedType(typeof(GenericDerived<>), "derived")]
public class GenericBase<T> { public T? Value { get; set; } }
public sealed class GenericDerived<T> : GenericBase<T>;

[JsonConverter(typeof(OptionConverter<>))]
public readonly record struct Option<T>(T Value);

public sealed class OptionConverter<T> : JsonConverter<Option<T>>
{
    public override Option<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(JsonSerializer.Deserialize<T>(ref reader, options)!);
    public override void Write(Utf8JsonWriter writer, Option<T> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.Value, options);
}

public sealed class HiddenData
{
    [JsonInclude]
    private string Name { get; set; } = "";
    [JsonConstructor]
    private HiddenData() { }
    public string ReadName() => Name;
}

public sealed class Defaults { public string Name { get; init; } = "default"; }
public sealed class ObjectExtra { [JsonExtensionData] public JsonObject? Extra { get; set; } }

public sealed class InConstructor
{
    public int Value { get; }
    [JsonConstructor]
    public InConstructor(in int value) => Value = value;
}

public sealed class RefConstructor
{
    public int Value { get; }
    [JsonConstructor]
    public RefConstructor(ref int value) => Value = value;
}

public sealed class OutConstructor
{
    public int Value { get; set; }
    [JsonConstructor]
    public OutConstructor(out int value) { value = 0; }
}

public sealed class RefReadonlyConstructor
{
    public int Value { get; }
    [JsonConstructor]
    public RefReadonlyConstructor(ref readonly int value) => Value = value;
}

public sealed class PrivateRefReadonlyConstructor
{
    public int Value { get; }
    [JsonConstructor]
    private PrivateRefReadonlyConstructor(ref readonly int value) => Value = value;
}

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using System.Text.Json.Serialization.Metadata;

internal static class DemoExceptionCases
{
    internal static void Run()
    {
        (string Name, Action Run)[] scenarios =
        [
            ("invalid-json", InvalidJson), ("invalid-arguments", InvalidArguments),
            ("missing-metadata", MissingMetadata), ("converter-exceptions", ConverterExceptions),
            ("invalid-factory", InvalidFactory), ("invalid-references", InvalidReferences),
            ("element-exceptions", ElementExceptions), ("reader-writer-exceptions", ReaderWriterExceptions),
            ("option-exceptions", OptionExceptions), ("stream-exceptions", StreamExceptions),
            ("populate-exceptions", PopulateExceptions), ("populate-contract-exceptions", PopulateContractExceptions),
            ("unsupported-dictionary-key", UnsupportedDictionaryKey), ("invalid-contracts", InvalidContracts),
            ("metadata-type-mismatch", MetadataTypeMismatch), ("unknown-derived-type", UnknownDerivedType),
            ("metadata-configuration", MetadataConfigurationErrors), ("unsupported-values", UnsupportedValues)
        ];
        foreach (var (name, run) in scenarios) DemoCheck.Run(name, run);
    }

    private static void InvalidJson()
    {
        try
        {
            _ = JsonSerializer.Deserialize<ErrorEnvelope>("{\n\"Items\":[1,\"bad\"]}");
            DemoCheck.Expect("error.nested-path", false, "accepted");
        }
        catch (JsonException error)
        {
            DemoCheck.Expect("error.nested-path", error.Path == "$.Items[1]" && error.LineNumber == 1 &&
                error.BytePositionInLine is not null && error.Message.Contains("$.Items[1]", StringComparison.Ordinal) &&
                error.Message.Contains("System.Int32", StringComparison.Ordinal),
                error.Path ?? "null");
        }
        bool incomplete = false;
        try { _ = JsonSerializer.Deserialize<int[]>("[1,"); }
        catch (JsonException) { incomplete = true; }
        DemoCheck.Expect("error.incomplete-array", incomplete, incomplete ? "rejected" : "accepted");
        bool extra = false;
        try { _ = JsonSerializer.Deserialize<int>("1 2"); }
        catch (JsonException) { extra = true; }
        DemoCheck.Expect("error.trailing-content", extra, extra ? "rejected" : "accepted");
        bool date = false;
        try { _ = JsonSerializer.Deserialize<DateTime>("\"not-a-date\""); }
        catch (JsonException) { date = true; }
        bool escape = false;
        try { _ = JsonSerializer.Deserialize<string>("\"\\q\""); }
        catch (JsonException) { escape = true; }
        bool overflow = false;
        try { _ = JsonSerializer.Deserialize<byte>("256"); }
        catch (JsonException) { overflow = true; }
        bool utf8 = false;
        try { _ = JsonSerializer.Deserialize<string>(new byte[] { 0xff }); }
        catch (JsonException) { utf8 = true; }
        bool bom = false;
        try { _ = JsonSerializer.Deserialize<int>(new byte[] { 0xef, 0xbb, 0xbf, (byte)'1' }); }
        catch (JsonException) { bom = true; }
        DemoCheck.Expect("error.invalid-scalars", date && escape && overflow && utf8 && bom,
            date && escape && overflow && utf8 && bom ? "rejected" : "accepted");
    }

    private static void InvalidArguments()
    {
        bool nullInput = false;
        try { _ = JsonSerializer.Deserialize<int>((string)null!); }
        catch (ArgumentNullException) { nullInput = true; }
        DemoCheck.Expect("error.null-input", nullInput, nullInput ? "rejected" : "accepted");
        bool wrongType = false;
        try { _ = JsonSerializer.Serialize("text", typeof(int)); }
        catch (ArgumentException) { wrongType = true; }
        DemoCheck.Expect("error.incompatible-input-type", wrongType, wrongType ? "rejected" : "accepted");
        bool nullWriter = false;
        try { JsonSerializer.Serialize((Utf8JsonWriter)null!, 1); }
        catch (ArgumentNullException) { nullWriter = true; }
        DemoCheck.Expect("error.null-writer", nullWriter, nullWriter ? "rejected" : "accepted");
    }

    private static void MissingMetadata()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = AdvancedJsonContext.Default };
        bool rejected = false;
        try { _ = JsonSerializer.Serialize(new ErrorEnvelope(), options); }
        catch (NotSupportedException) { rejected = true; }
        DemoCheck.Expect("error.missing-type-info", rejected, rejected ? "rejected" : "accepted");
    }

    private static void ConverterExceptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new ThrowingValueConverter());
        bool readPath = false;
        try { _ = JsonSerializer.Deserialize<ThrowEnvelope>("{\"Value\":1}", options); }
        catch (JsonException error)
        {
            readPath = error.Path == "$.Value" && error.InnerException is FormatException &&
                error.Message.Contains("decoder refused", StringComparison.Ordinal);
        }
        DemoCheck.Expect("error.converter-read-path", readPath, readPath ? "$.Value" : "wrong path");
        bool writePath = false;
        try { _ = JsonSerializer.Serialize(new ThrowEnvelope { Value = new ThrowValue() }, options); }
        catch (JsonException error)
        {
            writePath = error.Path == "$.Value" && error.InnerException is InvalidOperationException &&
                error.Message.Contains("writer refused", StringComparison.Ordinal);
        }
        DemoCheck.Expect("error.converter-write-path", writePath, writePath ? "$.Value" : "wrong path");
    }

    private static void InvalidFactory()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new InvalidConverterFactory());
        bool rejected = false;
        try { _ = JsonSerializer.Serialize(new InvalidFactoryValue(), options); }
        catch (InvalidOperationException) { rejected = true; }
        DemoCheck.Expect("error.converter-factory-null", rejected, rejected ? "rejected" : "accepted");
        var mismatched = new JsonSerializerOptions();
        mismatched.Converters.Add(new MismatchedConverterFactory());
        Reject<InvalidOperationException>("error.converter-factory-mismatch", () =>
            JsonSerializer.Serialize(new InvalidFactoryValue(), mismatched));
    }

    private static void InvalidReferences()
    {
        var options = new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.Preserve };
        string[] inputs = ["{\"$ref\":\"missing\"}", "{\"$ref\":\"1\",\"Next\":null}",
            "{\"$id\":1,\"Next\":null}", "{\"$id\":\"1\",\"Next\":{\"$id\":\"1\"}}"];
        for (int i = 0; i < inputs.Length; i++)
        {
            bool rejected = false;
            try { _ = JsonSerializer.Deserialize<CycleModel>(inputs[i], options); }
            catch (JsonException) { rejected = true; }
            DemoCheck.Expect("error.reference-metadata." + i, rejected, rejected ? "rejected" : "accepted");
        }
    }

    private static void ElementExceptions()
    {
        JsonDocument document = JsonDocument.Parse("{\"value\":\"text\"}");
        JsonElement root = document.RootElement;
        bool wrongKind = false;
        try { _ = root.GetProperty("value").GetInt32(); }
        catch (InvalidOperationException) { wrongKind = true; }
        bool missing = false;
        try { _ = root.GetProperty("missing"); }
        catch (KeyNotFoundException) { missing = true; }
        document.Dispose();
        bool disposed = false;
        try { _ = root.GetRawText(); }
        catch (ObjectDisposedException) { disposed = true; }
        DemoCheck.Expect("error.element-access", wrongKind && missing && disposed, disposed ? "rejected" : "accepted");
    }

    private static void ReaderWriterExceptions()
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes("2147483648"));
        reader.Read();
        bool format = false;
        try { _ = reader.GetInt32(); }
        catch (FormatException) { format = true; }
        using var output = new MemoryStream();
        using var writer = new Utf8JsonWriter(output);
        bool number = false;
        try { writer.WriteNumberValue(double.NaN); }
        catch (ArgumentException) { number = true; }
        DemoCheck.Expect("error.reader-writer", format && number, format && number ? "rejected" : "accepted");
    }

    private static void OptionExceptions()
    {
        Reject<ArgumentOutOfRangeException>("error.comment-option", () =>
            _ = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Allow });
        Reject<ArgumentOutOfRangeException>("error.indent-character", () =>
            _ = new JsonSerializerOptions { IndentCharacter = 'x' });
        Reject<ArgumentOutOfRangeException>("error.indent-size", () =>
            _ = new JsonSerializerOptions { IndentSize = -1 });
        Reject<ArgumentOutOfRangeException>("error.newline-option", () =>
            _ = new JsonSerializerOptions { NewLine = "x" });
        Reject<ArgumentException>("error.ignore-condition", () =>
            _ = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.Always });
        var naming = new JsonSerializerOptions { PropertyNamingPolicy = new NullNamingPolicy() };
        Reject<InvalidOperationException>("error.naming-policy-null", () =>
            JsonSerializer.Serialize(new NamingCaseModel(), naming));
    }

    private static void StreamExceptions()
    {
        using var input = new FailingJsonStream();
        Reject<IOException>("error.stream-read", () => JsonSerializer.Deserialize<AdvancedPayload>(input));
        using var output = new FailingJsonStream();
        Reject<IOException>("error.stream-write", () => JsonSerializer.Serialize(output, new AdvancedPayload()));
        using var empty = new MemoryStream();
        Reject<JsonException>("error.empty-stream", () => JsonSerializer.Deserialize<AdvancedPayload>(empty));
    }

    private static void PopulateExceptions()
    {
        var options = new JsonSerializerOptions
        {
            PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
            ReferenceHandler = ReferenceHandler.Preserve
        };
        bool rejected = false;
        try { _ = JsonSerializer.Deserialize<WritablePopulateModel>("{\"Items\":[1]}", options); }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException) { rejected = true; }
        DemoCheck.Expect("error.populate-with-preserve", rejected, rejected ? "rejected" : "accepted");
        var ignoreCycles = new JsonSerializerOptions
        {
            PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
            ReferenceHandler = ReferenceHandler.IgnoreCycles
        };
        Reject<InvalidOperationException>("error.populate-with-ignore-cycles", () =>
            JsonSerializer.Deserialize<WritablePopulateModel>("{\"Items\":[1]}", ignoreCycles));
    }

    private static void PopulateContractExceptions()
    {
        Reject<NotSupportedException>("error.populate-parameterized-constructor", () =>
            JsonSerializer.Deserialize<PopulateConstructorConflict>("{}"));
        Reject<InvalidOperationException>("error.populate-custom-converter", () =>
            JsonSerializer.Deserialize<PopulateConverterConflict>("{}"));
        Reject<InvalidOperationException>("error.populate-polymorphism", () =>
            JsonSerializer.Deserialize<PopulatePolymorphismConflict>("{}"));
        var options = new JsonSerializerOptions { IgnoreReadOnlyProperties = true };
        Reject<InvalidOperationException>("error.populate-ignore-readonly", () =>
            JsonSerializer.Deserialize<PopulateReadonlyConflict>("{}", options));
    }

    private static void UnsupportedDictionaryKey()
    {
        var values = new Dictionary<FactoryKey, int> { [new FactoryKey("a")] = 1 };
        Reject<NotSupportedException>("error.unsupported-dictionary-key", () => JsonSerializer.Serialize(values));
    }

    private static void InvalidContracts()
    {
        Reject<InvalidOperationException>("error.extension-constructor", () =>
            JsonSerializer.Deserialize<ExtensionConstructorConflict>("{}"));
        Reject<InvalidOperationException>("error.extension-type", () =>
            JsonSerializer.Deserialize<InvalidExtensionType>("{}"));
        Reject<InvalidOperationException>("error.duplicate-property-name", () =>
            JsonSerializer.Deserialize<DuplicatePropertyName>("{}"));
        Reject<InvalidOperationException>("error.multiple-json-constructors", () =>
            JsonSerializer.Deserialize<MultipleJsonConstructors>("{}"));
        Reject<InvalidOperationException>("error.ignore-null-value-type", () =>
            JsonSerializer.Serialize(new InvalidIgnoreNull()));
        Reject<InvalidOperationException>("error.extension-unmapped-conflict", () =>
            JsonSerializer.Deserialize<DisallowedExtensionModel>("{}"));
    }

    private static void MetadataTypeMismatch()
    {
        System.Text.Json.Serialization.Metadata.JsonTypeInfo info = AdvancedJsonContext.Default.AdvancedPayload;
        bool rejected = false;
        try { _ = JsonSerializer.Serialize(new ErrorEnvelope(), info); }
        catch (InvalidCastException error)
        {
            rejected = error.Message.Contains("ErrorEnvelope", StringComparison.Ordinal) &&
                error.Message.Contains("AdvancedPayload", StringComparison.Ordinal);
        }
        DemoCheck.Expect("error.metadata-type-mismatch", rejected, rejected ? "rejected" : "accepted");
    }

    private static void UnknownDerivedType()
    {
        KnownAnimal value = new UnregisteredAnimal { Name = "Ada" };
        bool rejected = false;
        try { _ = JsonSerializer.Serialize(value); }
        catch (NotSupportedException error)
        {
            rejected = error.Message.Contains("UnregisteredAnimal", StringComparison.Ordinal);
        }
        DemoCheck.Expect("error.unknown-derived-type", rejected, rejected ? "rejected" : "accepted");
    }

    private static void MetadataConfigurationErrors()
    {
        var resolver = AdvancedJsonContext.Default.WithAddedModifier(info =>
        {
            if (info.Type != typeof(AdvancedPayload)) return;
            foreach (JsonPropertyInfo property in info.Properties)
                if (property.Name == "Name") { property.IsRequired = true; property.Set = null; }
        });
        var options = new JsonSerializerOptions { TypeInfoResolver = resolver };
        Reject<InvalidOperationException>("error.required-property-without-setter", () =>
            JsonSerializer.Deserialize<AdvancedPayload>("{}", options));
        var noConstructor = AdvancedJsonContext.Default.WithAddedModifier(info =>
        {
            if (info.Type == typeof(AdvancedPayload)) info.CreateObject = null;
        });
        var noConstructorOptions = new JsonSerializerOptions { TypeInfoResolver = noConstructor };
        Reject<NotSupportedException>("error.metadata-without-constructor", () =>
            JsonSerializer.Deserialize<AdvancedPayload>("{}", noConstructorOptions));
    }

    private static void UnsupportedValues()
    {
        bool typeRejected = false;
        try { _ = JsonSerializer.Serialize<Type>(typeof(int)); }
        catch (NotSupportedException error)
        {
            typeRejected = error.Message.Contains("System.Type", StringComparison.Ordinal);
        }
        DemoCheck.Expect("error.system-type", typeRejected, typeRejected ? "rejected" : "accepted");
        Action action = static () => { };
        Reject<NotSupportedException>("error.delegate", () => JsonSerializer.Serialize(action));
    }

    private static void Reject<TException>(string name, Action action) where TException : Exception
    {
        bool rejected = false;
        try { action(); }
        catch (TException) { rejected = true; }
        DemoCheck.Expect(name, rejected, rejected ? "rejected" : "accepted");
    }
}

public sealed class ErrorEnvelope
{
    public List<int> Items { get; set; } = [];
}

public sealed record ExtensionConstructorConflict(
    [property: JsonExtensionData] Dictionary<string, JsonElement> Data);

public sealed class InvalidExtensionType
{
    [JsonExtensionData] public int Data { get; set; }
}

public sealed class DuplicatePropertyName
{
    [JsonPropertyName("same")] public int First { get; set; }
    [JsonPropertyName("same")] public int Second { get; set; }
}

public sealed class MultipleJsonConstructors
{
    [JsonConstructor] public MultipleJsonConstructors() { }
    [JsonConstructor] public MultipleJsonConstructors(int value) => Value = value;
    public int Value { get; }
}

public sealed class InvalidIgnoreNull
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int Value { get; set; }
}

[JsonPolymorphic(UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(RegisteredAnimal), "registered")]
public class KnownAnimal
{
    public string Name { get; set; } = "";
}

public sealed class RegisteredAnimal : KnownAnimal;
public sealed class UnregisteredAnimal : KnownAnimal;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DisallowedExtensionModel
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ThrowEnvelope
{
    public ThrowValue? Value { get; set; }
}

public sealed class PopulateConstructorConflict
{
    public PopulateConstructorConflict(int id) => Id = id;
    public int Id { get; }
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public List<int> Items { get; } = [1];
}

public sealed class PopulateConverterConflict
{
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    [JsonConverter(typeof(AttributeStringConverter))]
    public string Value { get; set; } = "";
}

public sealed class PopulatePolymorphismConflict
{
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Animal Value { get; set; } = new Dog();
}

public sealed class PopulateReadonlyConflict
{
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public List<int> Value { get; } = [1];
}

public sealed class ThrowValue;

public sealed class ThrowingValueConverter : JsonConverter<ThrowValue>
{
    public override ThrowValue? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new JsonException("decoder refused", new FormatException("invalid value"));

    public override void Write(Utf8JsonWriter writer, ThrowValue value, JsonSerializerOptions options) =>
        throw new JsonException("writer refused", new InvalidOperationException("invalid value"));
}

internal sealed class FailingJsonStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new IOException("read failed");
    public override void Write(byte[] buffer, int offset, int count) => throw new IOException("write failed");
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

internal sealed class NullNamingPolicy : JsonNamingPolicy
{
    public override string ConvertName(string name) => null!;
}

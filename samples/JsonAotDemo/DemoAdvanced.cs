using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

internal static class DemoAdvanced
{
    internal static async Task Run()
    {
        (string Name, Action Run)[] scenarios =
        [
            ("factory", ConverterFactory), ("factory-keys-null", FactoryKeysAndNull),
            ("factory-members", FactoryMembers), ("converter-collection", ConverterCollection),
            ("converter-initializer", ConverterInitializer), ("converter-nested-calls", ConverterNestedCalls),
            ("converter-nested-references", ConverterNestedReferences),
            ("converter-options-introspection", ConverterOptionsIntrospection),
            ("converter-options-across-methods", ConverterOptionsAcrossMethods),
            ("constructed-context", ContextMetadata), ("resolved-metadata", ResolvedMetadata),
            ("metadata-modifiers", MetadataModifiers), ("metadata-options", MetadataOptions),
            ("metadata-value", ExplicitValueMetadata), ("metadata-object", ExplicitObjectMetadata),
            ("metadata-methods", MetadataAcrossMethods), ("metadata-extended-context", ExtendedContext),
            ("metadata-fast-mode", ContextFastMode), ("metadata-factory-mode", ContextFactoryMode),
            ("metadata-mixed-mode", ContextMixedMode), ("metadata-add-property", MetadataAdditionalProperty),
            ("metadata-polymorphism", MetadataPolymorphism), ("metadata-callbacks", MetadataCallbacks),
            ("metadata-converter", MetadataConverter), ("metadata-policies", MetadataPropertyPolicies),
            ("advanced-errors", ErrorCases)
        ];
        foreach (var (name, run) in scenarios) DemoCheck.Run(name, run);
        await DemoCheck.RunAsync("nested-async", NestedAsync);
        await DemoCheck.RunAsync("nested-async-collections", NestedAsyncCollections);
        await DemoCheck.RunAsync("nested-async-failures", NestedAsyncFailures);
    }

    private static void ConverterFactory()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new FactoryValueConverterFactory());
        var value = new FactoryValue { Number = 7 };
        string json = JsonSerializer.Serialize(value, options);
        var copy = JsonSerializer.Deserialize<FactoryValue>(json, options);
        DemoCheck.Expect("factory.registered", json == "\"value:7\"" && copy?.Number == 7, json);
        string attributed = JsonSerializer.Serialize(new FactoryAttributeValue { Number = 8 });
        var attributedCopy = JsonSerializer.Deserialize<FactoryAttributeValue>(attributed);
        DemoCheck.Expect("factory.attribute", attributed == "\"attribute:8\"" && attributedCopy?.Number == 8,
            attributed);
    }

    private static void FactoryKeysAndNull()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new FactoryValueConverterFactory());
        var map = new Dictionary<FactoryKey, int> { [new FactoryKey("a")] = 3 };
        string mapJson = JsonSerializer.Serialize(map, options);
        var mapCopy = JsonSerializer.Deserialize<Dictionary<FactoryKey, int>>(mapJson, options);
        DemoCheck.Expect("factory.property-name", mapJson == "{\"key:a\":3}" &&
            mapCopy?[new FactoryKey("a")] == 3, mapJson);
        var nullValue = JsonSerializer.Deserialize<FactoryNullValue>("null", options);
        string encodedNull = JsonSerializer.Serialize<FactoryNullValue?>(null, options);
        DemoCheck.Expect("factory.handle-null", nullValue?.Text == "null" && encodedNull == "\"null\"", encodedNull);
    }

    private static void FactoryMembers()
    {
        var members = new FactoryMemberModel { Value = new FactoryValue { Number = 3 },
            Field = new FactoryValue { Number = 4 } };
        string membersJson = JsonSerializer.Serialize(members);
        var membersCopy = JsonSerializer.Deserialize<FactoryMemberModel>(membersJson);
        DemoCheck.Expect("factory.members", membersCopy is { Value.Number: 3, Field.Number: 4 } &&
            membersJson == "{\"Value\":\"value:3\",\"Field\":\"value:4\"}", membersJson);
        var customAttribute = new CustomAttributeModel { Value = new FactoryValue { Number = 5 } };
        string customJson = JsonSerializer.Serialize(customAttribute);
        var customCopy = JsonSerializer.Deserialize<CustomAttributeModel>(customJson);
        DemoCheck.Expect("factory.derived-converter-attribute", customJson == "{\"Value\":\"value:5\"}" &&
            customCopy?.Value.Number == 5, customJson);
    }

    private static void ConverterCollection()
    {
        var options = new JsonSerializerOptions();
        var unused = new FactoryValueConverter();
        options.Converters.Add(unused);
        options.Converters.Insert(0, new AlternateFactoryValueConverter());
        bool removed = options.Converters.Remove(unused);
        bool count = options.Converters.Count == 1;
        string json = JsonSerializer.Serialize(new FactoryValue { Number = 7 }, options);
        var copy = JsonSerializer.Deserialize<FactoryValue>(json, options);
        bool locked = false;
        try { options.Converters.Clear(); }
        catch (InvalidOperationException) { locked = true; }
        DemoCheck.Expect("converter.collection-operations", removed && count && locked && json == "\"alt:7\"" &&
            copy?.Number == 7, json);
    }

    private static void ConverterInitializer()
    {
        var options = new JsonSerializerOptions { Converters = { new FactoryValueConverterFactory() } };
        string json = JsonSerializer.Serialize(new FactoryValue { Number = 7 }, options);
        var copy = JsonSerializer.Deserialize<FactoryValue>(json, options);
        DemoCheck.Expect("converter.collection-initializer", json == "\"value:7\"" && copy?.Number == 7, json);
    }

    private static void ConverterNestedCalls()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new WrappedPayloadConverter());
        var value = new WrappedPayload { Value = new AdvancedPayload { Name = "Ada" } };
        string json = JsonSerializer.Serialize(value, options);
        var copy = JsonSerializer.Deserialize<WrappedPayload>(json, options);
        DemoCheck.Expect("converter.nested-serializer-calls", json == "{\"Name\":\"Ada\"}" &&
            copy?.Value.Name == "Ada", json);
    }

    private static void ConverterNestedReferences()
    {
        var options = new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.Preserve };
        options.Converters.Add(new WrappedPayloadConverter());
        string json = JsonSerializer.Serialize(new WrappedPayload { Value = new AdvancedPayload { Name = "Ada" } }, options);
        var copy = JsonSerializer.Deserialize<WrappedPayload>(json, options);
        DemoCheck.Expect("converter.nested-reference-scope", json == "{\"$id\":\"1\",\"Name\":\"Ada\"}" &&
            copy?.Value.Name == "Ada", json);
    }

    private static void ConverterOptionsIntrospection()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new ConverterCountConverter());
        string json = JsonSerializer.Serialize(new ConverterCountPayload { Count = 2 }, options);
        var copy = JsonSerializer.Deserialize<ConverterCountPayload>("2", options);
        DemoCheck.Expect("converter.options-collection", json == "3" && copy?.Count == 3, json);
    }

    private static void ConverterOptionsAcrossMethods()
    {
        JsonSerializerOptions options = CreateConverterOptions();
        string json = JsonSerializer.Serialize(new FactoryValue { Number = 7 }, options);
        var copy = JsonSerializer.Deserialize<FactoryValue>(json, options);
        DemoCheck.Expect("converter.options-across-methods", json == "\"value:7\"" && copy?.Number == 7, json);
    }

    private static JsonSerializerOptions CreateConverterOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new FactoryValueConverterFactory());
        return options;
    }

    private static void ContextMetadata()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var context = new AdvancedJsonContext(options);
        JsonTypeInfo<AdvancedPayload> info = context.AdvancedPayload;
        string json = JsonSerializer.Serialize(new AdvancedPayload { Name = "Ada" }, info);
        var copy = JsonSerializer.Deserialize("{\"NAME\":\"Ada\"}", info);
        DemoCheck.Expect("metadata.constructed-context", copy?.Name == "Ada", json);
    }

    private static void ResolvedMetadata()
    {
        IJsonTypeInfoResolver resolver = JsonTypeInfoResolver.Combine(AdvancedJsonContext.Default);
        var resolvedOptions = new JsonSerializerOptions { TypeInfoResolver = resolver };
        string resolved = JsonSerializer.Serialize(new AdvancedPayload { Name = "Ada" }, resolvedOptions);
        var fromResolver = JsonSerializer.Deserialize<AdvancedPayload>(resolved, resolvedOptions);
        DemoCheck.Expect("metadata.resolver", fromResolver?.Name == "Ada", resolved);
        var metadata = (JsonTypeInfo<AdvancedPayload>)resolvedOptions.GetTypeInfo(typeof(AdvancedPayload));
        string fromInfo = JsonSerializer.Serialize(new AdvancedPayload { Name = "Ada" }, metadata);
        DemoCheck.Expect("metadata.options-get-type-info", fromInfo == resolved, fromInfo);
        JsonTypeInfo? byType = AdvancedJsonContext.Default.GetTypeInfo(typeof(AdvancedPayload));
        string fromContext = JsonSerializer.Serialize(new AdvancedPayload { Name = "Ada" }, byType!);
        DemoCheck.Expect("metadata.context-get-type-info", fromContext == resolved, fromContext);
        JsonSerializerOptions contextOptions = AdvancedJsonContext.Default.Options;
        string fromOptions = JsonSerializer.Serialize(new AdvancedPayload { Name = "Ada" }, contextOptions);
        var fromContextOptions = JsonSerializer.Deserialize<AdvancedPayload>(fromOptions, contextOptions);
        DemoCheck.Expect("metadata.context-options", fromContextOptions?.Name == "Ada", fromOptions);
    }

    private static void ContextFastMode()
    {
        string fast = JsonSerializer.Serialize(new FastPayload { Name = "Ada" }, FastJsonContext.Default.FastPayload);
        DemoCheck.Expect("metadata.serialization-mode", fast == "{\"Name\":\"Ada\"}", fast);
    }

    private static void ContextFactoryMode()
    {
        string factoryJson = JsonSerializer.Serialize(new FactoryValue { Number = 9 },
            FactoryJsonContext.Default.FactoryValue);
        var factoryCopy = JsonSerializer.Deserialize(factoryJson, FactoryJsonContext.Default.FactoryValue);
        DemoCheck.Expect("metadata.factory-converter", factoryJson == "\"value:9\"" && factoryCopy?.Number == 9,
            factoryJson);
    }

    private static void ContextMixedMode()
    {
        string mixed = JsonSerializer.Serialize(new FastPayload { Name = "Ada" }, MixedModeJsonContext.Default.FastPayload);
        var mixedCopy = JsonSerializer.Deserialize("{\"Name\":\"Lin\"}", MixedModeJsonContext.Default.AdvancedPayload);
        DemoCheck.Expect("metadata.mixed-generation-modes", mixed == "{\"Name\":\"Ada\"}" &&
            mixedCopy?.Name == "Lin", mixed);
    }

    private static void ExplicitValueMetadata()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = AdvancedJsonContext.Default };
        JsonTypeInfo<int> info = JsonMetadataServices.CreateValueInfo<int>(options, JsonMetadataServices.Int32Converter);
        string json = JsonSerializer.Serialize(42, info);
        int copy = JsonSerializer.Deserialize(json, info);
        DemoCheck.Expect("metadata.manual-value-info", json == "42" && copy == 42, json);
    }

    private static void ExplicitObjectMetadata()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = AdvancedJsonContext.Default };
        JsonTypeInfo<AdvancedPayload> info = JsonTypeInfo.CreateJsonTypeInfo<AdvancedPayload>(options);
        info.CreateObject = static () => new AdvancedPayload();
        JsonPropertyInfo property = info.CreateJsonPropertyInfo(typeof(string), "label");
        property.Get = static instance => ((AdvancedPayload)instance).Name;
        property.Set = static (instance, value) => ((AdvancedPayload)instance).Name = (string)value!;
        info.Properties.Add(property);
        string json = JsonSerializer.Serialize(new AdvancedPayload { Name = "Ada" }, info);
        var copy = JsonSerializer.Deserialize(json, info);
        bool locked = false;
        try { info.Properties.Clear(); }
        catch (InvalidOperationException) { locked = true; }
        DemoCheck.Expect("metadata.manual-object-info", json == "{\"label\":\"Ada\"}" && copy?.Name == "Ada" &&
            info.IsReadOnly && locked, json);
    }

    private static void MetadataAcrossMethods()
    {
        JsonTypeInfo<AdvancedPayload> info = GetMetadata();
        string json = SerializeWithMetadata(new AdvancedPayload { Name = "Ada" }, info);
        var copy = JsonSerializer.Deserialize(json, info);
        JsonSerializerContext context = GetContext();
        string declared = JsonSerializer.Serialize(new AdvancedPayload { Name = "Ada" }, typeof(AdvancedPayload), context);
        var declaredCopy = JsonSerializer.Deserialize(declared, typeof(AdvancedPayload), context);
        DemoCheck.Expect("metadata.across-methods", json == "{\"Name\":\"Ada\"}" && copy?.Name == "Ada" &&
            declaredCopy is AdvancedPayload { Name: "Ada" }, json);
    }

    private static JsonTypeInfo<AdvancedPayload> GetMetadata() => AdvancedJsonContext.Default.AdvancedPayload;

    private static JsonSerializerContext GetContext() => AdvancedJsonContext.Default;

    private static void ExtendedContext()
    {
        JsonTypeInfo<AdvancedPayload> info = ExtendedJsonContext.Default.GetPayload();
        string json = JsonSerializer.Serialize(new AdvancedPayload { Name = "Ada" }, info);
        var copy = JsonSerializer.Deserialize(json, info);
        DemoCheck.Expect("metadata.extended-context", json == "{\"Name\":\"Ada\"}" && copy?.Name == "Ada", json);
    }

    private static string SerializeWithMetadata(AdvancedPayload value, JsonTypeInfo<AdvancedPayload> info) =>
        JsonSerializer.Serialize(value, info);

    private static void MetadataOptions()
    {
        var options = new JsonSerializerOptions();
        options.TypeInfoResolverChain.Add(AdvancedJsonContext.Default);
        options.TypeInfoResolverChain.Add(MultiJsonContext.Default);
        bool initiallyMutable = !options.IsReadOnly;
        options.MakeReadOnly();
        bool resolved = options.TryGetTypeInfo(typeof(AdvancedPayload), out JsonTypeInfo? info);
        JsonConverter converter = options.GetConverter(typeof(AdvancedPayload));
        bool mutationRejected = false;
        try { options.WriteIndented = true; }
        catch (InvalidOperationException) { mutationRejected = true; }
        bool resolverLocked = false;
        try { options.TypeInfoResolverChain.Clear(); }
        catch (InvalidOperationException) { resolverLocked = true; }
        DemoCheck.Expect("metadata.readonly-options", initiallyMutable && options.IsReadOnly && resolved &&
            info?.Type == typeof(AdvancedPayload) && converter.CanConvert(typeof(AdvancedPayload)) && mutationRejected &&
            resolverLocked,
            mutationRejected ? "locked" : "mutable");
        string other = JsonSerializer.Serialize(new ContextPayload { Name = "Lin", Number = 3 }, options);
        var copy = JsonSerializer.Deserialize<ContextPayload>(other, options);
        DemoCheck.Expect("metadata.resolver-chain", copy is { Name: "Lin", Number: 3 }, other);
    }

    private static void MetadataModifiers()
    {
        var resolver = AdvancedJsonContext.Default.WithAddedModifier(info =>
        {
            if (info.Type != typeof(AdvancedPayload)) return;
            foreach (JsonPropertyInfo property in info.Properties)
                if (property.Name == "Name")
                {
                    property.Name = "alias";
                    property.ShouldSerialize = static (_, value) => value is not "skip";
                }
        });
        var options = new JsonSerializerOptions { TypeInfoResolver = resolver };
        string json = JsonSerializer.Serialize(new AdvancedPayload { Name = "Ada" }, options);
        var copy = JsonSerializer.Deserialize<AdvancedPayload>(json, options);
        string skipped = JsonSerializer.Serialize(new AdvancedPayload { Name = "skip" }, options);
        DemoCheck.Expect("metadata.modifier", json == "{\"alias\":\"Ada\"}" && copy?.Name == "Ada" &&
            skipped == "{}", json + "/" + skipped);
        var customOptions = new JsonSerializerOptions { TypeInfoResolver = new AdvancedResolver() };
        string custom = JsonSerializer.Serialize(new AdvancedPayload { Name = "Ada" }, customOptions);
        var customCopy = JsonSerializer.Deserialize<AdvancedPayload>(custom, customOptions);
        DemoCheck.Expect("metadata.custom-resolver", customCopy?.Name == "Ada", custom);
    }

    private static void MetadataAdditionalProperty()
    {
        var resolver = AdvancedJsonContext.Default.WithAddedModifier(info =>
        {
            if (info.Type != typeof(AdvancedPayload)) return;
            JsonPropertyInfo property = info.CreateJsonPropertyInfo(typeof(string), "upper");
            property.Get = static instance => ((AdvancedPayload)instance).Name.ToUpperInvariant();
            property.Set = static (instance, value) => ((AdvancedPayload)instance).Name = (string)value!;
            info.Properties.Add(property);
        });
        var options = new JsonSerializerOptions { TypeInfoResolver = resolver };
        string json = JsonSerializer.Serialize(new AdvancedPayload { Name = "Ada" }, options);
        var copy = JsonSerializer.Deserialize<AdvancedPayload>("{\"upper\":\"Lin\"}", options);
        DemoCheck.Expect("metadata.add-property", json == "{\"Name\":\"Ada\",\"upper\":\"ADA\"}" &&
            copy?.Name == "Lin", json);
    }

    private static void MetadataPolymorphism()
    {
        var resolver = MetadataGraphContext.Default.WithAddedModifier(info =>
        {
            if (info.Type != typeof(MetadataAnimal)) return;
            var polymorphism = new JsonPolymorphismOptions { TypeDiscriminatorPropertyName = "kind" };
            polymorphism.DerivedTypes.Add(new JsonDerivedType(typeof(MetadataDog), "dog"));
            info.PolymorphismOptions = polymorphism;
        });
        var options = new JsonSerializerOptions { TypeInfoResolver = resolver };
        MetadataAnimal value = new MetadataDog { Name = "Ada", Barks = true };
        string json = JsonSerializer.Serialize(value, options);
        var copy = JsonSerializer.Deserialize<MetadataAnimal>(json, options);
        DemoCheck.Expect("metadata.polymorphism", json.Contains("\"kind\":\"dog\"") &&
            copy is MetadataDog { Name: "Ada", Barks: true }, json);
    }

    private static void MetadataCallbacks()
    {
        var events = new List<string>();
        var resolver = AdvancedJsonContext.Default.WithAddedModifier(info =>
        {
            if (info.Type != typeof(AdvancedPayload)) return;
            info.OnSerializing = _ => events.Add("before-write");
            info.OnSerialized = _ => events.Add("after-write");
            info.OnDeserializing = _ => events.Add("before-read");
            info.OnDeserialized = _ => events.Add("after-read");
        });
        var options = new JsonSerializerOptions { TypeInfoResolver = resolver };
        string json = JsonSerializer.Serialize(new AdvancedPayload { Name = "Ada" }, options);
        var copy = JsonSerializer.Deserialize<AdvancedPayload>(json, options);
        DemoCheck.Expect("metadata.callbacks", copy?.Name == "Ada" && events.SequenceEqual(
            ["before-write", "after-write", "before-read", "after-read"]), string.Join(',', events));
    }

    private static void MetadataConverter()
    {
        var resolver = MetadataConverterContext.Default.WithAddedModifier(info =>
        {
            if (info.Type != typeof(MetadataMemberPayload)) return;
            foreach (JsonPropertyInfo property in info.Properties)
                if (property.Name == "Value") property.CustomConverter = new FactoryValueConverterFactory();
        });
        var options = new JsonSerializerOptions { TypeInfoResolver = resolver };
        var value = new MetadataMemberPayload { Value = new FactoryValue { Number = 12 } };
        string json = JsonSerializer.Serialize(value, options);
        var copy = JsonSerializer.Deserialize<MetadataMemberPayload>(json, options);
        DemoCheck.Expect("metadata.custom-converter", json == "{\"Value\":\"value:12\"}" &&
            copy?.Value.Number == 12, json);
    }

    private static void MetadataPropertyPolicies()
    {
        var resolver = MetadataPolicyContext.Default.WithAddedModifier(info =>
        {
            if (info.Type != typeof(MetadataPolicyPayload)) return;
            foreach (JsonPropertyInfo property in info.Properties)
            {
                if (property.Name == "Name") { property.IsRequired = true; property.Order = -1; }
                if (property.Name == "Amount")
                    property.NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString;
            }
        });
        var options = new JsonSerializerOptions { TypeInfoResolver = resolver };
        string json = JsonSerializer.Serialize(new MetadataPolicyPayload { Name = "Ada", Amount = 7 }, options);
        var copy = JsonSerializer.Deserialize<MetadataPolicyPayload>(json, options);
        bool rejected = false;
        try { _ = JsonSerializer.Deserialize<MetadataPolicyPayload>("{\"Amount\":\"7\"}", options); }
        catch (JsonException) { rejected = true; }
        DemoCheck.Expect("metadata.property-policies", json == "{\"Name\":\"Ada\",\"Amount\":\"7\"}" &&
            copy is { Name: "Ada", Amount: 7 } && rejected, json);
    }

    private static async Task NestedAsync()
    {
        var value = new AsyncPayload { Values = Numbers() };
        using var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(stream, value);
        string json = Encoding.UTF8.GetString(stream.ToArray());
        DemoCheck.Expect("async-sequence.nested", json == "{\"Values\":[1,2]}", json);
        using var input = new MemoryStream(stream.ToArray());
        var asyncCopy = await JsonSerializer.DeserializeAsync<AsyncPayload>(input);
        var items = new List<int>();
        if (asyncCopy?.Values is { } sequence) await foreach (int item in sequence) items.Add(item);
        DemoCheck.Expect("async-sequence.nested-async-read", items.SequenceEqual([1, 2]), string.Join(',', items));
        bool syncRejected = false;
        try { _ = JsonSerializer.Serialize(value); }
        catch (NotSupportedException) { syncRejected = true; }
        DemoCheck.Expect("async-sequence.nested-sync", syncRejected, syncRejected ? "rejected" : "accepted");
        bool syncRootRejected = false;
        try { _ = JsonSerializer.Serialize(Numbers()); }
        catch (NotSupportedException) { syncRootRejected = true; }
        DemoCheck.Expect("async-sequence.root-sync", syncRootRejected, syncRootRejected ? "rejected" : "accepted");
        var syncCopy = JsonSerializer.Deserialize<AsyncPayload>(json);
        var syncItems = new List<int>();
        if (syncCopy?.Values is { } syncSequence) await foreach (int item in syncSequence) syncItems.Add(item);
        DemoCheck.Expect("async-sequence.nested-read", syncItems.SequenceEqual([1, 2]), string.Join(',', syncItems));
    }

    private static async IAsyncEnumerable<int> Numbers([EnumeratorCancellation] CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        yield return 1;
        await Task.Yield();
        cancellation.ThrowIfCancellationRequested();
        yield return 2;
    }

    private static async Task NestedAsyncCollections()
    {
        using var stream = new MemoryStream();
        var values = new Dictionary<string, AsyncPayload>
        {
            ["present"] = new() { Values = Numbers() },
            ["empty"] = new()
        };
        await JsonSerializer.SerializeAsync(stream, values);
        string json = Encoding.UTF8.GetString(stream.ToArray());
        DemoCheck.Expect("async-sequence.nested-map", json ==
            "{\"present\":{\"Values\":[1,2]},\"empty\":{\"Values\":null}}", json);
    }

    private static async Task NestedAsyncFailures()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var cancelledStream = new MemoryStream();
        bool cancelledResult = false;
        try
        {
            await JsonSerializer.SerializeAsync(cancelledStream, new AsyncPayload { Values = Numbers() },
                cancellationToken: cancelled.Token);
        }
        catch (OperationCanceledException) { cancelledResult = true; }
        DemoCheck.Expect("async-sequence.nested-cancel", cancelledResult, cancelledResult ? "cancelled" : "completed");
        using var failedStream = new MemoryStream();
        bool failed = false;
        try { await JsonSerializer.SerializeAsync(failedStream, new AsyncPayload { Values = FailingNumbers() }); }
        catch (InvalidOperationException) { failed = true; }
        DemoCheck.Expect("async-sequence.nested-failure", failed, failed ? "failed" : "completed");
    }

    private static async IAsyncEnumerable<int> FailingNumbers()
    {
        yield return 1;
        await Task.Yield();
        throw new InvalidOperationException("iterator failed");
    }

    private static void ErrorCases()
    {
        bool malformed = false;
        try { _ = JsonSerializer.Deserialize<int>("{"); }
        catch (JsonException) { malformed = true; }
        DemoCheck.Expect("error.malformed-root", malformed, malformed ? "rejected" : "accepted");
        bool nullType = false;
        try { _ = JsonSerializer.Deserialize("1", (Type)null!); }
        catch (ArgumentNullException) { nullType = true; }
        DemoCheck.Expect("error.null-type", nullType, nullType ? "rejected" : "accepted");
        bool invalidOptions = false;
        try { _ = new JsonSerializerOptions { MaxDepth = -1 }; }
        catch (ArgumentOutOfRangeException) { invalidOptions = true; }
        DemoCheck.Expect("error.invalid-options", invalidOptions, invalidOptions ? "rejected" : "accepted");
    }
}

public sealed class AdvancedPayload
{
    public string Name { get; set; } = "";
}

public sealed class AsyncPayload
{
    public IAsyncEnumerable<int>? Values { get; set; }
}

public sealed class FastPayload
{
    public string Name { get; set; } = "";
}

public sealed class MetadataMemberPayload
{
    public FactoryValue Value { get; set; } = new();
}

public sealed class MetadataPolicyPayload
{
    public string Name { get; set; } = "";
    public int Amount { get; set; }
}

public abstract class MetadataAnimal
{
    public string Name { get; set; } = "";
}

public sealed class MetadataDog : MetadataAnimal
{
    public bool Barks { get; set; }
}

[JsonSerializable(typeof(AdvancedPayload))]
internal partial class AdvancedJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Serialization)]
[JsonSerializable(typeof(FastPayload), GenerationMode = JsonSourceGenerationMode.Serialization)]
internal partial class FastJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(Converters = new[] { typeof(FactoryValueConverterFactory) })]
[JsonSerializable(typeof(FactoryValue))]
internal partial class FactoryJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(MetadataAnimal))]
[JsonSerializable(typeof(MetadataDog))]
internal partial class MetadataGraphContext : JsonSerializerContext;

[JsonSerializable(typeof(MetadataMemberPayload))]
[JsonSerializable(typeof(FactoryValue))]
internal partial class MetadataConverterContext : JsonSerializerContext;

[JsonSerializable(typeof(MetadataPolicyPayload))]
internal partial class MetadataPolicyContext : JsonSerializerContext;

[JsonSerializable(typeof(FastPayload), GenerationMode = JsonSourceGenerationMode.Serialization)]
[JsonSerializable(typeof(AdvancedPayload), GenerationMode = JsonSourceGenerationMode.Metadata)]
internal partial class MixedModeJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(AdvancedPayload))]
internal partial class ExtendedJsonContext : JsonSerializerContext
{
    public JsonTypeInfo<AdvancedPayload> GetPayload() => AdvancedPayload;
}

public sealed class AdvancedResolver : IJsonTypeInfoResolver
{
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
        ((IJsonTypeInfoResolver)AdvancedJsonContext.Default).GetTypeInfo(type, options);
}

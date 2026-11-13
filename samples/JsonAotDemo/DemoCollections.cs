using System.Text.Json;

internal static class DemoCollections
{
    internal static void Run()
    {
        CustomKey();
        var characters = new Dictionary<char, int> { ['A'] = 1, ['я'] = 2 };
        string characterJson = JsonSerializer.Serialize(characters);
        var characterCopy = JsonSerializer.Deserialize<Dictionary<char, int>>(characterJson);
        DemoCheck.Expect("map.character-keys", characterCopy is { Count: 2 } && characterCopy['A'] == 1 &&
            characterCopy['я'] == 2, characterJson);
        var decimals = new Dictionary<decimal, string> { [1.25m] = "a", [-2.5m] = "b" };
        string decimalJson = JsonSerializer.Serialize(decimals);
        var decimalCopy = JsonSerializer.Deserialize<Dictionary<decimal, string>>(decimalJson);
        DemoCheck.Expect("map.decimal-keys", decimalCopy is { Count: 2 } && decimalCopy[1.25m] == "a" &&
            decimalCopy[-2.5m] == "b", decimalJson);
        var floating = new Dictionary<double, int> { [1.5] = 1, [-2.25] = 2 };
        string floatingJson = JsonSerializer.Serialize(floating);
        var floatingCopy = JsonSerializer.Deserialize<Dictionary<double, int>>(floatingJson);
        DemoCheck.Expect("map.floating-keys", floatingCopy is { Count: 2 } && floatingCopy[1.5] == 1 &&
            floatingCopy[-2.25] == 2, floatingJson);
        bool nonfiniteRejected = false;
        try { JsonSerializer.Serialize(new Dictionary<double, int> { [double.PositiveInfinity] = 1 }); }
        catch (ArgumentException) { nonfiniteRejected = true; }
        DemoCheck.Expect("map.nonfinite-key", nonfiniteRejected, nonfiniteRejected ? "rejected" : "accepted");
        var dateKeys = new Dictionary<DateTime, string> { [new DateTime(2024, 2, 29, 1, 2, 3, DateTimeKind.Utc)] = "a" };
        string dateKeyJson = JsonSerializer.Serialize(dateKeys);
        var dateKeyCopy = JsonSerializer.Deserialize<Dictionary<DateTime, string>>(dateKeyJson);
        DemoCheck.Expect("map.datetime-key", dateKeyCopy?.Single().Key == dateKeys.Single().Key, dateKeyJson);
        var timeKeys = new Dictionary<TimeOnly, int> { [new TimeOnly(1, 2, 3)] = 1 };
        string timeKeyJson = JsonSerializer.Serialize(timeKeys);
        var timeKeyCopy = JsonSerializer.Deserialize<Dictionary<TimeOnly, int>>(timeKeyJson);
        DemoCheck.Expect("map.timeonly-key", timeKeyCopy?.Single().Key == timeKeys.Single().Key, timeKeyJson);
        var offsetKeys = new Dictionary<DateTimeOffset, int> { [DateTimeOffset.UnixEpoch] = 1 };
        string offsetKeyJson = JsonSerializer.Serialize(offsetKeys);
        var offsetKeyCopy = JsonSerializer.Deserialize<Dictionary<DateTimeOffset, int>>(offsetKeyJson);
        DemoCheck.Expect("map.offset-key", offsetKeyCopy?.Single().Key == DateTimeOffset.UnixEpoch, offsetKeyJson);
        var dayKeys = new Dictionary<DateOnly, int> { [new DateOnly(2024, 2, 29)] = 1 };
        string dayKeyJson = JsonSerializer.Serialize(dayKeys);
        var dayKeyCopy = JsonSerializer.Deserialize<Dictionary<DateOnly, int>>(dayKeyJson);
        DemoCheck.Expect("map.dateonly-key", dayKeyCopy?.Single().Key == new DateOnly(2024, 2, 29), dayKeyJson);
        var durationKeys = new Dictionary<TimeSpan, int> { [TimeSpan.FromSeconds(2)] = 1 };
        string durationKeyJson = JsonSerializer.Serialize(durationKeys);
        var durationKeyCopy = JsonSerializer.Deserialize<Dictionary<TimeSpan, int>>(durationKeyJson);
        DemoCheck.Expect("map.timespan-key", durationKeyCopy?.Single().Key == TimeSpan.FromSeconds(2), durationKeyJson);
        List<int> numbers = [1, 2, 3];
        string listJson = JsonSerializer.Serialize(numbers);
        DemoCheck.Expect("encode.list", listJson == "[1,2,3]", listJson);
        List<int> list = JsonSerializer.Deserialize<List<int>>(listJson)
            ?? throw new InvalidOperationException("Deserialization returned null.");
        DemoCheck.Expect("decode.list", list.Count == 3 && list[0] == 1 && list[2] == 3,
            $"count={list.Count}");
        var values = new Dictionary<string, int> { ["one"] = 1, ["two"] = 2 };
        string mapJson = JsonSerializer.Serialize(values);
        DemoCheck.Expect("encode.dictionary", mapJson == "{\"one\":1,\"two\":2}", mapJson);
        Dictionary<string, int> map = JsonSerializer.Deserialize<Dictionary<string, int>>(mapJson)
            ?? throw new InvalidOperationException("Deserialization returned null.");
        DemoCheck.Expect("decode.dictionary", map.Count == 2 && map["one"] == 1 && map["two"] == 2,
            $"count={map.Count}");
        MoreCollections();
        Interfaces();
        NumericKeys();
        NumericKeyTypes();
        OtherKeys();
        EnumKeys();
    }

    private static void CustomKey()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new LabelKeyConverter());
        var map = new Dictionary<LabelKey, int> { [new LabelKey { Name = "Ada" }] = 7 };
        string json = JsonSerializer.Serialize(map, options);
        var copy = JsonSerializer.Deserialize<Dictionary<LabelKey, int>>(json, options);
        DemoCheck.Expect("map.custom-key-converter", json == "{\"#Ada\":7}" &&
            copy?.Single().Key.Name == "Ada" && copy.Single().Value == 7, json);
        string attributedJson = JsonSerializer.Serialize(map);
        var attributedCopy = JsonSerializer.Deserialize<Dictionary<LabelKey, int>>(attributedJson);
        DemoCheck.Expect("map.attribute-key-converter", attributedJson == "{\"#Ada\":7}" &&
            attributedCopy?.Single().Key.Name == "Ada", attributedJson);
    }

    private static void EnumKeys()
    {
        var value = new Dictionary<DemoPermissions, int>
        {
            [DemoPermissions.Execute] = 1,
            [DemoPermissions.Read | DemoPermissions.Write] = 2,
            [(DemoPermissions)8] = 3
        };
        string json = JsonSerializer.Serialize(value);
        var copy = JsonSerializer.Deserialize<Dictionary<DemoPermissions, int>>(json);
        DemoCheck.Expect("roundtrip.enum-keys", copy?[DemoPermissions.Execute] == 1 &&
            copy?[DemoPermissions.Read | DemoPermissions.Write] == 2 && copy?[(DemoPermissions)8] == 3, json);
    }

    private static void OtherKeys()
    {
        var boolean = new Dictionary<bool, int> { [true] = 1 };
        var guid = new Dictionary<Guid, int> { [Guid.Empty] = 2 };
        var uri = new Dictionary<Uri, int> { [new Uri("https://example.com/a%20b")] = 3 };
        var version = new Dictionary<Version, int> { [new Version(1, 2)] = 4 };
        string a = JsonSerializer.Serialize(boolean);
        string b = JsonSerializer.Serialize(guid);
        string c = JsonSerializer.Serialize(uri);
        string d = JsonSerializer.Serialize(version);
        bool passed = JsonSerializer.Deserialize<Dictionary<bool, int>>(a)?[true] == 1 &&
            JsonSerializer.Deserialize<Dictionary<Guid, int>>(b)?[Guid.Empty] == 2 &&
            JsonSerializer.Deserialize<Dictionary<Uri, int>>(c)?[new Uri("https://example.com/a%20b")] == 3 &&
            JsonSerializer.Deserialize<Dictionary<Version, int>>(d)?[new Version(1, 2)] == 4;
        DemoCheck.Expect("roundtrip.other-key-types", passed, string.Join('|', a, b, c, d));
        bool rejected = false;
        try { _ = JsonSerializer.Deserialize<Dictionary<bool, int>>("{\" true\":1}"); }
        catch (JsonException) { rejected = true; }
        DemoCheck.Expect("decode.bool-key-whitespace", rejected, rejected ? "rejected" : "accepted");
    }

    private static void NumericKeyTypes()
    {
        var a = JsonSerializer.Deserialize<Dictionary<byte, int>>("{\"255\":1}");
        var b = JsonSerializer.Deserialize<Dictionary<sbyte, int>>("{\"-128\":1}");
        var c = JsonSerializer.Deserialize<Dictionary<short, int>>("{\"-32768\":1}");
        var d = JsonSerializer.Deserialize<Dictionary<ushort, int>>("{\"65535\":1}");
        var e = JsonSerializer.Deserialize<Dictionary<uint, int>>("{\"4294967295\":1}");
        var f = JsonSerializer.Deserialize<Dictionary<long, int>>("{\"-9223372036854775808\":1}");
        var g = JsonSerializer.Deserialize<Dictionary<ulong, int>>("{\"18446744073709551615\":1}");
        string result = string.Join('|', JsonSerializer.Serialize(a), JsonSerializer.Serialize(b),
            JsonSerializer.Serialize(c), JsonSerializer.Serialize(d), JsonSerializer.Serialize(e),
            JsonSerializer.Serialize(f), JsonSerializer.Serialize(g));
        bool passed = a?[byte.MaxValue] == 1 && b?[sbyte.MinValue] == 1 && c?[short.MinValue] == 1 &&
            d?[ushort.MaxValue] == 1 && e?[uint.MaxValue] == 1 && f?[long.MinValue] == 1 && g?[ulong.MaxValue] == 1;
        DemoCheck.Expect("roundtrip.integer-key-types", passed, result);
    }

    private static void NumericKeys()
    {
        var value = new Dictionary<int, string> { [1] = "one", [-2] = "minus" };
        string json = JsonSerializer.Serialize(value);
        var copy = JsonSerializer.Deserialize<Dictionary<int, string>>(json);
        DemoCheck.Expect("roundtrip.integer-keys", copy is not null && copy[1] == "one" && copy[-2] == "minus", json);
        bool rejected = false;
        try { _ = JsonSerializer.Deserialize<Dictionary<int, string>>("{\" 1\":\"bad\"}"); }
        catch (JsonException) { rejected = true; }
        DemoCheck.Expect("decode.integer-key-whitespace", rejected, rejected ? "rejected" : "accepted");
    }

    private static void MoreCollections()
    {
        var set = new HashSet<int> { 1, 2, 3 };
        string setJson = JsonSerializer.Serialize(set);
        var restoredSet = JsonSerializer.Deserialize<HashSet<int>>(setJson);
        DemoCheck.Expect("roundtrip.hashset", restoredSet is not null && restoredSet.SetEquals(set), setJson);
        var queue = new Queue<int>([1, 2, 3]);
        string queueJson = JsonSerializer.Serialize(queue);
        var restoredQueue = JsonSerializer.Deserialize<Queue<int>>(queueJson);
        DemoCheck.Expect("roundtrip.queue", restoredQueue is not null && restoredQueue.SequenceEqual(queue), queueJson);
        var linked = new LinkedList<int>([1, 2, 3]);
        string linkedJson = JsonSerializer.Serialize(linked);
        var restoredLinked = JsonSerializer.Deserialize<LinkedList<int>>(linkedJson);
        DemoCheck.Expect("roundtrip.linked-list", restoredLinked is not null && restoredLinked.SequenceEqual(linked),
            linkedJson);
        var stack = JsonSerializer.Deserialize<Stack<int>>("[1,2,3]");
        DemoCheck.Expect("decode.stack", stack is not null && stack.SequenceEqual([3, 2, 1]),
            stack is null ? "null" : string.Join(',', stack));
    }

    private static void Interfaces()
    {
        IEnumerable<int> source = new[] { 1, 2, 3 };
        string json = JsonSerializer.Serialize(source);
        var result = JsonSerializer.Deserialize<IEnumerable<int>>(json);
        DemoCheck.Expect("roundtrip.enumerable", result is not null && result.SequenceEqual(source), json);
        IReadOnlyDictionary<string, int> map = new Dictionary<string, int> { ["one"] = 1 };
        string mapJson = JsonSerializer.Serialize(map);
        var restored = JsonSerializer.Deserialize<IReadOnlyDictionary<string, int>>(mapJson);
        DemoCheck.Expect("roundtrip.readonly-map", restored is not null && restored["one"] == 1, mapJson);
    }
}

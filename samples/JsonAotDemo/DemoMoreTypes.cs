using System.Collections.Concurrent;
using System.Collections;
using System.Text.Json;

internal static class DemoMoreTypes
{
    internal static void Run()
    {
        DemoCheck.Run("numeric-boundaries", Numbers);
        DemoCheck.Run("wide-number-keys", WideNumberKeys);
        DemoCheck.Run("memory-values", MemoryValues);
        DemoCheck.Run("pairs-tuples", PairsAndTuples);
        DemoCheck.Run("more-collections", MoreCollections);
        DemoCheck.Run("unsupported-arrays", UnsupportedArrays);
    }

    private static void Numbers()
    {
        var value = new NumericBoundaries
        {
            Byte = byte.MaxValue, SignedByte = sbyte.MinValue, Short = short.MinValue, UShort = ushort.MaxValue,
            Int = int.MinValue, UInt = uint.MaxValue, Single = 1.5f, Double = -2.5,
            Signed = long.MinValue, Unsigned = ulong.MaxValue, Decimal = decimal.MaxValue,
            Half = (Half)1.5f, Wide = Int128.MaxValue, UnsignedWide = UInt128.MaxValue
        };
        string json = JsonSerializer.Serialize(value);
        var copy = JsonSerializer.Deserialize<NumericBoundaries>(json);
        DemoCheck.Expect("types.numeric-boundaries", copy is not null && copy.Byte == value.Byte &&
            copy.SignedByte == value.SignedByte && copy.Short == value.Short && copy.UShort == value.UShort &&
            copy.Int == value.Int && copy.UInt == value.UInt && copy.Single == value.Single &&
            copy.Double == value.Double && copy.Signed == value.Signed &&
            copy.Unsigned == value.Unsigned && copy.Decimal == value.Decimal && copy.Half == value.Half &&
            copy.Wide == value.Wide && copy.UnsignedWide == value.UnsignedWide, json);
    }

    private static void MemoryValues()
    {
        var value = new MemoryPayload { Bytes = new byte[] { 1, 2, 3 }, ReadOnlyBytes = new byte[] { 4, 5 },
            Numbers = new[] { 6, 7 }, Names = new[] { "Ada", "Lin" } };
        string json = JsonSerializer.Serialize(value);
        var copy = JsonSerializer.Deserialize<MemoryPayload>(json);
        DemoCheck.Expect("types.memory-bytes", json.Contains("\"Bytes\":\"AQID\"") &&
            json.Contains("\"ReadOnlyBytes\":\"BAU=\"") && copy is not null &&
            copy.Bytes.Span.SequenceEqual(new byte[] { 1, 2, 3 }) &&
            copy.ReadOnlyBytes.Span.SequenceEqual(new byte[] { 4, 5 }) && copy.Numbers.Span.SequenceEqual(new[] { 6, 7 }) &&
            copy.Names.Span.SequenceEqual(new[] { "Ada", "Lin" }), json);
    }

    private static void WideNumberKeys()
    {
        var signed = new Dictionary<Int128, string> { [Int128.MinValue] = "low" };
        string signedJson = JsonSerializer.Serialize(signed);
        var signedCopy = JsonSerializer.Deserialize<Dictionary<Int128, string>>(signedJson);
        var unsigned = new Dictionary<UInt128, string> { [UInt128.MaxValue] = "high" };
        string unsignedJson = JsonSerializer.Serialize(unsigned);
        var unsignedCopy = JsonSerializer.Deserialize<Dictionary<UInt128, string>>(unsignedJson);
        var halves = new Dictionary<Half, int> { [(Half)1.5f] = 2 };
        string halfJson = JsonSerializer.Serialize(halves);
        var halfCopy = JsonSerializer.Deserialize<Dictionary<Half, int>>(halfJson);
        DemoCheck.Expect("types.wide-number-keys", signedCopy?[Int128.MinValue] == "low" &&
            unsignedCopy?[UInt128.MaxValue] == "high" && halfCopy?[(Half)1.5f] == 2,
            signedJson + "/" + unsignedJson + "/" + halfJson);
    }

    private static void PairsAndTuples()
    {
        var pair = new KeyValuePair<string, int>("a", 2);
        string json = JsonSerializer.Serialize(pair);
        var copy = JsonSerializer.Deserialize<KeyValuePair<string, int>>(json);
        DemoCheck.Expect("types.key-value-pair", copy.Key == "a" && copy.Value == 2, json);
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        string camel = JsonSerializer.Serialize(pair, options);
        var camelCopy = JsonSerializer.Deserialize<KeyValuePair<string, int>>("{\"KEY\":\"a\"}", options);
        DemoCheck.Expect("types.key-value-pair-options", camel == "{\"key\":\"a\",\"value\":2}" &&
            camelCopy.Key == "a" && camelCopy.Value == 0, camel);
        var tuple = Tuple.Create("Ada", 7);
        string tupleJson = JsonSerializer.Serialize(tuple);
        var tupleCopy = JsonSerializer.Deserialize<Tuple<string, int>>(tupleJson);
        DemoCheck.Expect("types.tuple", tupleCopy is { Item1: "Ada", Item2: 7 }, tupleJson);
        var tupleOptions = new JsonSerializerOptions { IncludeFields = true };
        string valueJson = JsonSerializer.Serialize((Name: "Ada", Number: 7), tupleOptions);
        var valueCopy = JsonSerializer.Deserialize<(string Name, int Number)>(valueJson, tupleOptions);
        DemoCheck.Expect("types.value-tuple", valueCopy.Name == "Ada" && valueCopy.Number == 7, valueJson);
    }

    private static void MoreCollections()
    {
        var bag = new ConcurrentBag<int>([1, 2]);
        string json = JsonSerializer.Serialize(bag);
        bool rejected = false;
        try { _ = JsonSerializer.Deserialize<ConcurrentBag<int>>(json); }
        catch (NotSupportedException) { rejected = true; }
        DemoCheck.Expect("types.concurrent-bag", rejected, json);
        var sorted = new SortedDictionary<string, int> { ["b"] = 2, ["a"] = 1 };
        string sortedJson = JsonSerializer.Serialize(sorted);
        var sortedCopy = JsonSerializer.Deserialize<SortedDictionary<string, int>>(sortedJson);
        DemoCheck.Expect("types.sorted-map", sortedCopy is { Count: 2 } && sortedCopy["a"] == 1 &&
            sortedCopy["b"] == 2, sortedJson);
        var table = new Hashtable { ["first"] = 1, ["name"] = "Ada" };
        string tableJson = JsonSerializer.Serialize(table);
        var tableCopy = JsonSerializer.Deserialize<Hashtable>(tableJson);
        DemoCheck.Expect("types.hashtable", tableCopy?["first"] is JsonElement first && first.GetInt32() == 1 &&
            tableCopy["name"] is JsonElement name && name.GetString() == "Ada", tableJson);
        ISet<int> set = new HashSet<int> { 1, 2 };
        string setJson = JsonSerializer.Serialize(set);
        var setCopy = JsonSerializer.Deserialize<ISet<int>>(setJson);
        DemoCheck.Expect("types.interface-set", setCopy?.SetEquals([1, 2]) == true, setJson);
    }

    private static void UnsupportedArrays()
    {
        bool rejected = false;
        try { _ = JsonSerializer.Serialize(new int[1, 1]); }
        catch (NotSupportedException) { rejected = true; }
        DemoCheck.Expect("types.multidimensional-array", rejected, rejected ? "rejected" : "accepted");
    }
}

public sealed class NumericBoundaries
{
    public byte Byte { get; set; }
    public sbyte SignedByte { get; set; }
    public short Short { get; set; }
    public ushort UShort { get; set; }
    public int Int { get; set; }
    public uint UInt { get; set; }
    public float Single { get; set; }
    public double Double { get; set; }
    public long Signed { get; set; }
    public ulong Unsigned { get; set; }
    public decimal Decimal { get; set; }
    public Half Half { get; set; }
    public Int128 Wide { get; set; }
    public UInt128 UnsignedWide { get; set; }
}

public sealed class MemoryPayload
{
    public Memory<byte> Bytes { get; set; }
    public ReadOnlyMemory<byte> ReadOnlyBytes { get; set; }
    public Memory<int> Numbers { get; set; }
    public ReadOnlyMemory<string> Names { get; set; }
}

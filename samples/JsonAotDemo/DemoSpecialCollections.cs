using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;

internal static class DemoSpecialCollections
{
    internal static void Run()
    {
        DemoCheck.Run("special-dictionaries", Dictionaries);
        DemoCheck.Run("special-ordered-dictionary", Ordered);
        DemoCheck.Run("special-strings", Strings);
        DemoCheck.Run("special-string-dictionary", StringMap);
        DemoCheck.Run("special-bit-vector", Bits);
        DemoCheck.Run("special-readonly", ReadOnly);
        DemoCheck.Run("special-keyed", Keyed);
        DemoCheck.Run("special-blocking", Blocking);
    }

    private static void Dictionaries()
    {
        var hybrid = new HybridDictionary { ["name"] = "Ada" };
        string hybridJson = JsonSerializer.Serialize(hybrid);
        var hybridCopy = JsonSerializer.Deserialize<HybridDictionary>(hybridJson);
        var list = new ListDictionary { ["number"] = 7 };
        string listJson = JsonSerializer.Serialize(list);
        var listCopy = JsonSerializer.Deserialize<ListDictionary>(listJson);
        DemoCheck.Expect("types.special-dictionaries", hybridCopy?["name"] is JsonElement name &&
            name.GetString() == "Ada" && listCopy?["number"] is JsonElement number && number.GetInt32() == 7,
            hybridJson + "/" + listJson);
    }

    private static void Strings()
    {
        var strings = new StringCollection { "Ada", "Lin" };
        string json = JsonSerializer.Serialize(strings);
        bool collectionRejected = false;
        try { _ = JsonSerializer.Deserialize<StringCollection>(json); }
        catch (InvalidCastException) { collectionRejected = true; }
        var names = new NameValueCollection { ["name"] = "Ada" };
        string namesJson = JsonSerializer.Serialize(names);
        bool namesRejected = false;
        try { _ = JsonSerializer.Deserialize<NameValueCollection>(namesJson); }
        catch (NotSupportedException) { namesRejected = true; }
        DemoCheck.Expect("types.special-string-collections", json == "[\"Ada\",\"Lin\"]" &&
            namesJson == "[\"name\"]" && collectionRejected && namesRejected, json + "/" + namesJson);
    }

    private static void Ordered()
    {
        IOrderedDictionary value = new OrderedDictionary { ["name"] = "Ada" };
        string json = JsonSerializer.Serialize(value);
        bool rejected = false;
        try { _ = JsonSerializer.Deserialize<IOrderedDictionary>(json); }
        catch (NotSupportedException) { rejected = true; }
        DemoCheck.Expect("types.ordered-dictionary", json == "{\"name\":\"Ada\"}" && rejected, json);
    }

    private static void StringMap()
    {
        var value = new StringDictionary { ["name"] = "Ada" };
        string json = JsonSerializer.Serialize(value);
        bool rejected = false;
        try { _ = JsonSerializer.Deserialize<StringDictionary>(json); }
        catch (NotSupportedException) { rejected = true; }
        DemoCheck.Expect("types.string-dictionary", json == "[{\"Key\":\"name\",\"Value\":\"Ada\"}]" &&
            rejected, json);
    }

    private static void Bits()
    {
        var value = new BitVector32(3);
        string json = JsonSerializer.Serialize(value);
        var copy = JsonSerializer.Deserialize<BitVector32>(json);
        DemoCheck.Expect("types.bit-vector", json.Contains("\"Data\":3") && copy.Data == 0, json);
    }

    private static void ReadOnly()
    {
        var map = new ReadOnlyDictionary<string, int>(new Dictionary<string, int> { ["a"] = 1 });
        string json = JsonSerializer.Serialize(map);
        bool mapRejected = false;
        try { _ = JsonSerializer.Deserialize<ReadOnlyDictionary<string, int>>(json); }
        catch (NotSupportedException) { mapRejected = true; }
        var list = new ReadOnlyObservableCollection<int>(new ObservableCollection<int> { 1, 2 });
        string listJson = JsonSerializer.Serialize(list);
        bool listRejected = false;
        try { _ = JsonSerializer.Deserialize<ReadOnlyObservableCollection<int>>(listJson); }
        catch (NotSupportedException) { listRejected = true; }
        DemoCheck.Expect("types.readonly-collections", json == "{\"a\":1}" && listJson == "[1,2]" &&
            mapRejected && listRejected, json + "/" + listJson);
    }

    private static void Blocking()
    {
        using var values = new BlockingCollection<int>();
        values.Add(1);
        values.Add(2);
        string json = JsonSerializer.Serialize(values);
        bool rejected = false;
        try { _ = JsonSerializer.Deserialize<BlockingCollection<int>>(json); }
        catch (NotSupportedException) { rejected = true; }
        DemoCheck.Expect("types.blocking-collection", json == "[1,2]" && rejected, json);
    }

    private static void Keyed()
    {
        KeyedCollection<string, NamedItem> values = new NamedItems { new NamedItem { Name = "Ada" } };
        string json = JsonSerializer.Serialize(values);
        bool rejected = false;
        try { _ = JsonSerializer.Deserialize<KeyedCollection<string, NamedItem>>(json); }
        catch (NotSupportedException) { rejected = true; }
        DemoCheck.Expect("types.keyed-collection", json == "[{\"Name\":\"Ada\"}]" && rejected, json);
    }
}

public sealed class NamedItem
{
    public string Name { get; set; } = "";
}

public sealed class NamedItems : KeyedCollection<string, NamedItem>
{
    protected override string GetKeyForItem(NamedItem item) => item.Name;
}

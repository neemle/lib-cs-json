using System.Collections;
using System.Text.Json;

internal static class DemoMoreCollections
{
    internal static void Run()
    {
        DemoCheck.Run("jagged-arrays", JaggedArrays);
        DemoCheck.Run("dictionary-entry", Entry);
        DemoCheck.Run("non-generic-interfaces", NonGenericInterfaces);
        DemoCheck.Run("readonly-generic-interfaces", ReadOnlyInterfaces);
        DemoCheck.Run("linked-list-node", Node);
        DemoCheck.Run("native-integers", UnsupportedPointers);
    }

    private static void JaggedArrays()
    {
        int[][] values = [[1, 2], [], [3]];
        string json = JsonSerializer.Serialize(values);
        var copy = JsonSerializer.Deserialize<int[][]>(json);
        DemoCheck.Expect("types.jagged-arrays", json == "[[1,2],[],[3]]" && copy is { Length: 3 } &&
            copy[0].SequenceEqual([1, 2]) && copy[1].Length == 0 && copy[2].SequenceEqual([3]), json);
    }

    private static void Entry()
    {
        var entry = new DictionaryEntry("a", 2);
        string json = JsonSerializer.Serialize(entry);
        var copy = JsonSerializer.Deserialize<DictionaryEntry>(json);
        bool valid = copy.Key is JsonElement key && key.GetString() == "a" && copy.Value is JsonElement value &&
            value.GetInt32() == 2;
        DemoCheck.Expect("types.dictionary-entry", valid, json);
    }

    private static void NonGenericInterfaces()
    {
        ICollection values = new ArrayList { 1, "Ada" };
        string json = JsonSerializer.Serialize(values);
        var copy = JsonSerializer.Deserialize<ICollection>(json);
        IDictionary map = new Hashtable { ["name"] = "Ada" };
        string mapJson = JsonSerializer.Serialize(map);
        var mapCopy = JsonSerializer.Deserialize<IDictionary>(mapJson);
        var sorted = new SortedList { ["b"] = 2, ["a"] = 1 };
        string sortedJson = JsonSerializer.Serialize(sorted);
        var sortedCopy = JsonSerializer.Deserialize<SortedList>(sortedJson);
        IEnumerable sequence = new ArrayList { 1, 2 };
        string sequenceJson = JsonSerializer.Serialize(sequence);
        var sequenceCopy = JsonSerializer.Deserialize<IEnumerable>(sequenceJson);
        IList list = new ArrayList { 1, 2 };
        string listJson = JsonSerializer.Serialize(list);
        var listCopy = JsonSerializer.Deserialize<IList>(listJson);
        DemoCheck.Expect("types.non-generic-interfaces", copy?.Count == 2 &&
            mapCopy?["name"] is JsonElement name && name.GetString() == "Ada" && sortedCopy?.Count == 2 &&
            sequenceCopy?.Cast<object>().Count() == 2 && listCopy?.Count == 2,
            json + "/" + mapJson + "/" + sortedJson);
    }

    private static void Node()
    {
        var node = new LinkedListNode<int>(7);
        string json = JsonSerializer.Serialize(node);
        var copy = JsonSerializer.Deserialize<LinkedListNode<int>>(json);
        DemoCheck.Expect("types.linked-list-node", json.Contains("\"Value\":7") && copy?.Value == 7, json);
    }

    private static void ReadOnlyInterfaces()
    {
        IReadOnlyList<int> list = new[] { 1, 2 };
        string json = JsonSerializer.Serialize(list);
        var listCopy = JsonSerializer.Deserialize<IReadOnlyList<int>>(json);
        IReadOnlyCollection<int> collection = new List<int> { 3, 4 };
        string collectionJson = JsonSerializer.Serialize(collection);
        var collectionCopy = JsonSerializer.Deserialize<IReadOnlyCollection<int>>(collectionJson);
        DemoCheck.Expect("types.readonly-generic-interfaces", listCopy?.SequenceEqual([1, 2]) == true &&
            collectionCopy?.SequenceEqual([3, 4]) == true, json + "/" + collectionJson);
    }

    private static void UnsupportedPointers()
    {
        bool signed = false;
        try { _ = JsonSerializer.Serialize((nint)1); }
        catch (NotSupportedException) { signed = true; }
        bool unsigned = false;
        try { _ = JsonSerializer.Deserialize<nuint>("1"); }
        catch (NotSupportedException) { unsigned = true; }
        DemoCheck.Expect("types.native-integers", signed && unsigned, signed && unsigned ? "rejected" : "accepted");
    }
}

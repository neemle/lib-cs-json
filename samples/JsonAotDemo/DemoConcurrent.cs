using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.Json;

internal static class DemoConcurrent
{
    internal static void Run()
    {
        var queue = new ConcurrentQueue<int>([1, 2]);
        string queueJson = JsonSerializer.Serialize(queue);
        var queueCopy = JsonSerializer.Deserialize<ConcurrentQueue<int>>(queueJson);
        DemoCheck.Expect("concurrent.queue", queueCopy is not null && queueCopy.SequenceEqual(queue), queueJson);
        var stack = new ConcurrentStack<int>([1, 2]);
        string stackJson = JsonSerializer.Serialize(stack);
        var stackCopy = JsonSerializer.Deserialize<ConcurrentStack<int>>(stackJson);
        string stackRoundtrip = JsonSerializer.Serialize(stackCopy);
        DemoCheck.Expect("concurrent.stack", stackCopy is not null && stackCopy.SequenceEqual([1, 2]), stackRoundtrip);
        var map = new ConcurrentDictionary<string, int>();
        map["a"] = 1;
        string mapJson = JsonSerializer.Serialize(map);
        var mapCopy = JsonSerializer.Deserialize<ConcurrentDictionary<string, int>>(mapJson);
        DemoCheck.Expect("concurrent.map", mapCopy?["a"] == 1, mapJson);
        var collection = new ObservableCollection<int> { 1, 2 };
        string collectionJson = JsonSerializer.Serialize(collection);
        var collectionCopy = JsonSerializer.Deserialize<ObservableCollection<int>>(collectionJson);
        DemoCheck.Expect("collection.observable", collectionCopy is not null && collectionCopy.SequenceEqual(collection),
            collectionJson);
        var readOnly = new ReadOnlyCollection<int>(new List<int> { 1, 2 });
        string readOnlyJson = JsonSerializer.Serialize(readOnly);
        bool rejected = false;
        try { _ = JsonSerializer.Deserialize<ReadOnlyCollection<int>>(readOnlyJson); }
        catch (NotSupportedException) { rejected = true; }
        DemoCheck.Expect("collection.readonly", readOnlyJson == "[1,2]" && rejected, readOnlyJson);
        Custom();
    }

    private static void Custom()
    {
        var list = new CustomNumbers { 1, 2 };
        string json = JsonSerializer.Serialize(list);
        var copy = JsonSerializer.Deserialize<CustomNumbers>(json);
        DemoCheck.Expect("collection.custom-list", copy is not null && copy.SequenceEqual(list), json);
        var queue = new CustomQueue();
        queue.Enqueue(1);
        queue.Enqueue(2);
        string queued = JsonSerializer.Serialize(queue);
        var queueCopy = JsonSerializer.Deserialize<CustomQueue>(queued);
        DemoCheck.Expect("collection.custom-queue", queueCopy is not null && queueCopy.SequenceEqual(queue), queued);
        var map = new SwappedMap<int, string> { ["a"] = 3 };
        string mapJson = JsonSerializer.Serialize(map);
        var mapCopy = JsonSerializer.Deserialize<SwappedMap<int, string>>(mapJson);
        DemoCheck.Expect("collection.custom-map", mapCopy?["a"] == 3, mapJson);
    }
}

public sealed class CustomNumbers : List<int> { }

public sealed class CustomQueue : Queue<int> { }

public sealed class SwappedMap<TValue, TKey> : Dictionary<TKey, TValue> where TKey : notnull { }

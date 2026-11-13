using System.Collections.Immutable;
using System.Text.Json;

internal static class DemoImmutable
{
    internal static void Run()
    {
        var array = ImmutableArray.Create("a", "b");
        string arrayJson = JsonSerializer.Serialize(array);
        var arrayCopy = JsonSerializer.Deserialize<ImmutableArray<string>>(arrayJson);
        DemoCheck.Expect("immutable.array", arrayCopy.SequenceEqual(array), arrayJson);
        var list = ImmutableList.Create(1, 2);
        string listJson = JsonSerializer.Serialize(list);
        var listCopy = JsonSerializer.Deserialize<ImmutableList<int>>(listJson);
        DemoCheck.Expect("immutable.list", listCopy is not null && listCopy.SequenceEqual(list), listJson);
        IImmutableSet<int> set = ImmutableHashSet.Create(1, 2);
        string setJson = JsonSerializer.Serialize(set);
        var setCopy = JsonSerializer.Deserialize<IImmutableSet<int>>(setJson);
        DemoCheck.Expect("immutable.set", setCopy is not null && setCopy.SetEquals(set), setJson);
        IImmutableDictionary<string, int> map = ImmutableDictionary<string, int>.Empty.Add("a", 1);
        string mapJson = JsonSerializer.Serialize(map);
        var mapCopy = JsonSerializer.Deserialize<IImmutableDictionary<string, int>>(mapJson);
        DemoCheck.Expect("immutable.map", mapCopy?["a"] == 1, mapJson);
        IImmutableQueue<int> queue = ImmutableQueue.Create(1, 2);
        string queueJson = JsonSerializer.Serialize(queue);
        var queueCopy = JsonSerializer.Deserialize<IImmutableQueue<int>>(queueJson);
        DemoCheck.Expect("immutable.queue", queueCopy is not null && queueCopy.SequenceEqual(queue), queueJson);
        var stack = ImmutableStack<int>.Empty.Push(1).Push(2);
        string stackJson = JsonSerializer.Serialize(stack);
        var stackCopy = JsonSerializer.Deserialize<ImmutableStack<int>>(stackJson);
        string stackRoundtrip = JsonSerializer.Serialize(stackCopy);
        DemoCheck.Expect("immutable.stack", stackCopy is not null && stackCopy.SequenceEqual([1, 2]), stackRoundtrip);
        SortedAndInterfaces();
    }

    private static void SortedAndInterfaces()
    {
        IImmutableList<int> list = ImmutableList.Create(1, 2);
        string listJson = JsonSerializer.Serialize(list);
        var listCopy = JsonSerializer.Deserialize<IImmutableList<int>>(listJson);
        var sortedSet = ImmutableSortedSet.Create(2, 1);
        string setJson = JsonSerializer.Serialize(sortedSet);
        var setCopy = JsonSerializer.Deserialize<ImmutableSortedSet<int>>(setJson);
        var sortedMap = ImmutableSortedDictionary<string, int>.Empty.Add("b", 2).Add("a", 1);
        string mapJson = JsonSerializer.Serialize(sortedMap);
        var mapCopy = JsonSerializer.Deserialize<ImmutableSortedDictionary<string, int>>(mapJson);
        IImmutableStack<int> stack = ImmutableStack<int>.Empty.Push(1).Push(2);
        string stackJson = JsonSerializer.Serialize(stack);
        var stackCopy = JsonSerializer.Deserialize<IImmutableStack<int>>(stackJson);
        DemoCheck.Expect("immutable.sorted-and-interfaces", listCopy?.SequenceEqual([1, 2]) == true &&
            setCopy?.SetEquals([1, 2]) == true && mapCopy is { Count: 2 } && mapCopy["a"] == 1 &&
            mapCopy["b"] == 2 && stackCopy?.SequenceEqual([1, 2]) == true,
            listJson + "/" + setJson + "/" + mapJson + "/" + stackJson);
    }
}

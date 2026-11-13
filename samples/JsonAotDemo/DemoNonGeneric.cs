using System.Collections;
using System.Text.Json;

internal static class DemoNonGeneric
{
    internal static void Run()
    {
        var values = new ArrayList { 1, "two", true };
        string json = JsonSerializer.Serialize(values);
        var copy = JsonSerializer.Deserialize<ArrayList>(json);
        DemoCheck.Expect("nongeneric.array-list", copy is { Count: 3 } && copy[0] is JsonElement first &&
            first.GetInt32() == 1 && copy[1] is JsonElement second && second.GetString() == "two", json);
        var queue = new Queue();
        queue.Enqueue(1);
        queue.Enqueue(2);
        string queueJson = JsonSerializer.Serialize(queue);
        var queueCopy = JsonSerializer.Deserialize<Queue>(queueJson);
        DemoCheck.Expect("nongeneric.queue", queueCopy?.Count == 2, queueJson);
        var stack = new Stack();
        stack.Push(1);
        stack.Push(2);
        string stackJson = JsonSerializer.Serialize(stack);
        var stackCopy = JsonSerializer.Deserialize<Stack>(stackJson);
        string stackResult = JsonSerializer.Serialize(stackCopy);
        DemoCheck.Expect("nongeneric.stack", stackCopy?.Count == 2, stackResult);
        var bits = new BitArray(new[] { true, false });
        string bitsJson = JsonSerializer.Serialize(bits);
        bool rejected = false;
        try { _ = JsonSerializer.Deserialize<BitArray>(bitsJson); }
        catch (NotSupportedException) { rejected = true; }
        DemoCheck.Expect("nongeneric.bit-array", rejected && bitsJson == "[true,false]", bitsJson);
    }
}

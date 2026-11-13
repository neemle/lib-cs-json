using System.Text.Json;

internal static class DemoGenerics
{
    internal static void Run()
    {
        DemoCheck.Run("generic-nested", Nested);
        DemoCheck.Run("generic-tree", Tree);
        DemoCheck.Run("generic-record", Record);
        DemoCheck.Run("generic-inheritance", Inheritance);
        DemoCheck.Run("anonymous-type", Anonymous);
    }

    private static void Nested()
    {
        var value = new GenericBox<GenericBox<int?>> { Value = new GenericBox<int?> { Value = 7 } };
        string json = JsonSerializer.Serialize(value);
        var copy = JsonSerializer.Deserialize<GenericBox<GenericBox<int?>>>(json);
        DemoCheck.Expect("generic.nested-nullable", json == "{\"Value\":{\"Value\":7}}" &&
            copy?.Value?.Value == 7, json);
    }

    private static void Tree()
    {
        var tree = new GenericTree<string> { Value = "root", Children = [new() { Value = "leaf" }] };
        string treeJson = JsonSerializer.Serialize(tree);
        var treeCopy = JsonSerializer.Deserialize<GenericTree<string>>(treeJson);
        DemoCheck.Expect("generic.recursive-tree", treeCopy is { Value: "root", Children.Count: 1 } &&
            treeCopy.Children[0].Value == "leaf", treeJson);
    }

    private static void Record()
    {
        var record = new GenericRecord<string>("Ada", 7);
        string recordJson = JsonSerializer.Serialize(record);
        var recordCopy = JsonSerializer.Deserialize<GenericRecord<string>>(recordJson);
        DemoCheck.Expect("generic.record-constructor", recordCopy == record, recordJson);
    }

    private static void Inheritance()
    {
        var derived = new GenericDerived<int> { Value = 3, Name = "Ada" };
        string derivedJson = JsonSerializer.Serialize(derived);
        var derivedCopy = JsonSerializer.Deserialize<GenericDerived<int>>(derivedJson);
        DemoCheck.Expect("generic.inheritance", derivedCopy is { Value: 3, Name: "Ada" }, derivedJson);
    }

    private static void Anonymous()
    {
        var anonymous = new { Name = "Ada", Count = 7 };
        string anonymousJson = JsonSerializer.Serialize(anonymous);
        DemoCheck.Expect("generic.anonymous-type", anonymousJson == "{\"Name\":\"Ada\",\"Count\":7}",
            anonymousJson);
    }
}

public sealed class GenericBox<T>
{
    public T? Value { get; set; }
}

public sealed class GenericTree<T>
{
    public T? Value { get; set; }
    public List<GenericTree<T>> Children { get; set; } = [];
}

public sealed record GenericRecord<T>(T Value, int Count);

public class GenericBase<T>
{
    public T? Value { get; set; }
}

public sealed class GenericDerived<T> : GenericBase<T>
{
    public string Name { get; set; } = "";
}

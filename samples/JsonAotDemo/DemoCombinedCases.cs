using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

internal static class DemoCombinedCases
{
    internal static void Run()
    {
        PolymorphicReferences();
        HeterogeneousObjects();
        OutOfOrderReferences();
        ExtensionWithJsonNode();
        PersistentReferences();
    }

    private static void PolymorphicReferences()
    {
        var child = new GraphLeaf { Name = "child", Value = 7 };
        GraphNode root = new GraphBranch { Name = "root", Children = [child, child] };
        var options = new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.Preserve };
        string json = JsonSerializer.Serialize(root, options);
        var copy = JsonSerializer.Deserialize<GraphNode>(json, options) as GraphBranch;
        DemoCheck.Expect("combined.polymorphic-references", copy?.Children.Count == 2 &&
            copy.Children[0] is GraphLeaf { Name: "child", Value: 7 } &&
            ReferenceEquals(copy.Children[0], copy.Children[1]), json);
    }

    private static void HeterogeneousObjects()
    {
        object?[] values = [1, "Ada", null, true, new AdvancedPayload { Name = "Lin" }, new int[] { 2, 3 }];
        string json = JsonSerializer.Serialize(values);
        var copy = JsonSerializer.Deserialize<object?[]>(json);
        bool valid = copy is { Length: 6 } && copy[0] is JsonElement number && number.GetInt32() == 1 &&
            copy[1] is JsonElement text && text.GetString() == "Ada" && copy[2] is null &&
            copy[3] is JsonElement boolean && boolean.GetBoolean() && copy[4] is JsonElement model &&
            model.GetProperty("Name").GetString() == "Lin" && copy[5] is JsonElement array && array.GetArrayLength() == 2;
        DemoCheck.Expect("combined.object-array", valid, json);
    }

    private static void OutOfOrderReferences()
    {
        const string json = "{\"Next\":{\"$ref\":\"1\"},\"$id\":\"1\"}";
        var relaxed = new JsonSerializerOptions
        {
            ReferenceHandler = ReferenceHandler.Preserve,
            AllowOutOfOrderMetadataProperties = true
        };
        var copy = JsonSerializer.Deserialize<CycleModel>(json, relaxed);
        bool rejected = false;
        try
        {
            _ = JsonSerializer.Deserialize<CycleModel>(json,
                new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.Preserve });
        }
        catch (JsonException) { rejected = true; }
        DemoCheck.Expect("combined.out-of-order-reference", copy is not null && ReferenceEquals(copy, copy.Next) &&
            rejected, rejected ? "preserved" : "accepted invalid order");
    }

    private static void ExtensionWithJsonNode()
    {
        const string json = "{\"Name\":\"Ada\",\"extra\":{\"n\":1},\"empty\":null}";
        var options = new JsonSerializerOptions { UnknownTypeHandling = JsonUnknownTypeHandling.JsonNode };
        var value = JsonSerializer.Deserialize<DynamicExtension>(json, options);
        string encoded = JsonSerializer.Serialize(value, options);
        bool valid = value?.Extra?["extra"] is JsonObject nested && nested["n"]?.GetValue<int>() == 1 &&
            value.Extra["empty"] is null;
        DemoCheck.Expect("combined.extension-json-node", valid && encoded == json, encoded);
    }

    private static void PersistentReferences()
    {
        var handler = new PersistentReferenceHandler();
        var options = new JsonSerializerOptions { ReferenceHandler = handler };
        var value = new CycleModel();
        string first = JsonSerializer.Serialize(value, options);
        string second = JsonSerializer.Serialize(value, options);
        var firstCopy = JsonSerializer.Deserialize<CycleModel>(first, options);
        var secondCopy = JsonSerializer.Deserialize<CycleModel>(second, options);
        DemoCheck.Expect("combined.persistent-reference-resolver", first.Contains("\"$id\":\"item-1\"") &&
            second == "{\"$ref\":\"item-1\"}" && ReferenceEquals(firstCopy, secondCopy), first + "/" + second);
    }
}

internal sealed class PersistentReferenceHandler : ReferenceHandler
{
    private readonly DemoReferenceResolver resolver = new();
    public override ReferenceResolver CreateResolver() => resolver;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(GraphBranch), "branch")]
[JsonDerivedType(typeof(GraphLeaf), "leaf")]
public abstract class GraphNode
{
    public string Name { get; set; } = "";
}

public sealed class GraphBranch : GraphNode
{
    public List<GraphNode> Children { get; set; } = [];
}

public sealed class GraphLeaf : GraphNode
{
    public int Value { get; set; }
}

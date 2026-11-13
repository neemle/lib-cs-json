using System.Text.Json.Serialization;

public sealed class ConstructorSubsetModel
{
    [JsonConstructor]
    public ConstructorSubsetModel(int count, string name = "fallback", int scale = 5)
    {
        Name = name;
        Count = count;
        Scale = scale;
    }

    public string Name { get; }
    public int Count { get; }
    public int Scale { get; }
    public string Note { get; set; } = "initial";
}

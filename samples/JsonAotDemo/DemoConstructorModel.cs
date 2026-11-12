using System.Text.Json.Serialization;

public sealed class DemoConstructorModel
{
    [JsonConstructor]
    public DemoConstructorModel(string name, int count)
    {
        Name = name;
        Count = count;
    }

    public string Name { get; }
    public int Count { get; }

    [JsonInclude]
    public int Included;
}

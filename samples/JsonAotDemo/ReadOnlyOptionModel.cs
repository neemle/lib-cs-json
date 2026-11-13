using System.Text.Json.Serialization;

public sealed class ReadOnlyOptionModel
{
    public int Skipped => 1;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int Kept => 2;

    public List<int> Items { get; } = [3];
}

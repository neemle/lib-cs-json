using System.Text.Json.Serialization;

public sealed class DirectionalIgnoreModel
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public int ReadOnlyInJson { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenReading)]
    public int WriteOnlyInJson { get; set; } = 7;
}

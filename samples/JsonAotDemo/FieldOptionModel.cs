using System.Text.Json.Serialization;

public sealed class FieldOptionModel
{
    public int Value = 3;
    public readonly int ReadOnly = 4;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public readonly int Kept = 5;

    [JsonInclude]
    public int Explicit = 7;
}

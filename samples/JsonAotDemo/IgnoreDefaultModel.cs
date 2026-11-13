using System.Text.Json.Serialization;

public sealed class IgnoreDefaultModel
{
    public int Count { get; set; }
    public Guid Id { get; set; }
    public DayOfWeek Day { get; set; }
    public PositionValue Position { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Always { get; set; }
}

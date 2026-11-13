using System.Text.Json.Serialization;

[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
public sealed class ClassNumberModeModel
{
    public int Number { get; set; }

    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public int Strict { get; set; }

    public List<int?> Values { get; set; } = [];

    public Dictionary<string, int> Map { get; set; } = [];

    public int? Maybe { get; set; }

    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public List<int> StrictValues { get; set; } = [];
}

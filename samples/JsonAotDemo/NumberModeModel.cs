using System.Text.Json.Serialization;

public sealed class NumberModeModel
{
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public int Strict { get; set; }

    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public decimal Annotated { get; set; }
}

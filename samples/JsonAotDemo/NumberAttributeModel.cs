using System.Text.Json.Serialization;

public sealed class NumberAttributeModel
{
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long Long { get; set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double Double { get; set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public decimal Decimal { get; set; }

    [JsonInclude]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public short Small;
}

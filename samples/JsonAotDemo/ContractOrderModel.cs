using System.Text.Json.Serialization;

public sealed class ContractOrderModel
{
    [JsonPropertyOrder(2)]
    public int Last { get; set; } = 3;

    [JsonPropertyOrder(-1)]
    public int First { get; set; } = 1;

    public int Middle { get; set; } = 2;
}

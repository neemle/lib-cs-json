using System.Text.Json.Serialization;

public sealed class AddressModel
{
    [JsonConverter(typeof(AddressConverter))]
    public Uri Address { get; set; } = new("https://example.com/");
}

using System.Text.Json.Serialization;

public sealed class PopulateInvalid
{
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public PopulateValue Value { get; }
}

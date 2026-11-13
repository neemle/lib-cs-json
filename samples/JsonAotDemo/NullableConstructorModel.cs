using System.Text.Json.Serialization;

public sealed class NullableConstructorModel
{
    [JsonConstructor]
    public NullableConstructorModel(string? maybe, string required)
    {
        Maybe = maybe ?? "fallback";
        Required = required;
    }

    public string Maybe { get; }
    public string Required { get; }
}

using System.Text.Json.Serialization;

public sealed class RequiredInitModel
{
    public required string Name { get; init; }
    public int Count { get; init; }

    [JsonInclude]
    public required int Code;
}

public struct RequiredInitStruct
{
    public required int Number { get; init; }

    [JsonInclude]
    public required int Code;
}

public sealed class RequiredFieldModel
{
    public required int Number;
}

public sealed class ConstructorSetsRequiredModel
{
    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public ConstructorSetsRequiredModel() => Name = "default";

    public required string Name { get; init; }

    [JsonRequired]
    public int Explicit { get; set; }
}

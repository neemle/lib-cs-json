using System.Diagnostics.CodeAnalysis;

public sealed class NullableContractModel
{
    public string Name { get; set; } = "Ada";
    public string? Note { get; set; }

    [AllowNull]
    public string AllowedNull { get; set; } = "value";

    [DisallowNull]
    public string? DeniedNull { get; set; }
}

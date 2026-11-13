using System.Text.Json.Serialization;

public sealed class EnumAttributeModel
{
    [JsonConverter(typeof(JsonStringEnumConverter<DemoPermissions>))]
    public DemoPermissions Permissions { get; set; }

    public AttributedMode Mode { get; set; }
}

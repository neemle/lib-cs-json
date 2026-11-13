using System.Text.Json.Serialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed class SkipUnmappedModel
{
    public int Known { get; set; }
}

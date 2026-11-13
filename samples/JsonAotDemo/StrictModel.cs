using System.Text.Json.Serialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StrictModel
{
    public int Known { get; set; }
}

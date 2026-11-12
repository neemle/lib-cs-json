using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class DemoLowLevel
{
    internal static void Run(string json)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("name", "Ada");
            writer.WriteNumber("count", 2);
            writer.WriteEndObject();
        }
        string written = Encoding.UTF8.GetString(stream.ToArray());
        DemoCheck.Expect("writer.encode", written == "{\"name\":\"Ada\",\"count\":2}", written);
        var reader = new Utf8JsonReader(stream.ToArray());
        bool read = reader.Read() && reader.TokenType == JsonTokenType.StartObject
            && reader.Read() && reader.GetString() == "name"
            && reader.Read() && reader.GetString() == "Ada"
            && reader.Read() && reader.GetString() == "count"
            && reader.Read() && reader.GetInt32() == 2
            && reader.Read() && reader.TokenType == JsonTokenType.EndObject && !reader.Read();
        DemoCheck.Expect("reader.decode", read, read ? "name=Ada, count=2" : "unexpected tokens");
        using JsonDocument document = JsonDocument.Parse(json);
        DemoCheck.Expect("document.decode", document.RootElement.GetProperty("Number").GetInt32() == 7,
            document.RootElement.GetRawText());
        JsonNode node = JsonNode.Parse(json) ?? throw new InvalidOperationException("Missing JSON node.");
        node["Number"] = 9;
        string updated = node.ToJsonString();
        using JsonDocument updatedDocument = JsonDocument.Parse(updated);
        DemoCheck.Expect("node.encode-decode", updatedDocument.RootElement.GetProperty("Number").GetInt32() == 9,
            updated);
    }
}
